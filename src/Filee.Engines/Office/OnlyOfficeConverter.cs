// Word, PowerPoint and Excel files → PDF, and PDF → DOCX, with the converter inside an installed ONLYOFFICE Desktop
// Editors (macOS).
// Easy addition (not in upstream Filee): the built-in route DOCX/PPTX → HWPX → PDF (rhwp) lays the document out again
// in another engine, so fonts, letter spacing and line breaks change. ONLYOFFICE's x2t renders with the same engine its
// editor displays the file with. It is a separate program (AGPL-3.0) the user installed; Easy only runs it and ships
// none of it. x2t reads a small task file; it needs the font list (AllFonts.js) the editor writes on its first start.

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace Filee.Engines.Office;

/// <summary>DOCX, DOC, RTF, ODT, PPTX, XLSX and their siblings → PDF through ONLYOFFICE's x2t.</summary>
public sealed class OnlyOfficeConverter(ILogger<OnlyOfficeConverter>? log = null) : IConverter
{
    private readonly ILogger _log = log ?? NullLogger<OnlyOfficeConverter>.Instance;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>x2t's format ids (AVS_OFFICESTUDIO_FILE_CROSSPLATFORM_PDF, AVS_OFFICESTUDIO_FILE_DOCUMENT_DOCX).</summary>
    private static readonly Dictionary<string, int> Formats = new() { ["pdf"] = 0x0201, ["docx"] = 0x0041 };

    /// <summary>
    /// How x2t groups a PDF's text into a DOCX (m_oTextParams/m_nTextAssociationType). Measured on a 10-page paper:
    /// 0 and 1 dropped almost all text; 3 (a box per paragraph) mixed boxes with loosely placed paragraphs and stored
    /// table columns as single boxes; 2 gives every line as a box at its exact position with its fonts, and a table's
    /// values one by one, which <see cref="PdfLayoutDocx"/> turns into paragraphs, tables and headers.
    /// </summary>
    internal const int LineBoxes = 2;

    public string Id => "onlyoffice";
    public string DisplayName => "ONLYOFFICE";

    /// <summary>Each run is a separate x2t process of a few hundred MB: two at a time keep the Mac responsive.</summary>
    public int MaxParallelism => 2;

