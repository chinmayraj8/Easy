# Conversion audit report — 2026-11-01

Scope: the document and image pathways (DOCX ↔ PDF, PDF → images, PNG / JPG / WebP between each other), checked
end to end through the real `filee` command line and through the app itself, plus the xUnit suites.

**Where it ran.** A Linux container (Ubuntu 24.04, .NET 10.0.114, LibreOffice 24.2 with `libreoffice-math`). It can't
run macOS frameworks, so two macOS-only pieces were **not exercised**: ONLYOFFICE's x2t (the DOCX → PDF and PDF → DOCX
engine on a Mac that has ONLYOFFICE installed) and Apple Vision (Copy text, Searchable PDF). Their xUnit tests skip off
macOS. PDF → DOCX through ONLYOFFICE shares its post-processing (`PdfLayoutDocx`) with tests that do run here, and one
of the bugs below was fixed there.

## Results

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Conversion audit (`audit.py`, 78 checks over 69 corpus files) | 78 | 0 | — |
| Filee.Core.Tests | 277 | 0 | 1 |
| Filee.Engines.Tests | 1050 | 6 | 68 |
| Filee.App.Tests (headless UI) | 355 | 1 | 16 |
| Filee.Cli.Tests | 25 | 0 | 0 |
| Filee.Platform.Tests | 39 | 0 | 6 |

The 7 failing tests failed before any change too, and are about this machine, not the conversions audited here: the
system Pandoc (3.1) can't read AsciiDoc (3 tests); this FFmpeg has no AMR encoder; LibreOffice 24.2 measures ODS column
widths differently from the 26.2 the app downloads; the sub-pixel rasterisation test of the fidelity comparator draws
differently with Linux's fonts; and an Engines page test expects Pandoc and LibreOffice not to be installed. Skipped
tests need Windows, macOS, ONLYOFFICE, Ghostscript, calibre, 7-Zip or rhwp.

The app was also driven under a virtual display: dropping files on the drop zone (a real X11 drag and drop), choosing a
target in the donut, a batch of three images, a broken DOCX, converting a file whose output already exists, and history
after a restart.

## Defects found and fixed

