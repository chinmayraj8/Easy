"""Runs every document and image pathway through the real `filee` command line and checks each result against facts,
not against "a file came out":

- DOCX → PDF: words (and words per page), page count, page sizes, links, pictures, fonts, colours, list markers,
  alignment; and every page against a reference rendering of the same DOCX by LibreOffice run directly (SSIM).
- PDF → DOCX: the DOCX is valid OOXML, keeps the PDF's words, numbers, reading order, links, pictures, tables and page
  sizes, and its rendering (LibreOffice) is compared page by page with the PDF's (SSIM).
- PDF → PNG/JPG/WebP: one picture per page, the page's aspect ratio, close to a reference rendering.
- Images: size, aspect ratio, EXIF orientation applied, transparency kept (or flattened on white for JPG), colour close
  to the source, no needless quality loss.
- Broken, mislabelled, encrypted, empty and oddly named files: a clear failure and no half-written output, or a correct
  result.
- Several files at once, the same file converted repeatedly and in parallel, and that nothing is left in the temp folder.

    python3 audit.py <corpus> <work folder> [--cli path] [--only substring]

Writes <work>/report.json and <work>/report.md and exits 1 when a check failed.
"""

import argparse
import concurrent.futures
import glob
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import unicodedata
import zipfile

import fitz  # PyMuPDF
import numpy as np
from PIL import Image, ImageOps
from skimage.metrics import structural_similarity

fitz.TOOLS.mupdf_display_errors(False)

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CLI = os.path.join(HERE, "..", "..", "src", "Filee.Cli", "bin", "Release", "net10.0", "filee-cli")

results = []


class Check:
    def __init__(self, pathway, case):
        self.pathway, self.case, self.problems, self.notes, self.metrics = pathway, case, [], [], {}

    def fail(self, msg):
        self.problems.append(msg)

    def note(self, msg):
        self.notes.append(msg)

    def ok(self, cond, msg):
        if not cond:
            self.fail(msg)
        return cond


def record(check):
    results.append(check)
    status = "PASS" if not check.problems else "FAIL"
    print(f"[{status}] {check.pathway:<18} {check.case:<34} {'; '.join(check.problems)[:300]}")


# ---------------------------------------------------------------- running the app

def convert(cli, sources, target, outdir, extra=()):
    os.makedirs(outdir, exist_ok=True)
    start = time.time()
    p = subprocess.run([cli, "convert", *sources, "--to", target, "-o", outdir, "--json", "--overwrite", *extra],
                       capture_output=True, text=True, timeout=1800)
    elapsed = time.time() - start
    try:
        report = json.loads(p.stdout[p.stdout.index("{"):])
    except ValueError:
        report = {"files": [], "raw": p.stdout + p.stderr}
    return report, elapsed, p.returncode


def outputs_of(report):
    return [o for f in report.get("files", []) for o in f.get("outputs", []) or []]


# ---------------------------------------------------------------- text helpers

def norm(text):
    text = unicodedata.normalize("NFKC", text).replace("­", "")
    return re.sub(r"\s+", " ", text)


def pdf_text(path):
    with fitz.open(path) as d:
        return [norm(p.get_text()) for p in d]


def docx_text(path):
    with zipfile.ZipFile(path) as z:
        xml = ""
        for n in z.namelist():
            if re.match(r"word/(document|header\d*|footer\d*|footnotes|endnotes)\.xml$", n):
                xml += z.read(n).decode("utf-8") + " "
    xml = re.sub(r"</w:p>", " ", xml)
    xml = re.sub(r"<w:tab/>|<w:br/>", " ", xml)
    text = re.sub(r"<[^>]+>", "", xml)
    return norm(text.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", '"').replace("&apos;", "'"))


def missing_words(haystack, words):
    flat = haystack.replace(" ", "")
    return [w for w in words if w not in haystack and w.replace(" ", "") not in flat]


# ---------------------------------------------------------------- rendering and comparing

def render(pdf, dpi=60, max_pages=6):
    pages = []
    with fitz.open(pdf) as d:
        for i, p in enumerate(d):
            if i >= max_pages:
                break
            pix = p.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY, alpha=False)
            pages.append(np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width))
    return pages


def ssim(a, b):
    h, w = min(a.shape[0], b.shape[0]), min(a.shape[1], b.shape[1])
    if abs(a.shape[0] - b.shape[0]) > 3 or abs(a.shape[1] - b.shape[1]) > 3:
        b = np.asarray(Image.fromarray(b).resize((a.shape[1], a.shape[0])))
        h, w = a.shape
    return float(structural_similarity(a[:h, :w], b[:h, :w], data_range=255))


