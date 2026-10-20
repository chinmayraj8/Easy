// Merges Word (DOCX) or PowerPoint (PPTX) files into one by copying package parts, not through another format, so
// styles, lists, pictures, charts, headers and slide layouts come along as they are. The first file is the base.
//
// DOCX: each next file's body is appended as new sections that keep their own page setup, headers and footers. Styles
// the base lacks are added (a style both have keeps the base's look, as with Word's Insert → Text from File), lists are
// renumbered so they don't continue the base's, footnotes and endnotes come along, comments are dropped (their markers).
// PPTX: every slide of the next files is appended with its layout, master and theme; the base keeps its slide size and
// loses its sections. Speaker notes and comments of the appended slides are dropped.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office;

internal static class OoxmlMerger
{
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string RelTypes = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    /// <summary>Writes <paramref name="inputs"/> (all DOCX, or all PPTX: <paramref name="format"/>) as one file.</summary>
    public static void Merge(IReadOnlyList<string> inputs, string output, string format)
    {
        var target = Package.Load(inputs[0]);
        foreach (var input in inputs.Skip(1))
        {
            var source = Package.Load(input);
            if (format == "pptx")
                AppendSlides(target, source);
            else
                AppendDocument(target, source);
        }
        target.Save(output);
    }

    // ───────── DOCX ─────────

    private static void AppendDocument(Package target, Package source)
    {
        var targetPart = target.MainPart;
        var sourcePart = source.MainPart;
        var targetRoot = target.Xml(targetPart).Root!;
        var sourceRoot = source.Xml(sourcePart).Root!;
        var body = targetRoot.Element(W + "body")!;
        var content = sourceRoot.Element(W + "body")!.Elements().Select(e => new XElement(e)).ToList();
        var copier = new PartCopier(source, target, _ => true);

        // The base's last section ends before the appended file, which so starts on a new page with its own setup.
        if (body.Elements(W + "sectPr").LastOrDefault() is { } last)
        {
            last.Remove();
            body.Add(new XElement(W + "p", new XElement(W + "pPr", last)));
        }
        else
        {
            body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
        }

        foreach (var marker in content.SelectMany(e => e.DescendantsAndSelf())
                     .Where(e => e.Name == W + "commentRangeStart" || e.Name == W + "commentRangeEnd" || e.Name == W + "commentReference")
                     .ToList())
            marker.Remove();

        var lists = MergeNumbering(target, targetPart, source, sourcePart, copier);
        RenumberLists(content, lists);
        MergeStyles(target, targetPart, source, sourcePart, lists);
        MergeNotes(content, "footnote", target, targetPart, source, sourcePart, copier);
        MergeNotes(content, "endnote", target, targetPart, source, sourcePart, copier);
        Offset(body, content, [W + "bookmarkStart", W + "bookmarkEnd"], W + "id");
        Offset(body, content, [Wp + "docPr"], "id");
        RemapRelationships(content, source, sourcePart, target, targetPart, copier);
        CopyNamespaces(sourceRoot, targetRoot);
        body.Add(content);
    }