| # | Defect | Root cause | Fix | Regression test |
|---|---|---|---|---|
| 1 | PDF → DOCX dropped numbered headings at the top of each page ("Section 1 Results", "Section 2 Results"). | The running-header detector masks digits and removes text that repeats on most pages, whatever its size. | Only text no larger than the body is a running head when only its numbers change; the same words on most pages still are. | `PdfToDocxLayoutTests.Numbered_headings_at_the_top_of_each_page_stay_in_the_text` |
| 2 | The same on the ONLYOFFICE route: the headings left the body and became one header reading "Section ‹page› Results". | `PdfLayoutDocx` used the same digits-masked rule. | Same rule there (no taller than body text, or the same words). | `PdfLayoutDocxTests.Numbered_headings_at_the_top_of_each_page_stay_headings` |
| 3 | PDF → DOCX (built-in) dropped running headers, footers and page numbers. | They were removed so text reads on, with nowhere to go. | They become the DOCX's header and footer; page numbers become `PAGE` / `NUMPAGES` fields, a repeated logo a header picture. Heads that change per chapter, roman numerals and odd/even heads stay out. TXT / Markdown still read on without them. | `Running_head_and_page_numbers_become_the_header_and_footer`, `A_chapter_running_head_that_changes_is_left_out_not_repeated_on_every_page`, `Text_export_still_reads_on_without_the_running_head` |
| 4 | Lines the PDF set apart (short lines, a list of entries) ran together into one rewrapped paragraph. | The DOCX right margin was capped at 108 pt, so a PDF whose text ends mid-page got a wider column than it had. | The right margin follows the PDF's text (up to 45 % of the page), and a line that ended where Word would not have wrapped keeps its break. | `Short_lines_the_pdf_set_apart_keep_their_line_breaks` |
| 5 | Full pages of text spilled their last lines onto an extra page (3-page PDF → 4 pages). | Margins were measured from the glyphs, but "at least" line spacing puts the extra room above the text, so every page moved down. | Margins come from the line boxes (baseline, line height, descent). | `A_page_full_of_lines_still_fits_on_one_page` (page count checked on Windows CI; here by the audit) |
| 6 | Two-column pages became one column. | Reading order knew the columns; the DOCX was single-column. | Columns of about equal width become Word columns with column breaks (a continuous section). | `Two_columns_become_two_columns_with_a_column_break` |
| 7 | A scanned PDF that already has an OCR text layer came out as a picture without text. | Invisible text was always skipped. | On a page with no visible text, the OCR layer is the page's text (editable, at its positions); the page-sized scan is not also put behind it. Pages without any text are still the page picture. | `A_scan_with_an_ocr_layer_gives_its_text`, `A_scan_without_text_is_still_the_page_picture` |
| 8 | Turned (vertical) text was dropped. | Non-horizontal words were skipped. | Kept as a horizontal paragraph where it starts, words in the direction of their baseline. | `Turned_text_is_kept` |
| 9 | A failed conversion left a truncated file under the output's name (full disk), writers' `<name>.tmp` files, and the pages written before a failure; a failed overwrite lost the old file. | Engines write straight into the final path and nothing took it back on failure. | The queue removes a failed, refused or cancelled file's outputs and `.tmp` files; a file the preset overwrites is moved aside and put back if the new one never comes. | `IncompleteOutputTests` (6 tests) |
| 10 | The error for a DOCX LibreOffice couldn't open showed "failed to launch javaldx – java may not function correctly" and cut off the reason. | LibreOffice's whole stderr was the message. | The message keeps LibreOffice's `Error:` lines and drops the Java start-up warning. | `LibreOfficeRouteTests.Failure_message_gives_the_reason_not_the_java_warning` |
| 11 | Donut labels broke mid-word ("Passwo / rd", "Compre / ss"). | A word wider than the slice wrapped by characters. | The label shrinks (13 → 9 pt) until its longest word fits; a word still too wide gets a box as wide as it. | `DonutLabelPlacementTests.A_word_wider_than_its_slice_gets_a_smaller_font_instead_of_breaking` |

## Engines per pathway

| Pathway | Engine (in order of preference) |
|---|---|
| DOCX / DOC / RTF / ODT → PDF | ONLYOFFICE x2t when installed (macOS), else LibreOffice headless. **Neither installed → "No engine can do this conversion"** (macOS / Linux have no built-in DOCX → PDF; rhwp is a Windows download). |
| PDF → DOCX | ONLYOFFICE x2t + `PdfLayoutDocx` when installed, else the built-in reader (PdfPig + PDFium) and DOCX writer. |
| PDF → PNG / JPG / WebP | PDFium (PDFtoImage) + Magick.NET. |
| PNG / JPG / WebP ↔ | Magick.NET (ImageMagick). |

No conversion uses the network; nothing was added that does.

## Compatibility matrix