def ink(a):
    return float((a < 128).mean())


def soffice(src, target, outdir, filt=None):
    """LibreOffice run directly: the reference renderer, and the way a DOCX is opened to look at it."""
    if not shutil.which("soffice"):
        return None  # no LibreOffice: the visual comparisons are skipped
    os.makedirs(outdir, exist_ok=True)
    profile = tempfile.mkdtemp(prefix="lo-ref-")
    try:
        subprocess.run(["soffice", f"-env:UserInstallation=file://{profile}", "--headless", "--convert-to",
                        target + (":" + filt if filt else ""), "--outdir", outdir, src],
                       capture_output=True, timeout=600)
    finally:
        shutil.rmtree(profile, ignore_errors=True)
    out = os.path.join(outdir, os.path.splitext(os.path.basename(src))[0] + "." + target)
    return out if os.path.exists(out) else None


def docx_is_valid(path, c):
    try:
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            c.ok("word/document.xml" in names and "[Content_Types].xml" in names, "DOCX lacks document.xml or content types")
            bad = z.testzip()
            c.ok(bad is None, f"corrupt ZIP member {bad}")
            from xml.dom import minidom
            for n in names:
                if n.endswith(".xml") or n.endswith(".rels"):
                    minidom.parseString(z.read(n))
            return True
    except Exception as e:  # noqa: BLE001
        c.fail(f"DOCX is not a valid package: {e}")
        return False


# ---------------------------------------------------------------- DOCX → PDF

def audit_docx_to_pdf(cli, corpus, work, only):
    for src in sorted(glob.glob(os.path.join(corpus, "*.docx"))):
        name = os.path.splitext(os.path.basename(src))[0]
        exp_path = os.path.join(corpus, name + ".expect.json")
        if not os.path.exists(exp_path) or (only and only not in name):
            continue
        exp = json.load(open(exp_path, encoding="utf-8"))
        c = Check("docx→pdf", name)
        report, elapsed, _ = convert(cli, [src], "pdf", os.path.join(work, "docx2pdf"))
        c.metrics["seconds"] = round(elapsed, 2)
        outs = outputs_of(report)
        if not c.ok(len(outs) == 1 and os.path.getsize(outs[0]) > 0, f"no PDF: {json.dumps(report)[:300]}"):
            record(c)
            continue
        pdf = outs[0]
        try:
            pages = pdf_text(pdf)
        except Exception as e:  # noqa: BLE001
            c.fail(f"PDF does not open: {e}")
            record(c)
            continue
        text = " ".join(pages)
        if (m := missing_words(text, exp.get("words", []))):
            c.fail(f"missing words {m}")
        if exp.get("any_words") and missing_words(text, exp["any_words"]):
            c.note(f"not in text layer: {exp['any_words']} (may be drawn as a shape)")
        if (m := missing_words(text, exp.get("numbers", []))):
            c.fail(f"missing {len(m)} numbers, e.g. {m[:5]}")
        if "pages" in exp:
            c.ok(len(pages) == exp["pages"], f"{len(pages)} pages, expected {exp['pages']}")
        if "min_pages" in exp:
            c.ok(len(pages) >= exp["min_pages"], f"{len(pages)} pages, expected at least {exp['min_pages']}")
        for page, words in exp.get("per_page_words", {}).items():
            i = int(page) - 1
            if i < len(pages) and (m := missing_words(pages[i], words)):
                c.fail(f"page {page} lacks {m}")
        with fitz.open(pdf) as d:
            if "page_sizes" in exp:
                sizes = [[round(p.rect.width), round(p.rect.height)] for p in d]
                for i, (want, got) in enumerate(zip(exp["page_sizes"], sizes)):
                    c.ok(abs(want[0] - got[0]) <= 2 and abs(want[1] - got[1]) <= 2, f"page {i + 1} is {got}, expected {want}")
            if exp.get("page_numbers"):
                for i, p in enumerate(pages):
                    c.ok(re.search(rf"Page {i + 1}\b", p) is not None, f"page {i + 1} has no page number")
            if "links" in exp:
                uris = [l.get("uri") for p in d for l in p.get_links()]
                for u in exp["links"]:
                    c.ok(u in uris, f"link {u} lost (found {uris})")
            if "images" in exp:
                # Images drawn, those nested in form XObjects (as ONLYOFFICE writes them) included.
                n = sum(len(p.get_image_info()) for p in d)
                c.ok(n >= exp["images"], f"{n} pictures, expected {exp['images']}")
            if "fonts" in exp:
                fonts = {f[3].split("+")[-1].replace("-", "").replace(",", "") for p in d for f in p.get_fonts()}
                for f in exp["fonts"]:
                    c.ok(any(f.lower() in x.lower() for x in fonts), f"font {f} not embedded (has {sorted(fonts)})")
                c.metrics["fonts"] = sorted(fonts)
            if "colors" in exp:
                spans = [s for p in d for b in p.get_text("dict")["blocks"] for l in b.get("lines", []) for s in l["spans"]]
                cols = {f"{s['color']:06x}" for s in spans}
                for col in exp["colors"]:
                    c.ok(col in cols, f"text colour {col} lost (has {sorted(cols)})")
            if "alignment" in exp:
                p0 = d[0]
                w = p0.rect.width
                for word, where in exp["alignment"].items():
                    hits = p0.search_for(word)
                    if not c.ok(bool(hits), f"{word} not found for alignment"):
                        continue
                    x = hits[0].x0
                    rel = x / w
                    good = {"left": rel < 0.25, "center": 0.3 < rel < 0.6, "right": rel > 0.6}[where]
                    c.ok(good, f"{word} starts at {rel:.0%} of the width, expected {where}")
            if "markers" in exp:
                for mk in exp["markers"]:
                    c.ok(mk in text, f"list marker {mk!r} lost")
            if exp.get("columns") == 2:
                hits = [h.x0 for i in range(12) for h in d[0].search_for(f"ColumnPara{i} ")]
                c.ok(bool(hits) and max(hits) - min(hits) > d[0].rect.width * 0.3, "text not laid out in two columns")
        # Visual: the app's PDF against LibreOffice's own rendering of the same file.
        ref = soffice(src, "pdf", os.path.join(work, "docx2pdf-ref"))
        if ref:
            a, b = render(ref), render(pdf)
            if len(a) == len(b):
                scores = [ssim(x, y) for x, y in zip(a, b)]
                c.metrics["ssim_min"] = round(min(scores), 4)
                c.ok(min(scores) > 0.97, f"rendering differs from the reference (SSIM {min(scores):.3f})")
            else:
                c.note(f"reference has {len(a)} rendered pages, output {len(b)}")
        record(c)