    /// <summary>Adds the source's lists to the base's numbering; returns old → new list ids (empty: ids unchanged).</summary>
    private static Dictionary<string, string> MergeNumbering(Package target, string targetPart, Package source, string sourcePart, PartCopier copier)
    {
        var map = new Dictionary<string, string>();
        if (source.TargetOfType(sourcePart, "/numbering") is not { } from)
            return map;
        if (target.TargetOfType(targetPart, "/numbering") is not { } into)
        {
            // Nothing to clash with: the part comes as it is.
            target.AddRelationship(targetPart, RelTypes + "numbering", copier.Copy(from), external: false);
            return map;
        }

        var intoRoot = target.Xml(into).Root!;
        var fromRoot = source.Xml(from).Root!;
        var abstractOffset = intoRoot.Elements(W + "abstractNum").Select(a => (int?)a.Attribute(W + "abstractNumId") ?? 0).DefaultIfEmpty(-1).Max() + 1;
        var nextNum = intoRoot.Elements(W + "num").Select(n => (int?)n.Attribute(W + "numId") ?? 0).DefaultIfEmpty(0).Max() + 1;

        var abstracts = fromRoot.Elements(W + "abstractNum").Select(a =>
        {
            var copy = new XElement(a);
            copy.SetAttributeValue(W + "abstractNumId", ((int?)a.Attribute(W + "abstractNumId") ?? 0) + abstractOffset);
            // A shared nsid makes Word continue the base's list; picture bullets point at the base's pictures.
            copy.Element(W + "nsid")?.Remove();
            copy.Descendants(W + "lvlPicBulletId").Remove();
            return copy;
        }).ToList();
        var nums = fromRoot.Elements(W + "num").Select(n =>
        {
            var copy = new XElement(n);
            map[(string?)n.Attribute(W + "numId") ?? ""] = nextNum.ToString();
            copy.SetAttributeValue(W + "numId", nextNum++);
            if (copy.Element(W + "abstractNumId") is { } link)
                link.SetAttributeValue(W + "val", ((int?)link.Attribute(W + "val") ?? 0) + abstractOffset);
            return copy;
        }).ToList();

        // Schema order: numPicBullet*, abstractNum*, num*, numIdMacAtCleanup?
        var cleanup = intoRoot.Element(W + "numIdMacAtCleanup");
        if (intoRoot.Elements(W + "abstractNum").LastOrDefault() is { } lastAbstract)
            lastAbstract.AddAfterSelf(abstracts);
        else if ((intoRoot.Element(W + "num") ?? cleanup) is { } before)
            before.AddBeforeSelf(abstracts);
        else
            intoRoot.Add(abstracts);
        if (cleanup is not null)
            cleanup.AddBeforeSelf(nums);
        else
            intoRoot.Add(nums);
        return map;
    }

    private static void RenumberLists(IEnumerable<XElement> elements, Dictionary<string, string> lists)
    {
        foreach (var numId in elements.SelectMany(e => e.DescendantsAndSelf(W + "numId")))
            if (lists.TryGetValue((string?)numId.Attribute(W + "val") ?? "", out var renumbered))
                numId.SetAttributeValue(W + "val", renumbered);
    }

    /// <summary>Adds the source's styles the base doesn't have (by id).</summary>
    private static void MergeStyles(Package target, string targetPart, Package source, string sourcePart, Dictionary<string, string> lists)
    {
        if (source.TargetOfType(sourcePart, "/styles") is not { } from || target.TargetOfType(targetPart, "/styles") is not { } into)
            return;
        var intoRoot = target.Xml(into).Root!;
        var have = intoRoot.Elements(W + "style").Select(s => (string?)s.Attribute(W + "styleId")).ToHashSet();
        var added = source.Xml(from).Root!.Elements(W + "style")
            .Where(s => !have.Contains((string?)s.Attribute(W + "styleId")))
            .Select(s => new XElement(s))
            .ToList();
        RenumberLists(added, lists);
        intoRoot.Add(added);
    }

    /// <summary>Copies the footnotes (or endnotes) the content refers to, with new ids.</summary>
    private static void MergeNotes(List<XElement> content, string kind, Package target, string targetPart, Package source, string sourcePart, PartCopier copier)
    {
        var references = content.SelectMany(e => e.DescendantsAndSelf(W + $"{kind}Reference")).ToList();
        if (references.Count == 0 || source.TargetOfType(sourcePart, $"/{kind}s") is not { } from)
            return;
        if (target.TargetOfType(targetPart, $"/{kind}s") is not { } into)
        {
            target.AddRelationship(targetPart, RelTypes + kind + "s", copier.Copy(from), external: false);
            return;
        }

        var notes = new Dictionary<string, XElement>();
        foreach (var note in source.Xml(from).Root!.Elements(W + kind))
            notes.TryAdd((string?)note.Attribute(W + "id") ?? "", note);
        var intoRoot = target.Xml(into).Root!;
        var next = intoRoot.Elements(W + kind).Select(n => (int?)n.Attribute(W + "id") ?? 0).DefaultIfEmpty(0).Max() + 1;
        var copied = new List<XElement>();
        foreach (var reference in references)
        {
            if (!notes.TryGetValue((string?)reference.Attribute(W + "id") ?? "", out var note))
                continue;
            var copy = new XElement(note);
            copy.SetAttributeValue(W + "id", next);
            reference.SetAttributeValue(W + "id", next++);
            copied.Add(copy);
        }
        RemapRelationships(copied, source, from, target, into, copier);
        intoRoot.Add(copied);
    }

