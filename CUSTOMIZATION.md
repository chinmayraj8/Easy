# Easy: what is inherited and what is new

Easy is a customized build of [Filee](https://github.com/KnifeLemon/Filee) by Myeonggu Kim (KnifeLemon) and Filee
contributors, used under the MIT license (see `LICENSE`, which keeps the original notice). Easy is not affiliated with
or endorsed by the Filee project. Bundled third-party components keep their own licenses: `THIRD-PARTY-NOTICES.md`
(also shown on the About page).

## Inherited from Filee (unchanged)

Everything not listed below: the Avalonia app, donut toolbar, conversion engines (`src/Filee.Engines`), routing and job
queue (`src/Filee.Core`), platform layers, CLI, tests and build scripts. Internal namespaces and project names stay
`Filee.*` so upstream fixes can still be merged.

## Changed for Easy

| Change | Files |
|---|---|
| Name "Easy" in the UI, English only (other languages removed, language picker hidden) | `src/Filee.App/Assets/i18n/en.json`, `Services/LocalizationService.cs`, `Views/Pages/GeneralPage.axaml` |
| Attribution line on the About page; upstream GitHub, sponsor and update sections removed | `Views/Pages/AboutPage.axaml(.cs)`, `Views/MainWindow.axaml(.cs)` |
| No update checks, no "star us on GitHub" notice | `App.axaml.cs` |
| Product, author, copyright, repository URL | `Directory.Build.props`, `Services/UpdateService.cs` (fallback URL) |
| macOS identity `com.chinmayraj8.easy`, bundle `Easy.app`, Services menu "Convert with Easy" | `installer/macos/*`, `src/Filee.Platform.MacOS/*`, `build/build-portable.ps1`, `build/smoke-portable.ps1` |
| Own settings folder (`~/Library/Application Support/Easy`) and single-instance name | `src/Filee.Core/Settings/UserDataStore.cs`, `Services/SingleInstance.cs` |
| Teal accent `#14B8A6` by default | `src/Filee.Core/Settings/AppSettings.cs`, `Services/ThemeService.cs` |
| **New engine: ONLYOFFICE.** Word (DOCX, DOC, RTF, ODT), PowerPoint (PPTX) and Excel (XLSX) → PDF keeping the original layout, fonts and spacing, and PDF → DOCX rebuilt as a real document: ONLYOFFICE reads the PDF line by line (each line at its position, with its fonts) and `PdfLayoutDocx` turns the lines into paragraphs (indents, alignment, exact line pitch and spacing measured; paragraphs continue across pages), tables with one value per cell and borders from the rule lines (ruled grids with multi-line cells, icons, cell shading and bullet lists), two-column pages (also under a title block), list bullets (including Symbol-font ones), code kept line by line, bold and italic from the PDF's fonts, ligatures as letters, running headers and footers with PAGE/NUMPAGES fields, headings, sub- and superscripts, and optional hyphens where the PDF broke words; the text column comes from the PDF's glyphs (drag "docx-writer" up in Settings → Engines for the built-in, less faithful DOCX), using ONLYOFFICE Desktop Editors installed on the Mac (its `x2t` converter; nothing of it is shipped). Preferred over the built-in DOCX/PPTX → HWPX → PDF route, which re-lays out the document. Unlike upstream Filee's rule, this uses an application installed on the system. | `src/Filee.Engines/Office/OnlyOfficeConverter.cs`, `PdfLayoutDocx.cs`, `EngineRegistry.cs`, `en.json`, `tests/Filee.Engines.Tests/OnlyOfficeConverterTests.cs`, `PdfLayoutDocxTests.cs`, `EngineFixture.cs` |
| **DOCX → PDF fidelity record and checks.** Each conversion logs the renderer and its exact version (the PDF's producer), the export settings, the fonts the text actually uses (resolved through styles and theme) against those ONLYOFFICE has, and glyph fallbacks; substitutions put a notice on the file. `PdfFidelity` compares an output with a reference PDF: normalized text and numbers, pagination, page and image boxes (also images painted as patterns), and per-page visual overlays that tell antialiasing from shifted, missing or extra content. Opt-in run on any document: see `tests/Filee.Engines.Tests/Fidelity/DocxPdfFidelityTests.cs`. | `src/Filee.Engines/Office/WordFonts.cs`, `OnlyOfficeConverter.cs`, `en.json`, `tests/Filee.Engines.Tests/Fidelity/*` |
| Tests updated for the above, plus rebrand checks | `tests/**` |

## Known leftovers (not yet changed)

- The executable inside the bundle and the CLI are still named `Filee` / `filee` (assembly name, `avares://Filee/`
  resource URIs, CLI scripts).
- The icon is still Filee's.
- Downloaded engines still go to `.../Filee/engines` (shared with the Windows installer script).
- DOCX → PDF fidelity is measured against ONLYOFFICE (the renderer itself), not Microsoft Word, which isn't installed: no Word-fidelity claim. ONLYOFFICE fits table columns to their text when cells have no w:tcW (the grid is ignored).
- PDF → DOCX: a rebuilt document reflows, so pages can differ by a line or two (a PDF doesn't record hyphenation settings such as the hyphenation zone); equations become text with sub- and superscripts, not editable equation objects. A font the Mac doesn't have is substituted, so its text wraps differently. Poster-like designs (text over pictures, white text on a banner drawn as an image) are not rebuilt faithfully. Checked on a sweep of real PDFs (papers, résumés, job descriptions, cheat sheets, templates) with `Supplied_pdf_round_trip`.
- Windows installer (`installer/Filee.iss`, `build/tools/make-installer-engines.cs`) still expects Korean and Chinese
  files and Filee branding; Easy targets macOS only.