    /// <summary>
    /// One direct edge per format to PDF, cheaper than the two-step HWPX route (and preferred over it), and PDF → DOCX.
    /// That one costs 16: at the default priority the built-in DOCX writer (8 + its PDF penalty 6) wins, since x2t drops
    /// links, rotated text and a scan's OCR text layer, which the writer keeps (tests/conversion-audit on macOS: 5 of 10
    /// against 10 of 10). Moving "onlyoffice" to the top of Settings → Engines switches to it. Every edge is covered by
    /// OnlyOfficeConverterTests.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. new[] { "docx", "doc", "rtf", "odt", "pptx", "xlsx" }.Select(from => new ConversionEdge(from, "pdf")),
        new("pdf", "docx", Cost: 16),
    ];

    /// <summary>Applications folders searched for ONLYOFFICE.app (tests point it elsewhere).</summary>
    internal static IReadOnlyList<string> ApplicationFolders { get; set; } =
        ["/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")];

    /// <summary>Folder with the editor's font list, written when ONLYOFFICE starts for the first time.</summary>
    internal static string FontFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "asc.onlyoffice.ONLYOFFICE", "data", "fonts");

    public EngineStatus GetStatus()
    {
        if (Locate() is not { } app)
            return EngineStatus.Unavailable("engine.reason.not_installed");
        if (!File.Exists(Path.Combine(FontFolder, "AllFonts.js")))
            return EngineStatus.Unavailable("engine.reason.not_installed", "Open ONLYOFFICE once so it lists the fonts.");
        return EngineStatus.Available(app.Converter, Version(app.Converter));
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var app = Locate() ?? throw new InvalidOperationException("ONLYOFFICE is not installed.");
        // x2t turns a password-protected PDF into an empty DOCX and exits 0; the rebuild below then fails and that empty
        // file was kept as the result.
        if (step.From == "pdf")
        {
            try { PdfDocument.Open(step.InputPath).Dispose(); }
            catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
            {
                throw new InvalidOperationException("The PDF is password-protected. Remove the password, then convert it.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A PDF PdfPig can't parse is still x2t's to try, as before.
            }
        }
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        // x2t gets ASCII paths in a private folder: the user's file is copied, never opened in place.
        var scratch = Path.Combine(step.WorkDirectory, "onlyoffice-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var input = Path.Combine(scratch, "in." + step.From);
        var target = Path.Combine(scratch, "out." + step.To);
        // Not File.Copy: .NET takes an advisory lock to read, and a document open in ONLYOFFICE is locked by it, so the
        // copy failed with "being used by another process". cp reads without locking.
        var copy = await ProcessRunner.RunAsync("/bin/cp", [step.InputPath, input], TimeSpan.FromMinutes(1), cancellationToken);
        if (copy.ExitCode != 0)
            throw new IOException($"Couldn't read {step.InputPath}: {copy.StandardError.Trim()}");
        var task = Path.Combine(scratch, "task.xml");
        TaskFile(input, target, step.To, app.Themes).Save(task);

        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(2)))
            result = await ProcessRunner.RunAsync(app.Converter, [task], Timeout, cancellationToken,
                workingDirectory: Path.GetDirectoryName(app.Converter));

        if (result.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
            throw new InvalidOperationException(
                $"ONLYOFFICE failed (exit {result.ExitCode}). {result.StandardError.Trim()} {result.StandardOutput.Trim()}".Trim());
        if (step.From == "pdf")
        {
            // Paragraphs, tables and headers instead of a text box per line; fonts back to the PDF's (or equivalents).
            // The rebuild works on a copy: if it fails, x2t's own DOCX (every line a positioned box) is kept, with all
            // its content, rather than no document at all.
            var rebuilt = Path.Combine(scratch, "rebuilt.docx");
            File.Copy(target, rebuilt);
            try
            {
                var fontNotes = PdfLayoutDocx.Rebuild(rebuilt, PdfLayoutDocx.TextColumn(input), PdfLayoutDocx.Glyphs(input),
                    WordFonts.Installed(Path.Combine(FontFolder, "AllFonts.js")));
                foreach (var note in fontNotes)
                    _log.LogInformation("PDF → DOCX font: {Note}", note);
                File.Move(rebuilt, target, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Couldn't rebuild paragraphs and tables from {Input}; kept the line-by-line DOCX", step.InputPath);
                step.Notice?.Invoke("notice.pdf_layout_kept");
            }
        }
        if (step.From == "docx" && step.To == "pdf")
            Record(input, target, app.Converter, step.Notice);
        File.Move(target, output, overwrite: true);
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// What a DOCX → PDF conversion depended on, in the log, so a result can be reproduced and judged: the renderer and
    /// its exact version (the PDF's producer), the export settings, the fonts the text asks for against those ONLYOFFICE
    /// found, and glyphs drawn with a font the document never asked for (a character its font lacks). Missing fonts or
    /// fallbacks also put a notice on the file. Never fails the conversion: the PDF is already written.
    /// </summary>
    private void Record(string docx, string pdf, string converter, Action<string>? notice)
    {
        try
        {
            var report = CheckFonts(docx, pdf, Path.Combine(FontFolder, "AllFonts.js"));
            string producer;
            using (var document = PdfDocument.Open(pdf))
                producer = document.Information.Producer ?? "unknown";
            _log.LogInformation(
                "DOCX → PDF with {Producer} ({Converter}, bundle {Version}): x2t format 0x0201 (PDF), PDF/A off, fonts embedded " +
                "as subsets, font list {FontList}. Fonts requested: {Requested}. Missing (substituted): {Missing}. Glyph fallbacks: {Fallbacks}",
                producer, converter, Version(converter), Path.Combine(FontFolder, "AllFonts.js"),
                string.Join(", ", report.Requested.Select(r => $"{r.Font} ({r.Characters} chars)")),
                report.Missing.Count == 0 ? "none" : string.Join(", ", report.Missing.Select(m => $"{m.Font} ({m.Characters} chars)")),
                report.Fallbacks.Count == 0 ? "none" : string.Join(", ", report.Fallbacks.Select(f => $"{f.Key} for \"{f.Value}\"")));
            if (report.Missing.Count > 0 || report.Fallbacks.Count > 0)
                notice?.Invoke("notice.fonts_substituted");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Couldn't check the fonts of a DOCX → PDF conversion");
        }
    }

    /// <summary>Fonts a DOCX's text asks for, those the font list lacks, and fonts the PDF drew with that weren't asked for.</summary>
    internal static FontCheck CheckFonts(string docx, string pdf, string allFontsJs)
    {
        var requested = WordFonts.Requested(docx);
        var installed = WordFonts.Installed(allFontsJs);
        var missing = requested.Where(r => !installed.Contains(r.Font)).ToList();
        var asked = requested.Select(r => r.Font).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbacks = WordFonts.Drawn(pdf).Where(d => !asked.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value);
        return new(requested, missing, fallbacks);
    }

    /// <summary>The result of <see cref="CheckFonts"/>: fonts with the characters set in them, and fallback glyphs.</summary>
    internal sealed record FontCheck(
        IReadOnlyList<(string Font, int Characters)> Requested,
        IReadOnlyList<(string Font, int Characters)> Missing,
        IReadOnlyDictionary<string, string> Fallbacks);

    /// <summary>
    /// The task x2t reads: source, target, target format and where its fonts and themes are; for a PDF source, how its
    /// text is grouped (<see cref="LineBoxes"/>).
    /// </summary>
    internal static XDocument TaskFile(string input, string output, string to, string themes) => new(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("TaskQueueDataConvert",
            new XElement("m_sFileFrom", input),
            new XElement("m_sFileTo", output),
            new XElement("m_nFormatTo", Formats[to]),
            new XElement("m_sAllFontsPath", Path.Combine(FontFolder, "AllFonts.js")),
            new XElement("m_sFontDir", FontFolder),
            new XElement("m_sThemeDir", themes),
            new XElement("m_bIsNoBase64", true),
            input.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? new XElement("m_oTextParams", new XElement("m_nTextAssociationType", LineBoxes))
                : null));

    /// <summary>
    /// The app's version from ONLYOFFICE.app/Contents/Info.plist (an XML property list), for Settings → Engines;
    /// "installed" when it can't be read.
    /// </summary>
    internal static string Version(string converter)
    {
        try
        {
            var plist = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(converter)!, "..", "..", "Info.plist"));
            var keys = XDocument.Load(plist).Descendants("key").ToList();
            var key = keys.FirstOrDefault(k => k.Value == "CFBundleShortVersionString");
            if (key?.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "string" } value && value.Value.Length > 0)
                return value.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
        }
        return "installed";
    }

    /// <summary>x2t and the presentation themes inside ONLYOFFICE.app, or null when it isn't installed.</summary>
    internal static (string Converter, string Themes)? Locate()
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        foreach (var folder in ApplicationFolders)
        {
            var resources = Path.Combine(folder, "ONLYOFFICE.app", "Contents", "Resources");
            var converter = Path.Combine(resources, "converter", "x2t");
            if (File.Exists(converter))
                return (converter, Path.Combine(resources, "editors", "sdkjs", "slide", "themes"));
        }
        return null;
    }
}