    /// <summary>Moves the content's ids (bookmarks, drawings) past the base's, so none is used twice.</summary>
    private static void Offset(XElement body, List<XElement> content, XName[] elements, XName attribute)
    {
        var offset = elements.SelectMany(body.Descendants).Select(e => (long?)e.Attribute(attribute) ?? 0).DefaultIfEmpty(0).Max() + 1;
        foreach (var element in elements.SelectMany(name => content.SelectMany(c => c.DescendantsAndSelf(name))))
            if ((long?)element.Attribute(attribute) is { } id)
                element.SetAttributeValue(attribute, id + offset);
    }

    /// <summary>Declares the source root's namespaces on the base root, so copied content keeps its prefixes.</summary>
    private static void CopyNamespaces(XElement from, XElement into)
    {
        foreach (var declaration in from.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns))
            if (into.GetNamespaceOfPrefix(declaration.Name.LocalName) is null)
                into.Add(new XAttribute(declaration));
        var ignorable = Mc + "Ignorable";
        var prefixes = ((string?)into.Attribute(ignorable) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Union(((string?)from.Attribute(ignorable) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(prefix => into.GetNamespaceOfPrefix(prefix) is not null)
            .ToList();
        if (prefixes.Count > 0)
            into.SetAttributeValue(ignorable, string.Join(' ', prefixes));
    }

    // ───────── PPTX ─────────

    private static void AppendSlides(Package target, Package source)
    {
        var presentationPart = target.MainPart;
        var presentation = target.Xml(presentationPart).Root!;
        var sourcePart = source.MainPart;
        var copier = new PartCopier(source, target, type =>
            !type.EndsWith("/notesSlide", StringComparison.Ordinal) && !type.EndsWith("/comments", StringComparison.Ordinal));

        // ponytail: sections dropped (the new slides would be in none, which PowerPoint repairs); add one per file if missed.
        foreach (var sections in presentation.Descendants(P14 + "sectionLst").Select(s => s.Parent).OfType<XElement>().ToList())
            sections.Remove();

        var slides = presentation.Element(P + "sldIdLst");
        if (slides is null)
        {
            slides = new XElement(P + "sldIdLst");
            (presentation.Element(P + "handoutMasterIdLst") ?? presentation.Element(P + "notesMasterIdLst") ?? presentation.Element(P + "sldMasterIdLst"))!
                .AddAfterSelf(slides);
        }
        var nextSlide = slides.Elements(P + "sldId").Select(s => (long?)s.Attribute("id") ?? 0).Append(255).Max() + 1;
        foreach (var slide in source.Xml(sourcePart).Root!.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            if (source.Target(sourcePart, (string?)slide.Attribute(R + "id")) is not { } part)
                continue;
            var id = target.AddRelationship(presentationPart, RelTypes + "slide", copier.Copy(part), external: false);
            slides.Add(new XElement(P + "sldId", new XAttribute("id", nextSlide++), new XAttribute(R + "id", id)));
        }

        // The masters that came with the slides join the presentation; they and their layouts take ids after every id
        // in use (master and layout ids share one range).
        var masters = presentation.Element(P + "sldMasterIdLst")!;
        var nextId = masters.Elements(P + "sldMasterId").Select(m => (long?)m.Attribute("id") ?? 0)
            .Concat(target.Relationships(presentationPart).Where(r => r.Type.EndsWith("/slideMaster", StringComparison.Ordinal))
                .SelectMany(r => target.Xml(r.Target).Root!.Element(P + "sldLayoutIdLst")?.Elements(P + "sldLayoutId") ?? [])
                .Select(l => (long?)l.Attribute("id") ?? 0))
            .Append(2147483647)
            .Max() + 1;
        foreach (var (from, to) in copier.Copied)
        {
            if (source.ContentType(from)?.EndsWith("slideMaster+xml", StringComparison.Ordinal) != true)
                continue;
            var id = target.AddRelationship(presentationPart, RelTypes + "slideMaster", to, external: false);
            masters.Add(new XElement(P + "sldMasterId", new XAttribute("id", nextId++), new XAttribute(R + "id", id)));
            foreach (var layout in target.Xml(to).Root!.Element(P + "sldLayoutIdLst")?.Elements(P + "sldLayoutId") ?? [])
                layout.SetAttributeValue("id", nextId++);
        }
    }

    // ───────── Parts and relationships ─────────

    /// <summary>
    /// Points the content's relationship ids (pictures, links, headers, charts, ...) at copies of their targets in the
    /// base, related from <paramref name="targetPart"/>.
    /// </summary>
    private static void RemapRelationships(IEnumerable<XElement> content, Package source, string sourcePart, Package target, string targetPart, PartCopier copier)
    {
        var relationships = new Dictionary<string, Relationship>();
        foreach (var relationship in source.Relationships(sourcePart))
            relationships.TryAdd(relationship.Id, relationship);
        var mapped = new Dictionary<string, string>();
        foreach (var attribute in content.SelectMany(e => e.DescendantsAndSelf()).SelectMany(e => e.Attributes()).Where(a => a.Name.Namespace == R).ToList())
        {
            if (!mapped.TryGetValue(attribute.Value, out var id))
            {
                if (!relationships.TryGetValue(attribute.Value, out var relationship) || (!relationship.External && !source.Exists(relationship.Target)))
                    continue;
                id = target.AddRelationship(targetPart, relationship.Type,
                    relationship.External ? relationship.Target : copier.Copy(relationship.Target), relationship.External);
                mapped[attribute.Value] = id;
            }
            attribute.Value = id;
        }
    }

    private readonly record struct Relationship(string Id, string Type, string Target, bool External);

    /// <summary>Copies parts with everything they relate to (once each), under names free in the target.</summary>
    private sealed class PartCopier(Package from, Package to, Func<string, bool> follow)
    {
        /// <summary>Source part → its copy.</summary>
        public Dictionary<string, string> Copied { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Copy(string part)
        {
            if (Copied.TryGetValue(part, out var copied))
                return copied;
            var copy = to.FreeName(part);
            Copied[part] = copy; // before the relationships: a layout and its master point at each other
            to.Add(copy, from.Bytes(part), from.ContentType(part));
            foreach (var relationship in from.Relationships(part))
            {
                if (!relationship.External && (!follow(relationship.Type) || !from.Exists(relationship.Target)))
                    continue;
                var target = relationship.External ? relationship.Target : Package.Relative(copy, Copy(relationship.Target));
                to.Rels(copy).Add(new XElement(Rel + "Relationship",
                    new XAttribute("Id", relationship.Id), new XAttribute("Type", relationship.Type), new XAttribute("Target", target),
                    relationship.External ? new XAttribute("TargetMode", "External") : null));
            }
            return copy;
        }
    }

    /// <summary>An OPC package held in memory: parts as bytes, or as XML once read.</summary>
    private sealed class Package
    {
        private const string ContentTypesPart = "[Content_Types].xml";
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, XDocument> _xml = new(StringComparer.OrdinalIgnoreCase);

        public static Package Load(string path)
        {
            var package = new Package();
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                package._bytes[entry.FullName] = memory.ToArray();
            }
            return package;
        }

        public string MainPart => TargetOfType("", "/officeDocument") ?? throw new InvalidDataException("Not an Office Open XML package.");

        public bool Exists(string part) => _bytes.ContainsKey(part) || _xml.ContainsKey(part);

        public byte[] Bytes(string part)
        {
            if (!_xml.TryGetValue(part, out var xml))
                return _bytes[part];
            using var memory = new MemoryStream();
            xml.Save(memory, SaveOptions.DisableFormatting);
            return memory.ToArray();
        }

        public XDocument Xml(string part)
        {
            if (!_xml.TryGetValue(part, out var xml))
            {
                using var stream = new MemoryStream(_bytes[part]);
                _xml[part] = xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                _bytes.Remove(part);
            }
            return xml;
        }

        public string? ContentType(string part)
        {
            var types = Xml(ContentTypesPart).Root!;
            var extension = Path.GetExtension(part).TrimStart('.');
            return (string?)types.Elements(Ct + "Override")
                       .FirstOrDefault(o => string.Equals((string?)o.Attribute("PartName"), "/" + part, StringComparison.OrdinalIgnoreCase))
                       ?.Attribute("ContentType")
                   ?? (string?)types.Elements(Ct + "Default")
                       .FirstOrDefault(d => string.Equals((string?)d.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase))
                       ?.Attribute("ContentType");
        }

        public void Add(string part, byte[] bytes, string? contentType)
        {
            _bytes[part] = bytes;
            if (contentType is not null && ContentType(part) != contentType)
                Xml(ContentTypesPart).Root!.Add(new XElement(Ct + "Override",
                    new XAttribute("PartName", "/" + part), new XAttribute("ContentType", contentType)));
        }

        /// <summary>A name like <paramref name="part"/> that no part has yet (image1.png → image1_2.png).</summary>
        public string FreeName(string part)
        {
            if (!Exists(part))
                return part;
            var extension = Path.GetExtension(part);
            var stem = part[..^extension.Length];
            for (var n = 2; ; n++)
                if (!Exists($"{stem}_{n}{extension}"))
                    return $"{stem}_{n}{extension}";
        }

        /// <summary>The Relationships element of a part ("" = the package), created when it has none.</summary>
        public XElement Rels(string part)
        {
            var path = RelsPath(part);
            if (!Exists(path))
                _xml[path] = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Rel + "Relationships"));
            return Xml(path).Root!;
        }

        public IEnumerable<Relationship> Relationships(string part) =>
            !Exists(RelsPath(part)) ? [] : Rels(part).Elements(Rel + "Relationship").Select(r =>
            {
                var target = (string?)r.Attribute("Target") ?? "";
                var external = (string?)r.Attribute("TargetMode") == "External";
                return new Relationship((string?)r.Attribute("Id") ?? "", (string?)r.Attribute("Type") ?? "",
                    external ? target : OpcPackage.ResolvePath(Folder(part), target), external);
            }).ToList();

        public string? Target(string part, string? id) =>
            Relationships(part).FirstOrDefault(r => r.Id == id && !r.External).Target;

        public string? TargetOfType(string part, string typeSuffix) =>
            Relationships(part).FirstOrDefault(r => !r.External && r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)).Target;

