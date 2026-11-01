# Conversion audit

End-to-end checks of the document and image pathways through the real `filee` command line, against facts about each
file instead of "a file came out". The xUnit suites (`dotnet test`) test the engines piece by piece; this audit runs
whole conversions on a generated corpus and compares the results with what the source contains and with an independent
rendering.

## What it checks

| Section | Pathways | Checks |
|---|---|---|
| `docx` | DOCX → PDF | words (per page too), page count and sizes, links, pictures, embedded fonts, colours, list markers, alignment, two columns; every page against LibreOffice's own rendering of the same DOCX (SSIM > 0.97) |
| `pdf` | PDF → DOCX | valid OOXML; words, numbers, reading order, links, pictures, table cells, page sizes; the DOCX rendered by LibreOffice against the PDF (SSIM > 0.80, missing ink < 3 % of a page); scans keep their picture; encrypted PDFs fail clearly |
| `pdfimg` | PDF → PNG / JPG / WebP | one picture per page, page aspect ratio, close to a reference rendering |
| `img` | PNG / JPG / WebP ↔ each other | size, EXIF orientation applied, transparency kept (PNG, WebP) or flattened on white (JPG), colour difference, SSIM, grayscale and ICC profiles kept |
| `bad` | broken, empty, mislabelled, encrypted files | a failure (not "converted"), a non-zero exit code, nothing left in the output folder; a JPEG named `.png` converts |
| `misc` | file names, batches, parallel runs, repeats, existing outputs, temp folder | Unicode and quoted names, 16 files at once, 8 parallel runs giving the same text, 5 repeats giving the same text, `--skip` / default / `--overwrite`, nothing left in the temp folder |
| `large` | 54-page DOCX, 300-page PDF, 8000 × 6000 PNG | time budget and no lost text |

## Running it

Needs Python 3 with `python-docx reportlab pymupdf scikit-image pillow numpy`, LibreOffice (`soffice`, the reference
renderer and the DOCX → PDF engine off macOS-with-ONLYOFFICE) and `qpdf` (to encrypt a test PDF).

```bash
dotnet build src/Filee.Cli -c Release
pip install python-docx reportlab pymupdf scikit-image pillow numpy
python3 tests/conversion-audit/make_corpus.py /tmp/audit/corpus
python3 tests/conversion-audit/audit.py /tmp/audit/corpus /tmp/audit/work
```

`--section docx|pdf|pdfimg|img|bad|misc|large` runs one section, `--only <text>` the files whose name contains it.
Results go to `<work>/report.md` and `report.json`; the exit code is 1 when a check failed. Every converted file stays in
the work folder for a look.

Each corpus file has a `<name>.expect.json` with what the conversion must keep, written by `make_corpus.py`.