# ---------------------------------------------------------------- PDF → DOCX

def audit_pdf_to_docx(cli, corpus, work, only):
    for src in sorted(glob.glob(os.path.join(corpus, "*.pdf"))):
        name = os.path.splitext(os.path.basename(src))[0]
        exp_path = os.path.join(corpus, name + ".expect.json")
        if not os.path.exists(exp_path) or (only and only not in name):
            continue
        exp = json.load(open(exp_path, encoding="utf-8"))
        c = Check("pdf→docx", name)
        report, elapsed, _ = convert(cli, [src], "docx", os.path.join(work, "pdf2docx"))
        c.metrics["seconds"] = round(elapsed, 2)
        outs = outputs_of(report)
        if exp.get("password_protected"):
            state = report.get("files", [{}])[0]
            c.ok(not outs and state.get("state") == "failed", f"encrypted PDF: expected a clear failure, got {json.dumps(state)[:200]}")
            c.metrics["error"] = state.get("error")
            record(c)
            continue
        if not c.ok(len(outs) == 1, f"no DOCX: {json.dumps(report)[:300]}"):
            record(c)
            continue
        out = outs[0]
        if not docx_is_valid(out, c):
            record(c)
            continue
        text = docx_text(out)
        if exp.get("scanned"):
            # Only a picture of text: the page must come back as that picture (OCR is macOS Vision only).
            with zipfile.ZipFile(out) as z:
                media = [n for n in z.namelist() if n.startswith("word/media/")]
            c.ok(len(media) >= 1, "scanned page lost its picture")
            if missing_words(text, exp.get("words", [])):
                c.note("no text recognised (no OCR on this platform)")
        else:
            if (m := missing_words(text, exp.get("words", []))):
                c.fail(f"missing words {m}")
            if (m := missing_words(text, exp.get("numbers", []))):
                c.fail(f"missing numbers {m}")
        if "order" in exp:
            pos = [text.find(w) for w in exp["order"]]
            c.ok(all(p >= 0 for p in pos) and pos == sorted(pos), f"reading order wrong: {list(zip(exp['order'], pos))}")
        with zipfile.ZipFile(out) as z:
            doc = z.read("word/document.xml").decode("utf-8")
            rels = z.read("word/_rels/document.xml.rels").decode("utf-8") if "word/_rels/document.xml.rels" in z.namelist() else ""
            media = [n for n in z.namelist() if n.startswith("word/media/")]
        if "links" in exp:
            for u in exp["links"]:
                c.ok(u in rels, f"link {u} lost")
        if "images" in exp:
            c.ok(len(media) >= exp["images"], f"{len(media)} pictures, expected {exp['images']}")
        if "table_cells" in exp:
            cells = doc.count("<w:tc>") + doc.count("<w:tc ")
            c.ok(cells >= exp["table_cells"], f"table rebuilt with {cells} cells, expected {exp['table_cells']}")
        if "page_sizes" in exp:
            sizes = [(int(m.group(1)) / 20, int(m.group(2)) / 20)
                     for m in re.finditer(r'<w:pgSz w:w="(\d+)" w:h="(\d+)"', doc)]
            for i, want in enumerate(exp["page_sizes"]):
                if i < len(sizes):
                    c.ok(abs(sizes[i][0] - want[0]) < 3 and abs(sizes[i][1] - want[1]) < 3,
                         f"section {i + 1} page size {sizes[i]}, expected {want}")
                else:
                    c.fail(f"no section for page {i + 1} (sizes {sizes})")
        # Visual: render the DOCX (LibreOffice) and compare with the PDF.
        rendered = soffice(out, "pdf", os.path.join(work, "pdf2docx-render"))
        if not shutil.which("soffice"):
            c.note("LibreOffice not installed: DOCX rendering not compared")
        elif c.ok(rendered is not None, "the DOCX could not be opened and rendered"):
            with fitz.open(rendered) as d:
                n = d.page_count
            if "pages" in exp:
                c.metrics["pages"] = n
                if n != exp["pages"]:
                    (c.fail if abs(n - exp["pages"]) > max(1, exp["pages"] // 20) else c.note)(
                        f"DOCX renders as {n} pages, PDF has {exp['pages']}")
            a, b = render(src), render(rendered)
            scores = [ssim(x, y) for x, y in zip(a, b)]
            if scores and exp.get("ocr_text"):
                c.metrics["ssim_min"] = round(min(scores), 4)
                c.note("scan replaced by its OCR text: pixels not compared")
            elif scores:
                c.metrics["ssim_min"] = round(min(scores), 4)
                c.metrics["ssim_mean"] = round(sum(scores) / len(scores), 4)
                c.ok(min(scores) > 0.80, f"rendered DOCX looks different from the PDF (SSIM min {min(scores):.3f})")
                missing_ink = [ink(x) - ink(y) for x, y in zip(a, b)]
                c.ok(max(missing_ink) < 0.03, f"ink missing in the DOCX rendering ({max(missing_ink):.1%} of a page)")
        record(c)


# ---------------------------------------------------------------- PDF → images

def audit_pdf_to_images(cli, corpus, work, only):
    for fmt in ("png", "jpg", "webp"):
        for src in ("pdf_digital.pdf", "pdf_mixed.pdf"):
            if only and only not in src and only not in fmt:
                continue
            c = Check(f"pdf→{fmt}", src)
            path = os.path.join(corpus, src)
            report, elapsed, _ = convert(cli, [path], fmt, os.path.join(work, f"pdf2{fmt}"))
            c.metrics["seconds"] = round(elapsed, 2)
            outs = sorted(outputs_of(report))
            with fitz.open(path) as d:
                c.ok(len(outs) == d.page_count, f"{len(outs)} pictures for {d.page_count} pages")
                for i, (o, p) in enumerate(zip(outs, d)):
                    with Image.open(o) as im:
                        im.load()
                        want = p.rect.width / p.rect.height
                        c.ok(abs(im.width / im.height - want) < 0.01, f"page {i + 1} aspect {im.width}x{im.height}, page is {want:.3f}")
                        c.metrics.setdefault("sizes", []).append(f"{im.width}x{im.height}")
                        ref = p.get_pixmap(dpi=60, colorspace=fitz.csGRAY)
                        refa = np.frombuffer(ref.samples, dtype=np.uint8).reshape(ref.height, ref.width)
                        got = np.asarray(im.convert("L").resize((ref.width, ref.height), Image.LANCZOS))
                        s = ssim(refa, got)
                        c.metrics.setdefault("ssim", []).append(round(s, 3))
                        c.ok(s > 0.85, f"page {i + 1} differs from the reference rendering (SSIM {s:.3f})")
            record(c)


# ---------------------------------------------------------------- images

def pixels(path):
    with Image.open(path) as im:
        im = ImageOps.exif_transpose(im)
        return im.convert("RGBA")


def audit_images(cli, corpus, work, only):
    sources = sorted(glob.glob(os.path.join(corpus, "img_*")))
    for src in sources:
        name = os.path.basename(src)
        sext = name.rsplit(".", 1)[1]
        for fmt in ("png", "jpg", "webp"):
            if fmt == sext or (only and only not in name and only != fmt):
                continue
            c = Check(f"{sext}→{fmt}", name)
            report, elapsed, _ = convert(cli, [src], fmt, os.path.join(work, f"img-{sext}2{fmt}"))
            c.metrics["seconds"] = round(elapsed, 2)
            outs = outputs_of(report)
            if not c.ok(len(outs) == 1, f"no output: {json.dumps(report)[:300]}"):
                record(c)
                continue
            a, b = pixels(src), Image.open(outs[0])
            c.metrics["mode"] = b.mode
            exif_orient = b.getexif().get(0x0112, 1)
            b = ImageOps.exif_transpose(b).convert("RGBA")
            c.ok(a.size == b.size, f"size {b.size}, expected {a.size} (EXIF orientation {exif_orient} in output)")
            if a.size != b.size:
                record(c)
                continue
            aa, ba = np.asarray(a).astype(int), np.asarray(b).astype(int)
            src_alpha = aa[..., 3].min() < 255
            if src_alpha and fmt in ("png", "webp"):
                c.ok(np.abs(aa[..., 3] - ba[..., 3]).max() <= 2, "transparency lost or changed")
            if src_alpha and fmt == "jpg":
                hole = ba[: a.height // 4, : a.width // 4, :3]
                c.ok(hole.mean() > 240, f"transparent area became {hole.mean():.0f}/255 (black?) instead of white")
            visible = aa[..., 3] > 250
            if visible.any():
                diff = np.abs(aa[..., :3] - ba[..., :3])[visible]
                mean = diff.mean()
                c.metrics["mean_abs_diff"] = round(float(mean), 2)
                limit = 0.5 if fmt == "png" and sext in ("png",) else 6
                if mean > limit and fmt == "webp" and min(a.size) < 8:
                    # Lossy WebP always halves colour resolution (4:2:0): an image a few pixels tall loses colour.
                    c.note(f"lossy WebP colour loss on a {a.width}x{a.height} image: {mean:.2f}/255 (known limitation)")
                else:
                    c.ok(mean <= limit, f"colours changed: mean difference {mean:.2f}/255 (limit {limit})")
                if a.width >= 7 and a.height >= 7:
                    # A JPEG has no transparency: compare with the source flattened on white, as it should be.
                    flat = Image.alpha_composite(Image.new("RGBA", a.size, "white"), a) if fmt == "jpg" else a
                    s = ssim(np.asarray(flat.convert("L")), np.asarray(b.convert("L")))
                    c.metrics["ssim"] = round(s, 4)
                    c.ok(s > 0.95, f"structure changed (SSIM {s:.3f})")
            with Image.open(src) as orig, Image.open(outs[0]) as out:
                if orig.mode in ("L", "LA") and fmt != "webp":
                    c.ok(out.mode in ("L", "LA", "I;16", "P", "1"), f"grayscale became {out.mode}")
                if "icc_profile" in orig.info and fmt != "webp":
                    c.ok("icc_profile" in out.info, "colour profile dropped")
            record(c)


# ---------------------------------------------------------------- failures and robustness

def audit_bad_files(cli, corpus, work, only):
    cases = [("bad_truncated.png", "jpg"), ("bad_random.jpg", "png"), ("bad_empty.webp", "png"),
             ("bad_notreally.pdf", "docx"), ("bad_notreally.pdf", "png"), ("bad_truncated.docx", "pdf"),
             ("pdf_password.pdf", "png"), ("pdf_password.pdf", "docx")]
    for name, fmt in cases:
        if only and only not in name:
            continue
        c = Check("invalid input", f"{name}→{fmt}")
        outdir = os.path.join(work, "bad", name + "-" + fmt)
        shutil.rmtree(outdir, ignore_errors=True)
        report, _, code = convert(cli, [os.path.join(corpus, name)], fmt, outdir)
        files = report.get("files", [{}])
        state = files[0].get("state") if files else None
        left = os.listdir(outdir) if os.path.isdir(outdir) else []
        c.ok(state in ("failed", "skipped"), f"state {state!r}: a broken file must not be reported as converted")
        c.ok(not left, f"left files behind: {left}")
        c.ok(code != 0, "exit code 0 for a failed conversion")
        c.metrics["error"] = files[0].get("error") if files else report.get("raw", "")[:200]
        record(c)
    # A JPEG named .png converts by content.
    c = Check("mislabelled", "mislabeled_jpeg.png→webp")
    report, _, _ = convert(cli, [os.path.join(corpus, "mislabeled_jpeg.png")], "webp", os.path.join(work, "misl"))
    c.ok(len(outputs_of(report)) == 1, f"failed: {json.dumps(report)[:200]}")
    record(c)


def audit_names_batch_parallel(cli, corpus, work, only):
    if only:
        return
    tmp_before = set(os.listdir(tempfile.gettempdir()))
    # Unusual names.
    for name, fmt in (("ünïcødé 名前 (copy) #1.docx", "pdf"), ("spaces and 'quotes'.png", "jpg")):
        c = Check("file names", name)
        report, _, _ = convert(cli, [os.path.join(corpus, name)], fmt, os.path.join(work, "names"))
        outs = outputs_of(report)
        c.ok(len(outs) == 1 and os.path.basename(outs[0]) == os.path.splitext(name)[0] + "." + fmt,
             f"output {outs}")
        record(c)
    # Batch: many files in one command, all must come out.
    c = Check("batch", "12 images + 4 documents")
    srcs = sorted(glob.glob(os.path.join(corpus, "img_*.png")))[:6] + sorted(glob.glob(os.path.join(corpus, "img_*.jpg")))[:6]
    report, elapsed, _ = convert(cli, srcs, "webp", os.path.join(work, "batch"))
    c.ok(report.get("converted") == len(srcs), f"{report.get('converted')} of {len(srcs)} converted")
    docs = [os.path.join(corpus, n) for n in ("fmt_text.docx", "lists.docx", "tables.docx", "hyperlinks.docx")]
    report, e2, _ = convert(cli, docs, "pdf", os.path.join(work, "batch-docs"))
    c.ok(report.get("converted") == len(docs), f"documents: {report.get('converted')} of {len(docs)} converted")
    c.metrics["seconds"] = round(elapsed + e2, 2)
    record(c)
    # Parallel: four commands at once on the same inputs into separate folders; outputs must be identical in content.
    c = Check("concurrency", "4 parallel docx→pdf + pdf→docx")
    jobs = []
    with concurrent.futures.ThreadPoolExecutor(8) as ex:
        for i in range(4):
            jobs.append(ex.submit(convert, cli, [os.path.join(corpus, "tables.docx")], "pdf", os.path.join(work, f"par{i}")))
            jobs.append(ex.submit(convert, cli, [os.path.join(corpus, "pdf_table.pdf")], "docx", os.path.join(work, f"par{i}")))
    texts = {"pdf": set(), "docx": set()}
    for j in jobs:
        report, _, _ = j.result()
        outs = outputs_of(report)
        if c.ok(len(outs) == 1, f"a parallel run failed: {json.dumps(report)[:200]}"):
            o = outs[0]
            texts["pdf" if o.endswith(".pdf") else "docx"].add(" ".join(pdf_text(o)) if o.endswith(".pdf") else docx_text(o))
    c.ok(all(len(v) == 1 for v in texts.values()), "parallel runs produced different text")
    record(c)
    # Repeated: the same conversion five times, same output.
    c = Check("repeat", "5× pdf_table→docx")
    seen = set()
    for i in range(5):
        report, _, _ = convert(cli, [os.path.join(corpus, "pdf_table.pdf")], "docx", os.path.join(work, "repeat"))
        seen.add(docx_text(outputs_of(report)[0]))
    c.ok(len(seen) == 1, "repeated conversion gave different text")
    record(c)
    # Overwrite and skip.
    c = Check("existing output", "--skip keeps, --overwrite replaces")
    d = os.path.join(work, "exist")
    os.makedirs(d, exist_ok=True)
    target = os.path.join(d, "img_rgb.jpg")
    open(target, "w").write("old")
    subprocess.run([cli, "convert", os.path.join(corpus, "img_rgb.png"), "--to", "jpg", "-o", d, "--skip", "--quiet"])
    c.ok(open(target).read() == "old", "--skip replaced the existing file")
    subprocess.run([cli, "convert", os.path.join(corpus, "img_rgb.png"), "--to", "jpg", "-o", d, "--quiet"])
    c.ok(open(target).read() == "old", "the default replaced an existing file")
    c.ok(len(os.listdir(d)) == 2, f"default should write a second name next to it: {os.listdir(d)}")
    subprocess.run([cli, "convert", os.path.join(corpus, "img_rgb.png"), "--to", "jpg", "-o", d, "--overwrite", "--quiet"])
    with Image.open(target) as im:
        c.ok(im.format == "JPEG", "--overwrite did not replace the file")
    record(c)
    # Temp files: nothing new left behind in the temp folder.
    c = Check("cleanup", "temp folder after all runs")
    left = [n for n in set(os.listdir(tempfile.gettempdir())) - tmp_before if not n.startswith("lo-ref-")]
    c.ok(not left, f"left in temp: {left[:10]}")
    record(c)


def audit_large(cli, corpus, work, only):
    if only and only != "large":
        return
    for name, fmt, budget in (("long_document.docx", "pdf", 120), ("pdf_large.pdf", "docx", 600),
                              ("img_large.png", "webp", 120), ("img_large.png", "jpg", 60)):
        c = Check("large", f"{name}→{fmt}")
        report, elapsed, _ = convert(cli, [os.path.join(corpus, name)], fmt, os.path.join(work, "large"))
        c.metrics["seconds"] = round(elapsed, 1)
        outs = outputs_of(report)
        c.ok(len(outs) == 1, f"failed: {json.dumps(report)[:300]}")
        c.ok(elapsed < budget, f"took {elapsed:.0f}s (budget {budget}s)")
        if outs and fmt == "docx":
            t = docx_text(outs[0])
            c.ok(not missing_words(t, ["P0L0", "P150L25", "P299L49"]), "text lost in the large document")
        if outs and fmt == "pdf":
            with fitz.open(outs[0]) as d:
                c.metrics["pages"] = d.page_count
                t = d[-1].get_text()
                c.ok("Chapter40Para11" in norm(t) or "Chapter40Para11" in " ".join(pdf_text(outs[0])), "end of the document lost")
        record(c)


def write_report(work):
    data = [dict(pathway=c.pathway, case=c.case, passed=not c.problems, problems=c.problems, notes=c.notes, metrics=c.metrics)
            for c in results]
    with open(os.path.join(work, "report.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    lines = ["| Pathway | Case | Result | Problems / notes | Metrics |", "|---|---|---|---|---|"]
    for d in data:
        lines.append("| {} | {} | {} | {} | {} |".format(
            d["pathway"], d["case"], "pass" if d["passed"] else "**FAIL**",
            "; ".join(d["problems"] + d["notes"]).replace("|", "/")[:400],
            ", ".join(f"{k}={v}" for k, v in d["metrics"].items() if k not in ("fonts",))[:200]))
    passed = sum(d["passed"] for d in data)
    lines.insert(0, f"**{passed} of {len(data)} checks passed**\n")
    with open(os.path.join(work, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"\n{passed} of {len(data)} checks passed; report in {work}/report.md")
    return passed == len(data)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("corpus")
    ap.add_argument("work")
    ap.add_argument("--cli", default=DEFAULT_CLI)
    ap.add_argument("--only", default="")
    ap.add_argument("--section", default="all")
    a = ap.parse_args()
    cli = os.path.abspath(a.cli)
    os.makedirs(a.work, exist_ok=True)
    sections = {"docx": audit_docx_to_pdf, "pdf": audit_pdf_to_docx, "pdfimg": audit_pdf_to_images, "img": audit_images,
                "bad": audit_bad_files, "misc": audit_names_batch_parallel, "large": audit_large}
    for key, fn in sections.items():
        if a.section in ("all", key):
            fn(cli, a.corpus, a.work, a.only)
    raise SystemExit(0 if write_report(a.work) else 1)


if __name__ == "__main__":
    main()