        /// <summary>Relates <paramref name="part"/> to <paramref name="target"/> (a part path, or a URI when external); returns the new id.</summary>
        public string AddRelationship(string part, string type, string target, bool external)
        {
            var rels = Rels(part);
            var ids = rels.Elements(Rel + "Relationship").Select(r => (string?)r.Attribute("Id")).ToHashSet();
            var n = ids.Count + 1;
            while (ids.Contains($"rId{n}"))
                n++;
            rels.Add(new XElement(Rel + "Relationship",
                new XAttribute("Id", $"rId{n}"), new XAttribute("Type", type),
                new XAttribute("Target", external ? target : Relative(part, target)),
                external ? new XAttribute("TargetMode", "External") : null));
            return $"rId{n}";
        }

        public void Save(string path)
        {
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var part in _xml.Keys.Concat(_bytes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p != ContentTypesPart))
            {
                using var stream = zip.CreateEntry(part, CompressionLevel.Optimal).Open();
                stream.Write(Bytes(part));
            }
        }

        /// <summary>Path of <paramref name="to"/> relative to the folder of <paramref name="from"/>.</summary>
        public static string Relative(string from, string to)
        {
            var folder = Folder(from).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var target = to.Split('/');
            var common = 0;
            while (common < folder.Length && common < target.Length - 1 && string.Equals(folder[common], target[common], StringComparison.OrdinalIgnoreCase))
                common++;
            return string.Join('/', Enumerable.Repeat("..", folder.Length - common).Concat(target.Skip(common)));
        }

        private static string RelsPath(string part)
        {
            var slash = part.LastIndexOf('/');
            return $"{part[..(slash + 1)]}_rels/{part[(slash + 1)..]}.rels";
        }

        private static string Folder(string part) => part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
    }
}