| Conversion | Test coverage | Visual fidelity (measured) | Status | Known limitations |
|---|---|---|---|---|
| DOCX → PDF (LibreOffice) | 14 corpus documents (fonts, emphasis, colour, alignment, spacing, page sizes and orientation, sections, headers / footers / page numbers, columns, merged / nested / multi-page tables, inline and floating pictures, lists, links, footnotes and endnotes, equation, text box, watermark, borders and shading, Unicode in 9 scripts, 54 pages) + xUnit | Identical to LibreOffice's own rendering (SSIM 1.000 on every page); all words, numbers, links, pictures, fonts, colours and page sizes kept | Reliable as LibreOffice renders Word documents | Compared with LibreOffice, not with Word (no Word here): LibreOffice's own differences from Word apply (endnotes on a page of their own; fonts it lacks substituted, e.g. Cambria → Caladea). On Linux LibreOffice needs `libreoffice-math` or it drops equations silently. |
| DOCX → PDF (ONLYOFFICE) | xUnit only, skipped here | Not measured here | Untested in this audit | macOS only. |
| PDF → DOCX (built-in) | 10 corpus PDFs (digital multi-page with header / footer / link, ruled table, two columns, landscape and mixed page sizes with picture, rotated text, watermark and background, form, scan, scan with OCR layer, scan mixed with text, 300 pages, encrypted) + 56 xUnit | Rendered DOCX vs PDF: SSIM 0.83–0.99 (table 0.83, mixed 0.89–0.99, text pages 0.93–0.97); words, numbers, reading order, links, pictures, table cells and page sizes kept | Good for text documents; structure is reconstructed, not exact | Tables without ruling lines come out as paragraphs. Form fields become their labels' text (no fields). Watermarks / background drawings on text pages and vector drawings are not converted. Turned text becomes horizontal. A scan with an OCR layer becomes text without the scan image; a scan without one stays a picture (no OCR on this route). Heads that change per chapter, roman page numbers and odd/even heads are left out. |
| PDF → DOCX (ONLYOFFICE) | `PdfLayoutDocx` xUnit (12 run here), x2t skipped | Not measured here | Untested end to end in this audit | macOS only. |
| PDF → PNG / JPG / WebP | 2 PDFs × 3 formats + xUnit | SSIM 0.91–1.00 against a reference rendering; one picture per page, page aspect kept | Reliable | — |
| PNG ↔ JPG ↔ WebP | 14 source images (RGB, RGBA, grey, palette, CMYK, EXIF-rotated, ICC, 8000 × 6000, 1 × 997, 5000 × 3) × 2 targets + xUnit | Lossless targets identical (difference 0); lossy ≤ 1.5 / 255 mean difference, SSIM ≥ 0.987; transparency kept in PNG / WebP and flattened on white in JPG; EXIF orientation applied; grey stays grey; ICC kept | Reliable | Lossy WebP halves colour resolution (4:2:0): an image a few pixels tall loses colour (13.9 / 255 on 5000 × 3); WebP lossless avoids it. |
| Broken / mislabelled / encrypted input | 8 cases + the full-disk and overwrite cases | — | Fails clearly, nothing left behind | — |
| Batch, parallel, repeated, Unicode names, overwrite, temp files | 7 checks + UI | — | Reliable | — |

## Performance (this container)

DOCX → PDF about 2 s per document (54 pages: 2.2 s); PDF → DOCX 1.1–1.5 s, 300 pages 5.3 s; 8000 × 6000 PNG → JPG 1.8 s,
→ WebP 4.2 s; 16 files in one batch 6.9 s. No change here was aimed at speed; the fixes add no measurable time.

## Remaining issues

- DOCX → PDF on a Mac with neither ONLYOFFICE nor LibreOffice fails ("No engine can do this conversion"); the Engines
  dialog's "Word … convert without it" means other targets, not PDF.
- On Linux, a LibreOffice without `libreoffice-math` drops equations without a warning.
- The x2t routes and Vision OCR need a run on macOS: `dotnet test` there runs their tests, and `audit.py` checks
  whichever route the app picks.
- Tables without ruling lines, form fields and vector drawings in PDF → DOCX (see the matrix).

## Reproducing

```bash
dotnet build Filee.slnx -c Release
dotnet test Filee.slnx --no-build -c Release
pip install python-docx reportlab pymupdf scikit-image pillow numpy
python3 tests/conversion-audit/make_corpus.py /tmp/audit/corpus
python3 tests/conversion-audit/audit.py /tmp/audit/corpus /tmp/audit/work
```

The audit needs LibreOffice (with Math on Linux) and `qpdf`; see `README.md` beside this file.
