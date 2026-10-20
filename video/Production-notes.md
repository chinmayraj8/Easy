# Easy — product showcase

Duration: 2 minutes 55 seconds. Full HD, 1920 × 1080, 30 fps.

## Chapters and narration

### 00:00 — Meet Easy.

Meet Easy. A desktop file converter that brings the right format to your cursor. Built on the open source Filee project, this customized Mac app turns everyday conversion into a simple gesture.

### 00:11 — A gesture. A new format.

Hold Option, drag a file, and choose a format from the ring. Drop onto your choice, and Easy starts converting. The available choices adapt to your files, so your workflow stays focused.

### 00:22 — One workflow. Many file types.

Images, documents, spreadsheets, presentations, and PDFs share one workflow. The engine catalog also covers e books, archives, subtitles, fonts, structured data, CAD drawings, and three dimensional models. Support depends on the format, route, and available engine.

### 00:40 — Make everyday files useful.

Turn images into web friendly formats, resize them, or build a PDF. Merge documents, split pages, select a page range, or compress a PDF. Extract text or page images for the next step in your work.

### 00:53 — Office documents, carefully rendered.

Easy adds an ONLYOFFICE integration. With Desktop Editors installed and opened once on your Mac, Word, PowerPoint, and Excel files can become PDFs through its rendering engine. Font checks and conversion logs help identify substitutions that may affect the result.

### 01:09 — From fixed pages to editable content.

For PDF to Word, Easy rebuilds positioned text into paragraphs, headings, and tables. The result is a document you can edit. Layout can reflow, and equations become text with subscript and superscript, rather than editable equation objects.

### 01:23 — Your media. The format you need.

Add FFmpeg for video and audio. Convert video formats, extract audio, create animated GIFs, or export frames. Presets control quality, resolution, and compatible codecs. Text subtitles can also be converted, with hardware encoding available for supported setups.

### 01:41 — Set it once. Use it again.

Create a toolbar for each file type. Reorder its options, then save presets for quality, size, PDF pages, and compression. Choose the output folder and naming rules. Light and dark themes let the workspace match your preferences.

### 01:55 — From a few files to a full workflow.

Drop several files to process them in parallel. Watch folders automatically convert new arrivals. The filee command line exposes the same engines and presets for scripts. Progress and recent conversions help you follow the work from start to finish.

### 02:09 — Smart routing behind the gesture.

Under the hood, Easy plans a route between formats, including conversions with intermediate steps. A job queue manages engine concurrency, and engine priority resolves competing choices. Built in, bundled, and optional tools work together behind the same interface.

### 02:24 — Your files stay on your Mac.

Conversion runs locally, without uploading your files. Once the required engines are available, conversion works offline. Results normally appear beside the originals, with configurable destinations. Source files are protected from overwrite. Optional engine downloads still need an internet connection.

### 02:41 — Less file friction. More flow.

Easy combines quick conversions, reusable presets, and automation, with added document handling for the Mac. From one image to a folder of reports: choose your format, keep control of your files, and get back to your work.

## Production notes

Custom animated illustrations, not a recording of the live app. English synthetic narration uses the installed macOS Samantha voice. The quiet ambient score is synthesized from original sine-wave chords; no external music samples are used. English captions are included as an optional MP4 subtitle track and a separate SRT file. Caption timings are estimated at phrase level.

## Fact sources

- `CUSTOMIZATION.md`: Easy identity, macOS scope, ONLYOFFICE integration and PDF reconstruction limitations.
- `README.md`: inherited core workflow and capability families.
- `docs/ARCHITECTURE.md`: routing, queue, presets, watch folders and original-file protection.
- `docs/ENGINES.md`: format coverage and optional-engine requirements.
- `src/Filee.Engines/Office/OnlyOfficeConverter.cs`: supported Office inputs, macOS discovery and first-launch font requirement.

The video describes the local repository, not a newly tested release. It avoids claiming that every format converts to every other format or that PDF reconstruction guarantees identical pagination. It does not imply that the added ONLYOFFICE integration is bundled.

## Editable production files

`source/scenes.json` contains the narration and scene data. `source/render.py` generates motion graphics, original background audio, subtitles, previews and the final MP4. `audio/00.aiff` through `audio/11.aiff` contain the narration sections. The renderer needs Python with Pillow, NumPy and imageio-ffmpeg, plus the listed macOS Arial fonts. Update the imageio-ffmpeg import path in the script if rebuilding elsewhere.

Built on Filee by Myeonggu Kim (KnifeLemon) and Filee contributors. Easy is a customized build and is not affiliated with or endorsed by Filee.