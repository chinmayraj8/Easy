"""Builds the conversion-audit corpus: DOCX files that each exercise one Word feature, PDFs of every kind the PDF → DOCX
route meets (digital, scanned, mixed, rotated, landscape, forms, encrypted ...), and images in every colour model.

Every file records what it must keep in <name>.expect.json (words, page count, images, links ...), so audit.py can check
the converted result against facts instead of only "a file came out".

    python3 make_corpus.py <corpus folder>
"""

import io
import json
import os
import random
import sys
import zipfile

from docx import Document
from docx.enum.section import WD_ORIENT, WD_SECTION
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK, WD_LINE_SPACING
from docx.oxml import OxmlElement, parse_xml
from docx.oxml.ns import nsdecls, qn
from docx.shared import Cm, Inches, Pt, RGBColor
from PIL import Image, ImageDraw, ImageFont

OUT = sys.argv[1] if len(sys.argv) > 1 else "corpus"
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"


def expect(name, **facts):
    with open(os.path.join(OUT, name + ".expect.json"), "w", encoding="utf-8") as f:
        json.dump(facts, f, ensure_ascii=False, indent=1)


def picture(path, w=600, h=400, mode="RGB", label="IMG", transparent=False):
    img = Image.new("RGBA" if transparent else mode, (w, h), (0, 0, 0, 0) if transparent else None)
    d = ImageDraw.Draw(img)
    for y in range(h):  # gradient: compression and colour shifts show up
        d.line([(0, y), (w, y)], fill=(40 + 180 * y // h, 90, 220 - 160 * y // h, 255))
    d.ellipse([w // 4, h // 4, 3 * w // 4, 3 * h // 4], fill=(250, 200, 30, 255))
    d.text((10, 10), label, fill=(255, 255, 255, 255), font=ImageFont.truetype(FONT, 28))
    if transparent:
        px = img.load()
        for x in range(w // 3):
            for y in range(h // 3):
                px[x, y] = (0, 0, 0, 0)
    img.save(path)
    return path


def shading(cell_or_par, color):
    el = parse_xml(r'<w:shd {} w:val="clear" w:color="auto" w:fill="{}"/>'.format(nsdecls("w"), color))
    pr = cell_or_par._tc.get_or_add_tcPr() if hasattr(cell_or_par, "_tc") else cell_or_par._p.get_or_add_pPr()
    pr.append(el)


def hyperlink(paragraph, url, text):
    part = paragraph.part
    r_id = part.relate_to(url, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), r_id)
    run = OxmlElement("w:r")
    rpr = OxmlElement("w:rPr")
    style = OxmlElement("w:rStyle")
    style.set(qn("w:val"), "Hyperlink")
    rpr.append(style)
    color = OxmlElement("w:color")
    color.set(qn("w:val"), "0563C1")
    rpr.append(color)
    u = OxmlElement("w:u")
    u.set(qn("w:val"), "single")
    rpr.append(u)
    run.append(rpr)
    t = OxmlElement("w:t")
    t.text = text
    run.append(t)
    link.append(run)
    paragraph._p.append(link)


def field(paragraph, instr):
    run = paragraph.add_run()
    for tag, attr in (("w:fldChar", "begin"), ("w:instrText", None), ("w:fldChar", "separate")):
        el = OxmlElement(tag)
        if attr:
            el.set(qn("w:fldCharType"), attr)
        else:
            el.set(qn("xml:space"), "preserve")
            el.text = instr
        run._r.append(el)
    run2 = paragraph.add_run("1")
    end = OxmlElement("w:fldChar")
    end.set(qn("w:fldCharType"), "end")
    run2._r.append(end)


def save(doc, name, **facts):
    doc.save(os.path.join(OUT, name + ".docx"))
    expect(name, **facts)


LOREM = ("The quick brown fox jumps over the lazy dog while seventeen zebras quietly measure "
         "the river with copper rulers and violet notebooks.")


def docx_corpus():
    # 1. Text formatting: fonts, sizes, emphasis, colour.
    d = Document()
    p = d.add_paragraph()
    for font, size in (("Liberation Serif", 11), ("Liberation Sans", 14), ("Liberation Mono", 9), ("DejaVu Sans", 18)):
        r = p.add_run(f"{font} {size}pt sample. ")
        r.font.name, r.font.size = font, Pt(size)
    p = d.add_paragraph()
    p.add_run("Bold ").bold = True
    p.add_run("Italic ").italic = True
    p.add_run("Underline ").underline = True
    r = p.add_run("Strikethrough ")
    r.font.strike = True
    r = p.add_run("RedColour")
    r.font.color.rgb = RGBColor(0xC0, 0x10, 0x10)
    save(d, "fmt_text", words=["Bold", "Italic", "Underline", "Strikethrough", "RedColour", "Liberation", "Mono"], pages=1,
         fonts=["LiberationSerif", "LiberationSans", "LiberationMono", "DejaVuSans"], colors=["c01010"])

    # 2. Alignment, spacing, line spacing.
    d = Document()
    for align, word in ((WD_ALIGN_PARAGRAPH.LEFT, "LeftAligned"), (WD_ALIGN_PARAGRAPH.CENTER, "Centered"),
                        (WD_ALIGN_PARAGRAPH.RIGHT, "RightAligned"), (WD_ALIGN_PARAGRAPH.JUSTIFY, "Justified")):
        p = d.add_paragraph(f"{word} " + LOREM * 2)
        p.alignment = align
        p.paragraph_format.space_after = Pt(18)
        p = d.add_paragraph(f"Short{word}")  # one line: where it starts shows the alignment
        p.alignment = align
    p = d.add_paragraph("DoubleSpaced " + LOREM * 3)
    p.paragraph_format.line_spacing_rule = WD_LINE_SPACING.DOUBLE
    save(d, "fmt_alignment", words=["LeftAligned", "Centered", "RightAligned", "Justified", "DoubleSpaced"], pages=1,
         alignment={"ShortCentered": "center", "ShortRightAligned": "right", "ShortLeftAligned": "left"})

    # 3. Page size, margins, landscape section.
    d = Document()
    s = d.sections[0]
    s.page_width, s.page_height = Cm(21), Cm(29.7)
    s.left_margin = s.right_margin = Cm(4)
    d.add_paragraph("PortraitA4Page with four centimetre margins. " + LOREM)
    s2 = d.add_section(WD_SECTION.NEW_PAGE)
    s2.orientation = WD_ORIENT.LANDSCAPE
    s2.page_width, s2.page_height = Cm(29.7), Cm(21)
    d.add_paragraph("LandscapeSection " + LOREM)
    s3 = d.add_section(WD_SECTION.NEW_PAGE)
    s3.orientation = WD_ORIENT.PORTRAIT
    s3.page_width, s3.page_height = Inches(8.5), Inches(11)
    d.add_paragraph("LetterSection " + LOREM)
    save(d, "page_setup", words=["PortraitA4Page", "LandscapeSection", "LetterSection"], pages=3,
         page_sizes=[[595, 842], [842, 595], [612, 792]])

    # 4. Headers, footers, page numbers, page breaks.
    d = Document()
    d.sections[0].header.paragraphs[0].text = "RunningHeaderText"
    fp = d.sections[0].footer.paragraphs[0]
    fp.text = "Page "
    field(fp, "PAGE")
    for i in range(1, 4):
        d.add_paragraph(f"BodyPage{i} " + LOREM)
        if i < 3:
            d.add_paragraph().add_run().add_break(WD_BREAK.PAGE)
    save(d, "header_footer", words=["RunningHeaderText", "BodyPage1", "BodyPage2", "BodyPage3"], pages=3,
         per_page_words={"1": ["RunningHeaderText", "BodyPage1"], "3": ["RunningHeaderText", "BodyPage3"]},
         page_numbers=True)

    # 5. Two columns.
    d = Document()
    cols = d.sections[0]._sectPr.xpath("./w:cols")
    c = cols[0] if cols else OxmlElement("w:cols")
    c.set(qn("w:num"), "2")
    c.set(qn("w:space"), "720")
    if not cols:
        d.sections[0]._sectPr.append(c)
    for i in range(12):
        d.add_paragraph(f"ColumnPara{i} " + LOREM)
    save(d, "columns", words=[f"ColumnPara{i}" for i in range(12)], pages=1, columns=2)

    # 6. Tables: merged cells, nested table, shading, a table across pages.
    d = Document()
    t = d.add_table(rows=4, cols=4)
    t.style = "Table Grid"
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    for r in range(4):
        for c in range(4):
            t.cell(r, c).text = f"R{r}C{c}"
    a = t.cell(0, 0).merge(t.cell(0, 1))
    a.text = "MergedHorizontal"
    b = t.cell(1, 3).merge(t.cell(3, 3))
    b.text = "MergedVertical"
    shading(t.cell(2, 0), "FFE699")
    inner = t.cell(2, 1).add_table(rows=2, cols=2)
    for r in range(2):
        for c in range(2):
            inner.cell(r, c).text = f"Inner{r}{c}"
    d.add_paragraph("LongTableFollows")
    big = d.add_table(rows=80, cols=3)
    big.style = "Table Grid"
    for r in range(80):
        for c in range(3):
            big.cell(r, c).text = f"{r * 3 + c + 1000}"
    save(d, "tables", words=["MergedHorizontal", "MergedVertical", "Inner00", "Inner11", "R3C0", "LongTableFollows"],
         numbers=[str(n) for n in range(1000, 1240)], min_pages=2)

    # 7. Pictures: inline and floating (anchored, square wrap).
    d = Document()
    img = picture(os.path.join(OUT, "_inline.png"), label="INLINE")
    d.add_paragraph("InlinePictureBelow")
    d.add_picture(img, width=Cm(8))
    p = d.add_paragraph("FloatingPictureAnchor " + LOREM * 4)
    run = p.add_run()
    run.add_picture(picture(os.path.join(OUT, "_float.png"), label="FLOAT"), width=Cm(5))
    inline = run._r.xpath(".//wp:inline")[0]
    anchor = parse_xml(
        '<wp:anchor {} distT="0" distB="0" distL="114300" distR="114300" simplePos="0" relativeHeight="2" behindDoc="0" '
        'locked="0" layoutInCell="1" allowOverlap="1"><wp:simplePos x="0" y="0"/>'
        '<wp:positionH relativeFrom="margin"><wp:align>right</wp:align></wp:positionH>'
        '<wp:positionV relativeFrom="paragraph"><wp:posOffset>0</wp:posOffset></wp:positionV>'
        '<wp:extent cx="{}" cy="{}"/><wp:wrapSquare wrapText="bothSides"/></wp:anchor>'.format(
            nsdecls("wp"), inline.extent.cx, inline.extent.cy))
    for child in list(inline):
        if child.tag not in (qn("wp:extent"),):
            anchor.append(child)
    inline.getparent().replace(inline, anchor)
    os.remove(os.path.join(OUT, "_inline.png"))
    os.remove(os.path.join(OUT, "_float.png"))
    save(d, "pictures", words=["InlinePictureBelow", "FloatingPictureAnchor"], images=2, pages=1)

    # 8. Lists: bullets, numbers, multilevel.
    d = Document()
    for i in range(3):
        d.add_paragraph(f"BulletItem{i}", style="List Bullet")
    for i in range(3):
        d.add_paragraph(f"NumberItem{i}", style="List Number")
    d.add_paragraph("LevelTwoItem", style="List Number 2")
    d.add_paragraph("LevelThreeBullet", style="List Bullet 3")
    save(d, "lists", words=["BulletItem0", "NumberItem2", "LevelTwoItem", "LevelThreeBullet"], pages=1,
         markers=["1.", "2.", "3."])

    # 9. Hyperlinks.
    d = Document()
    p = d.add_paragraph("Visit ")
    hyperlink(p, "https://example.com/audit", "ExampleLinkText")
    save(d, "hyperlinks", words=["Visit", "ExampleLinkText"], links=["https://example.com/audit"], pages=1)

    # 10. Unicode and languages.
    d = Document()
    for text in ("Ελληνικά κείμενο", "Русский текст", "中文文本测试", "日本語のテキスト", "한국어 텍스트",
                 "Ünïcødé àçcéñts ß", "∑ ∫ √ ∞ ≠ ≤ → ©®™ €£¥", "עברית", "العربية"):
        d.add_paragraph(text)
    save(d, "unicode", words=["Ελληνικά", "Русский", "中文文本测试", "日本語のテキスト", "한국어", "Ünïcødé", "€£¥"], pages=1)

    # 11. Footnotes and endnotes (python-docx has no API: write the parts).
    d = Document()
    d.add_paragraph("FootnoteAnchor sentence.")
    d.add_paragraph("EndnoteAnchor sentence.")
    path = os.path.join(OUT, "notes.docx")
    d.save(path)
    add_notes(path)
    # LibreOffice (and ONLYOFFICE) start endnotes on a page of their own; Word puts them after the text.
    expect("notes", words=["FootnoteAnchor", "EndnoteAnchor", "FootnoteBodyText", "EndnoteBodyText"], min_pages=1,
           per_page_words={"1": ["FootnoteAnchor", "FootnoteBodyText"]})

    # 12. Equation (OMML), text box, watermark, page border, background.
    d = Document()
    p = d.add_paragraph("EquationFollows ")
    p._p.append(parse_xml(
        '<m:oMath xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><m:r><m:t>E=m</m:t></m:r>'
        '<m:sSup><m:e><m:r><m:t>c</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath>'))
    p = d.add_paragraph("TextBoxAnchor ")
    p._p.append(parse_xml(
        '<w:r {}><mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">'
        '<mc:Fallback><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml" style="width:200pt;height:50pt" '
        'strokecolor="#2F5597" strokeweight="1.5pt"><v:textbox><w:txbxContent><w:p><w:r><w:t>TextBoxContent</w:t></w:r>'
        '</w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r>'.format(nsdecls("w"))))
    hdr = d.sections[0].header.paragraphs[0]
    hdr._p.append(parse_xml(
        '<w:r {}><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml" type="#_x0000_t136" '
        'style="position:absolute;margin-left:0;margin-top:0;width:400pt;height:100pt;rotation:315;'
        'mso-position-horizontal:center;mso-position-horizontal-relative:margin;mso-position-vertical:center;'
        'mso-position-vertical-relative:margin" fillcolor="silver" stroked="f"><v:fill opacity=".5"/>'
        '<v:textpath style="font-family:&quot;Liberation Sans&quot;;font-size:1pt" string="DRAFTMARK"/></v:shape>'
        '</w:pict></w:r>'.format(nsdecls("w"))))
    bp = d.add_paragraph("BorderedShadedParagraph")
    bp._p.get_or_add_pPr().append(parse_xml(
        '<w:pBdr {}><w:top w:val="single" w:sz="12" w:space="1" w:color="FF0000"/>'
        '<w:bottom w:val="single" w:sz="12" w:space="1" w:color="FF0000"/></w:pBdr>'.format(nsdecls("w"))))
    shading(bp, "DDEBF7")
    save(d, "objects", words=["EquationFollows", "TextBoxAnchor", "TextBoxContent", "BorderedShadedParagraph"],
         any_words=["DRAFTMARK"], pages=1)

    # 13. Long document: 150+ pages with headings.
    d = Document()
    for ch in range(1, 41):
        d.add_heading(f"Chapter {ch} Heading", level=1)
        for k in range(12):
            d.add_paragraph(f"Chapter{ch}Para{k} " + LOREM * 2)
    save(d, "long_document", words=["Chapter1Para0", "Chapter40Para11"], min_pages=40)


def add_notes(path):
    """Adds a footnote and an endnote to a python-docx file (it has no API for them)."""
    w = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'

    def notes(kind, text):
        sep = (f'<w:{kind} w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:{kind}>'
               f'<w:{kind} w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:{kind}>')
        ref = "footnoteRef" if kind == "footnote" else "endnoteRef"
        return (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:{kind}s {w}>{sep}'
                f'<w:{kind} w:id="1"><w:p><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:{ref}/></w:r>'
                f'<w:r><w:t xml:space="preserve"> {text}</w:t></w:r></w:p></w:{kind}></w:{kind}s>')

    with zipfile.ZipFile(path) as z:
        parts = {n: z.read(n) for n in z.namelist()}
    doc = parts["word/document.xml"].decode()
    doc = doc.replace("FootnoteAnchor sentence.</w:t></w:r>",
                      'FootnoteAnchor sentence.</w:t></w:r><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr>'
                      '<w:footnoteReference w:id="1"/></w:r>', 1)
    doc = doc.replace("EndnoteAnchor sentence.</w:t></w:r>",
                      'EndnoteAnchor sentence.</w:t></w:r><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr>'
                      '<w:endnoteReference w:id="1"/></w:r>', 1)
    parts["word/document.xml"] = doc.encode()
    parts["word/footnotes.xml"] = notes("footnote", "FootnoteBodyText").encode()
    parts["word/endnotes.xml"] = notes("endnote", "EndnoteBodyText").encode()
    ct = parts["[Content_Types].xml"].decode().replace(
        "</Types>",
        '<Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/>'
        '<Override PartName="/word/endnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml"/></Types>')
    parts["[Content_Types].xml"] = ct.encode()
    rels = parts["word/_rels/document.xml.rels"].decode().replace(
        "</Relationships>",
        '<Relationship Id="rIdFn" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes" Target="footnotes.xml"/>'
        '<Relationship Id="rIdEn" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/endnotes" Target="endnotes.xml"/></Relationships>')
    parts["word/_rels/document.xml.rels"] = rels.encode()
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for n, b in parts.items():
            z.writestr(n, b)


def pdf_corpus():
    from reportlab.lib.pagesizes import A4, landscape, letter
    from reportlab.lib.units import cm
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont
    from reportlab.pdfgen import canvas

    pdfmetrics.registerFont(TTFont("DejaVu", FONT))
    pdfmetrics.registerFont(TTFont("DejaVuSerif", "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf"))

    def new(name, size=A4):
        return canvas.Canvas(os.path.join(OUT, name + ".pdf"), pagesize=size)

    # Digital text, multi-page, headers/footers, page numbers, hyperlink.
    c = new("pdf_digital")
    for page in range(1, 4):
        c.setFont("Helvetica", 9)
        c.drawString(2 * cm, 28.5 * cm, "PdfRunningHeader")
        c.drawCentredString(10.5 * cm, 1.2 * cm, f"Page {page} of 3")
        c.setFont("Times-Bold", 18)
        c.drawString(2 * cm, 26.5 * cm, f"Section {page} Title")
        c.setFont("Times-Roman", 11)
        y = 25 * cm
        for i in range(30):
            c.drawString(2 * cm, y, f"DigitalLine{page}x{i} the quick brown fox jumps over the lazy dog.")
            y -= 0.7 * cm
        if page == 1:
            c.setFillColorRGB(0, 0, 0.8)
            c.drawString(2 * cm, 3 * cm, "PdfLinkText")
            c.linkURL("https://example.com/pdf", (2 * cm, 2.9 * cm, 5 * cm, 3.4 * cm))
            c.setFillColorRGB(0, 0, 0)
        c.showPage()
    c.save()
    expect("pdf_digital", words=["PdfRunningHeader", "DigitalLine1x0", "DigitalLine3x29", "PdfLinkText", "Section"],
           pages=3, links=["https://example.com/pdf"])

    # Table with ruled grid.
    c = new("pdf_table")
    c.setFont("Helvetica-Bold", 14)
    c.drawString(2 * cm, 27 * cm, "QuarterlyTable")
    x0, y0, cw, rh = 2 * cm, 25.5 * cm, 4 * cm, 0.9 * cm
    rows = [["Region", "Q1", "Q2", "Q3"]] + [[f"Region{r}", str(100 + r), str(200 + r), str(300 + r)] for r in range(8)]
    for r, row in enumerate(rows):
        for k, val in enumerate(row):
            c.rect(x0 + k * cw, y0 - r * rh, cw, rh)
            c.setFont("Helvetica-Bold" if r == 0 else "Helvetica", 10)
            c.drawString(x0 + k * cw + 0.2 * cm, y0 - r * rh + 0.3 * cm, val)
    c.save()
    expect("pdf_table", words=["QuarterlyTable", "Region", "Region7"], numbers=["100", "207", "307"], pages=1, table_cells=36)

    # Two columns: reading order must be left column, then right.
    c = new("pdf_columns")
    c.setFont("Times-Roman", 10)
    for col, x in enumerate((2 * cm, 11 * cm)):
        y = 27 * cm
        for i in range(40):
            c.drawString(x, y, f"Col{col}Line{i:02d} lorem ipsum")
            y -= 0.6 * cm
    c.save()
    expect("pdf_columns", words=["Col0Line00", "Col1Line39"], pages=1, order=["Col0Line00", "Col0Line39", "Col1Line00", "Col1Line39"])

    # Landscape + mixed page sizes, rotated text, image, watermark, background.
    c = new("pdf_mixed", landscape(A4))
    c.setFillColorRGB(0.93, 0.95, 1)
    c.rect(0, 0, *landscape(A4), fill=1, stroke=0)
    c.setFillColorRGB(0, 0, 0)
    c.setFont("DejaVu", 14)
    c.drawString(2 * cm, 18 * cm, "LandscapePageText with Ünïcødé Ελληνικά")
    c.drawImage(picture(os.path.join(OUT, "_p.png"), label="PDFIMG"), 2 * cm, 6 * cm, 9 * cm, 6 * cm)
    c.saveState()
    c.translate(20 * cm, 5 * cm)
    c.rotate(90)
    c.drawString(0, 0, "RotatedText")
    c.restoreState()
    c.saveState()
    c.setFillColorRGB(0.8, 0.8, 0.8)
    c.setFont("Helvetica-Bold", 60)
    c.translate(15 * cm, 10 * cm)
    c.rotate(30)
    c.drawCentredString(0, 0, "WATERMARK")
    c.restoreState()
    c.showPage()
    c.setPageSize(letter)
    c.setFont("DejaVuSerif", 12)
    c.drawString(2 * cm, 25 * cm, "LetterSizedPage")
    c.save()
    os.remove(os.path.join(OUT, "_p.png"))
    expect("pdf_mixed", words=["LandscapePageText", "Ünïcødé", "Ελληνικά", "LetterSizedPage", "RotatedText"], pages=2,
           images=1, page_sizes=[[842, 595], [612, 792]])

    # Form with checkboxes and a text field.
    c = new("pdf_form")
    c.setFont("Helvetica", 12)
    c.drawString(2 * cm, 27 * cm, "FormTitle")
    c.drawString(2 * cm, 25.5 * cm, "NameLabel")
    c.acroForm.textfield(name="name", x=5 * cm, y=25.2 * cm, width=8 * cm, height=0.8 * cm, value="FilledName")
    c.drawString(2 * cm, 24 * cm, "AgreeLabel")
    c.acroForm.checkbox(name="agree", x=5 * cm, y=23.8 * cm, size=14, checked=True)
    c.save()
    expect("pdf_form", words=["FormTitle", "NameLabel", "AgreeLabel"], pages=1)

    # Scanned: a page that is only a picture of text, with and without an OCR layer, and mixed with a digital page.
    scan = Image.new("L", (1240, 1754), 255)
    d = ImageDraw.Draw(scan)
    f = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf", 34)
    for i in range(25):
        d.text((120, 150 + i * 56), f"ScannedLine{i:02d} printed on paper and scanned", fill=0, font=f)
    rnd = random.Random(1)
    px = scan.load()
    for _ in range(20000):  # scanner noise
        px[rnd.randrange(1240), rnd.randrange(1754)] = rnd.randrange(150, 255)
    scan = scan.rotate(0.6, fillcolor=255)
    scan_path = os.path.join(OUT, "_scan.png")
    scan.save(scan_path)
    c = new("pdf_scanned")
    c.drawImage(scan_path, 0, 0, *A4)
    c.save()
    expect("pdf_scanned", pages=1, images=1, scanned=True, words=["ScannedLine00", "ScannedLine24"])
    c = new("pdf_scanned_ocr")
    c.drawImage(scan_path, 0, 0, *A4)
    t = c.beginText()
    t.setTextRenderMode(3)  # invisible OCR layer
    t.setFont("Helvetica", 16)
    for i in range(25):
        t.setTextOrigin(120 * A4[0] / 1240, A4[1] - (150 + i * 56 + 30) * A4[1] / 1754)
        t.textLine(f"ScannedLine{i:02d} printed on paper and scanned")
    c.drawText(t)
    c.save()
    # The DOCX gets the OCR layer as text, not the noisy scan: compared by words and line positions, not pixels.
    expect("pdf_scanned_ocr", pages=1, words=["ScannedLine00", "ScannedLine24"], ocr_text=True,
           order=[f"ScannedLine{i:02d}" for i in range(25)])
    c = new("pdf_mixed_scan")
    c.setFont("Helvetica", 12)
    c.drawString(2 * cm, 27 * cm, "DigitalFirstPage")
    c.showPage()
    c.drawImage(scan_path, 0, 0, *A4)
    c.save()
    os.remove(scan_path)
    expect("pdf_mixed_scan", pages=2, words=["DigitalFirstPage"], images=1)

    # Large: 300 pages.
    c = new("pdf_large")
    for page in range(300):
        c.setFont("Helvetica", 10)
        for i in range(50):
            c.drawString(2 * cm, 28 * cm - i * 0.5 * cm, f"P{page}L{i} large document line")
        c.showPage()
    c.save()
    expect("pdf_large", pages=300, words=["P0L0", "P299L49"])


def image_corpus():
    picture(os.path.join(OUT, "img_rgb.png"))
    picture(os.path.join(OUT, "img_rgba.png"), transparent=True)
    Image.open(os.path.join(OUT, "img_rgb.png")).convert("L").save(os.path.join(OUT, "img_gray.png"))
    Image.open(os.path.join(OUT, "img_rgb.png")).convert("P", palette=Image.ADAPTIVE).save(os.path.join(OUT, "img_palette.png"))
    rgb = Image.open(os.path.join(OUT, "img_rgb.png"))
    rgb.save(os.path.join(OUT, "img_rgb.jpg"), quality=95)
    rgb.convert("CMYK").save(os.path.join(OUT, "img_cmyk.jpg"), quality=95)
    rgb.convert("L").save(os.path.join(OUT, "img_gray.jpg"), quality=95)
    # EXIF orientation 6: stored 600×400, shown rotated 90° (400×600).
    exif = Image.Exif()
    exif[0x0112] = 6
    exif[0x010F] = "AuditCam"
    rgb.save(os.path.join(OUT, "img_exif_rot.jpg"), quality=95, exif=exif.tobytes())
    Image.open(os.path.join(OUT, "img_rgba.png")).save(os.path.join(OUT, "img_rgba.webp"), lossless=True)
    rgb.save(os.path.join(OUT, "img_rgb.webp"), quality=90)
    # ICC profile (sRGB from Pillow's ImageCms) on a JPEG.
    try:
        from PIL import ImageCms
        icc = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
        rgb.save(os.path.join(OUT, "img_icc.jpg"), quality=95, icc_profile=icc)
    except Exception:
        pass
    picture(os.path.join(OUT, "img_large.png"), w=8000, h=6000)
    picture(os.path.join(OUT, "img_odd.png"), w=1, h=997)
    picture(os.path.join(OUT, "img_wide.png"), w=5000, h=3)
    for name in os.listdir(OUT):
        if name.startswith("img_"):
            pass
    # Corrupt and mislabelled files.
    with open(os.path.join(OUT, "bad_truncated.png"), "wb") as f:
        f.write(open(os.path.join(OUT, "img_rgb.png"), "rb").read()[:300])
    with open(os.path.join(OUT, "bad_random.jpg"), "wb") as f:
        f.write(os.urandom(4096))
    with open(os.path.join(OUT, "bad_empty.webp"), "wb"):
        pass
    with open(os.path.join(OUT, "bad_notreally.pdf"), "w") as f:
        f.write("this is plain text pretending to be a PDF\n")
    with open(os.path.join(OUT, "bad_truncated.docx"), "wb") as f:
        f.write(open(os.path.join(OUT, "fmt_text.docx"), "rb").read()[:500])
    # A PNG that is really a JPEG (wrong extension).
    rgb.save(os.path.join(OUT, "mislabeled_jpeg.png"), "JPEG")


def special_names():
    import shutil
    shutil.copy(os.path.join(OUT, "fmt_text.docx"), os.path.join(OUT, "ünïcødé 名前 (copy) #1.docx"))
    expect("ünïcødé 名前 (copy) #1", words=["Bold", "Italic"], pages=1)
    shutil.copy(os.path.join(OUT, "img_rgba.png"), os.path.join(OUT, "spaces and 'quotes'.png"))


def encrypted():
    import subprocess
    src = os.path.join(OUT, "pdf_digital.pdf")
    subprocess.run(["qpdf", "--encrypt", "secret", "owner", "256", "--", src, os.path.join(OUT, "pdf_password.pdf")], check=True)
    expect("pdf_password", password_protected=True)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    docx_corpus()
    pdf_corpus()
    image_corpus()
    special_names()
    encrypted()
    print("corpus:", len(os.listdir(OUT)), "files in", OUT)
