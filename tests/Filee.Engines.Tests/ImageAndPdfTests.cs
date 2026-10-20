using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class ImageAndPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        Assert.All(job.Outputs, o => Assert.True(new FileInfo(o).Length > 0, o));
    }

    [Theory]
    [InlineData("jpg", MagickFormat.Jpeg)]
    [InlineData("webp", MagickFormat.WebP)]
    [InlineData("tiff", MagickFormat.Tiff)]
    [InlineData("bmp", MagickFormat.Bmp)]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("avif", MagickFormat.Avif)]
    public async Task Png_converts_to(string target, MagickFormat expected)
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([source], new Preset { Name = target, TargetFormat = target });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal(expected, result.Format);
        Assert.Equal(320u, result.Width);
    }

    /// <summary>A 3-frame animated GIF (red, green, blue; 20, 30 and 40 hundredths of a second, looping).</summary>
    private static string MakeAnimatedGif(string folder)
    {
        using var frames = new MagickImageCollection();
        foreach (var (color, delay) in new[] { (MagickColors.Red, 20u), (MagickColors.Lime, 30u), (MagickColors.Blue, 40u) })
            frames.Add(new MagickImage(color, 64, 48) { AnimationDelay = delay });
        var path = Path.Combine(folder, "anim.gif");
        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Animated_gif_becomes_an_animated_webp_and_back(bool lossless)
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimatedGif(dir);

        var toWebp = await fx.ConvertAsync([gif], new Preset { TargetFormat = "webp", Image = { WebpLossless = lossless } });

        AssertDone(toWebp);
        var webp = toWebp.Outputs.Single();
        using (var frames = new MagickImageCollection(webp))
        {
            Assert.Equal(3, frames.Count);
            Assert.All(frames, f => Assert.Equal(MagickFormat.WebP, f.Format));
            Assert.Equal([20u, 30u, 40u], frames.Select(f => f.AnimationDelay));
            Assert.True(frames[1].GetPixels().GetPixel(32, 24).ToColor()!.G > 200, "second frame should be green");
        }

        var back = await fx.ConvertAsync([webp], new Preset { TargetFormat = "gif", Output = { FileNamePattern = "{name}_back" } });

        AssertDone(back);
        using var again = new MagickImageCollection(back.Outputs.Single());
        Assert.Equal(3, again.Count);
        Assert.Equal([20u, 30u, 40u], again.Select(f => f.AnimationDelay));
    }

    [Fact]
    public async Task Animated_gif_to_a_still_format_takes_the_first_frame()
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimatedGif(dir);

        var job = await fx.ConvertAsync([gif], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var png = new MagickImage(job.Outputs.Single());
        Assert.True(png.GetPixels().GetPixel(32, 24).ToColor()!.R > 200, "first frame is red");
    }

    [Fact]
    public async Task Multi_page_tiff_to_webp_writes_one_image_per_page()
    {
        var dir = fx.NewFolder();
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.Red, 64, 48));
            pages.Add(new MagickImage(MagickColors.Blue, 64, 48));
            pages.Write(Path.Combine(dir, "pages.tiff"), MagickFormat.Tiff);
        }

        var job = await fx.ConvertAsync([Path.Combine(dir, "pages.tiff")], new Preset { TargetFormat = "webp" });

        AssertDone(job);
        Assert.Equal(2, job.Outputs.Count());
    }

    [Theory]
    [InlineData(true, true, true)]    // all metadata kept
    [InlineData(false, true, false)]  // metadata removed, colour profile kept
    [InlineData(false, false, false)] // everything removed
    public async Task Removing_metadata_can_keep_the_colour_profile(bool keepMetadata, bool keepColorProfile, bool exifExpected)
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "photo.jpg");
        using (var image = new MagickImage(MagickColors.SkyBlue, 64, 48))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Model, "TestPhone");
            image.SetProfile(exif);
            // A wide-gamut profile, like Display P3 from a phone (an sRGB profile would just become PNG's sRGB chunk).
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.Write(source, MagickFormat.Jpeg);
        }

        var job = await fx.ConvertAsync([source], new Preset
        {
            TargetFormat = "webp",
            Image = { KeepMetadata = keepMetadata, KeepColorProfile = keepColorProfile },
        });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal(exifExpected, result.GetExifProfile() is not null);
        Assert.Equal(keepMetadata || keepColorProfile, result.GetColorProfile() is not null);
    }

    [Fact]
    public async Task Jpg_output_flattens_transparency_and_applies_quality()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "jpg", Image = { Quality = 40, Background = "#FF0000" } });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.False(result.HasAlpha);
        Assert.InRange(result.Quality, 35u, 45u);
        var corner = result.GetPixels().GetPixel(5, 5).ToColor()!;
        Assert.True(corner.R > 200 && corner.G < 60, "transparent corner should become the background colour");
    }

    [Fact]
    public async Task Resize_by_long_edge_and_percent()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png, 1000, 500);

        var longEdge = await fx.ConvertAsync([source], new Preset { TargetFormat = "png", Image = { Resize = ResizeMode.LongEdge, LongEdge = 400 }, Output = { FileNamePattern = "{name}_le" } });
        var percent = await fx.ConvertAsync([source], new Preset { TargetFormat = BuiltInData.SameAsSource, Image = { Resize = ResizeMode.Percent, ResizePercent = 50 }, Output = { FileNamePattern = "{name}_50" } });

        AssertDone(longEdge);
        AssertDone(percent);
        using (var a = new MagickImage(longEdge.Outputs.Single())) Assert.Equal((400u, 200u), (a.Width, a.Height));
        using (var b = new MagickImage(percent.Outputs.Single())) Assert.Equal((500u, 250u), (b.Width, b.Height));
    }

    [Fact]
    public async Task Ico_contains_several_sizes()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "logo.png", MagickFormat.Png, 300, 200);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "ico", Image = { IcoSizes = [64, 32, 16] } });

        AssertDone(job);
        using var icons = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal([64u, 32u, 16u], icons.Select(i => i.Width));
    }

    [Fact]
    public async Task Multi_page_tiff_to_png_writes_one_file_per_page()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeMultiPageTiff(dir, 3);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });

        AssertDone(job);
        Assert.Equal(["pages_p1.png", "pages_p2.png", "pages_p3.png"], job.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Images_to_pdf_and_back()
    {
        var dir = fx.NewFolder();
        var tiff = EngineFixture.MakeMultiPageTiff(dir, 2);

        var toPdf = await fx.ConvertAsync([tiff], new Preset { TargetFormat = "pdf" });
        AssertDone(toPdf);
        using (var pdf = PdfReader.Open(toPdf.Outputs.Single(), PdfDocumentOpenMode.Import))
            Assert.Equal(2, pdf.PageCount);

        var toPng = await fx.ConvertAsync([toPdf.Outputs.Single()], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72 }, Output = { FileNamePattern = "{name}_render" } });
        AssertDone(toPng);
        Assert.Equal(2, toPng.Outputs.Count());
    }

    [Fact]
    public async Task Pdf_to_multi_page_tiff()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "tiff", Pdf = { RenderDpi = 72 }, Output = { FileNamePattern = "{name}_back" } });

        AssertDone(job);
        using var pages = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public async Task Pdf_pages_stack_into_one_tall_image()
    {
        // Issue #50: an 8-page PDF gave 8 pictures; "Long image" gives one with the pages one under another.
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using var firstPage = new MagickImage((await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, PageRange = "1" } })).Outputs.Single());

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, CombinePages = true }, Output = { FileNamePattern = "{name}_long" } });

        AssertDone(job);
        Assert.Equal("pages_long.png", Path.GetFileName(job.Outputs.Single()));
        using var tall = new MagickImage(job.Outputs.Single());
        Assert.Equal(firstPage.Width, tall.Width);
        Assert.Equal(firstPage.Height * 3, tall.Height);
    }

    [Theory]
    [InlineData("jpg", 65_500)]
    [InlineData("webp", 16_383)]
    public void A_stack_taller_than_the_format_stores_is_scaled_down(string target, int limit)
    {
        // 40 A4 pages at 300 dpi: about 140,000 px tall.
        var pages = Enumerable.Range(0, 40).Select(_ => (IMagickImage<byte>)new MagickImage(MagickColors.SkyBlue, 2480, 3508)).ToList();
        try
        {
            using var tall = Filee.Engines.Pdf.PdfiumConverter.Stack(pages);
            Filee.Engines.Magick.ImageEncoder.ApplyOptions(tall, new ImageOptions(), target);
            Assert.True(tall.Height <= limit, $"{tall.Height} px");
            Assert.True((long)tall.Width * tall.Height <= Filee.Engines.Pdf.PdfiumConverter.MaxStackedPixels);
            Assert.InRange(tall.Width / (double)tall.Height, 2480 / (3508.0 * 40) * 0.95, 2480 / (3508.0 * 40) * 1.05); // pages keep their shape
        }
        finally
        {
            pages.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task An_image_taller_than_a_jpg_holds_is_scaled_down_instead_of_failing()
    {
        // Issue #50: a 1200 × 74,673 px long PNG → JPG failed with "could not write the file".
        var dir = fx.NewFolder();
        var png = Path.Combine(dir, "long.png");
        using (var source = new MagickImage(MagickColors.SkyBlue, 12, 70_000))
            source.Write(png, MagickFormat.Png);

        var job = await fx.ConvertAsync([png], new Preset { TargetFormat = "jpg" });

        AssertDone(job);
        using var jpg = new MagickImage(job.Outputs.Single());
        Assert.InRange(jpg.Height, 60_000u, 65_500u);
    }

    [Fact]
    public void Width_resize_shrinks_wide_images_and_leaves_narrow_ones()
    {
        var options = new ImageOptions { Resize = ResizeMode.Width, Width = 1200 };
        using var wide = new MagickImage(MagickColors.White, 3000, 1000);
        using var narrow = new MagickImage(MagickColors.White, 800, 2000);

        Filee.Engines.Magick.ImageEncoder.ApplyOptions(wide, options, "jpg");
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(narrow, options, "jpg");

        Assert.Equal((1200u, 400u), (wide.Width, wide.Height));
        Assert.Equal((800u, 2000u), (narrow.Width, narrow.Height));
    }

    [Fact]
    public void A_long_image_splits_between_pages_into_parts_the_format_holds()
    {
        // Five 6,000 px pages: a WebP holds 16,383 px, so two pages per part; at half width (Width 50 px) all fit.
        var pages = Enumerable.Range(0, 5).Select(_ => (IMagickImage<byte>)new MagickImage(MagickColors.White, 100, 6000)).ToList();
        try
        {
            var parts = Filee.Engines.Pdf.PdfiumConverter.Split(pages, "webp", new ImageOptions());
            Assert.Equal([2, 2, 1], parts.Select(p => p.Count));

            var half = Filee.Engines.Pdf.PdfiumConverter.Split(pages, "webp", new ImageOptions { Resize = ResizeMode.Width, Width = 50 });
            Assert.Equal([5], half.Select(p => p.Count));
            Assert.Single(Filee.Engines.Pdf.PdfiumConverter.Split(pages, "png", new ImageOptions()));
        }
        finally
        {
            pages.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void Trimming_keeps_the_content_and_a_small_margin()
    {
        using var page = new MagickImage(MagickColors.White, 200, 1000);
        page.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(20, 400, 180, 499));
        using var blank = new MagickImage(MagickColors.White, 200, 1000);

        Filee.Engines.Pdf.PdfiumConverter.TrimVertically(page, 72); // margin 12 px at 72 dpi
        Filee.Engines.Pdf.PdfiumConverter.TrimVertically(blank, 72);

        Assert.Equal((200u, 124u), (page.Width, page.Height));
        Assert.Equal(24u, blank.Height);
    }

    [Fact]
    public async Task Trimmed_long_image_is_shorter_and_a_split_one_names_its_parts()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var full = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, CombinePages = true }, Output = { FileNamePattern = "{name}_full" } });
        var trimmed = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, CombinePages = true, TrimPageMargins = true }, Output = { FileNamePattern = "{name}_trim" } });

        AssertDone(full);
        AssertDone(trimmed);
        using var a = new MagickImage(full.Outputs.Single());
        using var b = new MagickImage(trimmed.Outputs.Single());
        Assert.Equal(a.Width, b.Width);
        Assert.True(b.Height <= a.Height, $"{b.Height} > {a.Height}");

        // A split that fits in one part writes one file without a number.
        var split = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72, CombinePages = true, SplitTallImage = true }, Output = { FileNamePattern = "{name}_long" } });
        AssertDone(split);
        Assert.Equal("pages_long.jpg", Path.GetFileName(split.Outputs.Single()));
    }

    [Fact]
    public async Task Merge_images_into_one_pdf()
    {
        var dir = fx.NewFolder();
        var a = EngineFixture.MakeImage(dir, "1.jpg", MagickFormat.Jpeg);
        var b = EngineFixture.MakeImage(dir, "2.png", MagickFormat.Png);
        var c = EngineFixture.MakeMultiPageTiff(dir, 2);

        var job = await fx.ConvertAsync([a, b, c], new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true, PageSize = PdfPageSize.A4, MarginMm = 10 }, Output = { FileNamePattern = "{name}_merged" } });

        AssertDone(job);
        using var pdf = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(4, pdf.PageCount);
        Assert.Equal("1_merged.pdf", Path.GetFileName(job.Outputs.Single()));
    }

    [Fact]
    public async Task Split_pdf_by_page_range()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 4)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { SplitPages = true, PageRange = "2-3" },
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" },
        });

        AssertDone(job);
        Assert.Equal(["pages_p2.pdf", "pages_p3.pdf"], job.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Split_pdf_into_chosen_parts()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 5)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { SplitPages = true, SplitParts = "1-2, 3, 4-" },
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" },
        });

        AssertDone(job);
        Assert.Equal(["pages_p1-2.pdf", "pages_p3.pdf", "pages_p4-5.pdf"], job.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using var last = PdfReader.Open(job.Outputs.Single(o => o.EndsWith("_p4-5.pdf")), PdfDocumentOpenMode.Import);
        Assert.Equal(2, last.PageCount);
    }

    [Theory]
    [InlineData("3, 1-2", new[] { 2, 0, 1, 3, 4 })]
    [InlineData("5-1", new[] { 4, 3, 2, 1, 0 })]
    [InlineData("4-, 2, 2, x, 99", new[] { 3, 4, 1, 0, 2 })]
    [InlineData("", new[] { 0, 1, 2, 3, 4 })]
    public void Reordered_pages_take_each_page_once(string order, int[] expected) =>
        Assert.Equal(expected, PdfSharpConverter.Reordered(order, 5));

    [Theory]
    [InlineData("3@90, 1-2", false, "2@90 0 1 3 4")]
    [InlineData("3,1", true, "2 0")]
    [InlineData("2@270, 4@-90, 5@180, 1@45", true, "1@270 3@270 4@180 0")] // -90 is 270; 45 isn't a turn: not turned
    [InlineData("5-4@90", true, "4@90 3@90")]
    [InlineData("", true, "0 1 2 3 4")] // nothing listed: nothing is dropped
    public void Arranged_pages_can_be_turned_and_left_out(string order, bool dropUnlisted, string expected) =>
        Assert.Equal(expected, string.Join(' ', PdfSharpConverter.Arranged(order, 5, dropUnlisted)
            .Select(p => p.Rotation == 0 ? $"{p.Index}" : $"{p.Index}@{p.Rotation}")));

    [Fact]
    public async Task Edited_pdf_drops_and_turns_pages()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { Reorder = true, PageOrder = "3@90,1", DropUnlisted = true },
        });

        AssertDone(job);
        using var result = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(90, result.Pages[0].Rotate);
        Assert.Equal(0, result.Pages[1].Rotate);
    }

    [Fact]
    public async Task Reorder_pdf_keeps_every_page()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { Reorder = true, PageOrder = "3-1" },
            Output = { FileNamePattern = "{name}_reordered" },
        });

        AssertDone(job);
        using var result = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(3, result.PageCount);
    }

    [Fact]
    public async Task Protect_then_unlock_round_trips_and_a_wrong_password_is_refused()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 2)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var protect = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Protect = true, Password = "s3cret" } });
        AssertDone(protect);
        var locked = protect.Outputs.Single();
        Assert.ThrowsAny<Exception>(() => PdfReader.Open(locked, PdfDocumentOpenMode.Import));

        var wrong = await fx.ConvertAsync([locked], new Preset { TargetFormat = "pdf", Pdf = { Unlock = true, Password = "nope" } });
        Assert.Equal("error.wrong_password", wrong.Files.Single().ErrorKey);

        var unlock = await fx.ConvertAsync([locked], new Preset { TargetFormat = "pdf", Pdf = { Unlock = true, Password = "s3cret" } });
        AssertDone(unlock);
        using var open = PdfReader.Open(unlock.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(2, open.PageCount);
    }

    [Fact]
    public async Task Protect_without_a_password_is_refused_not_written_unprotected()
    {
        // Run from a watch folder or the CLI there is no password to ask for (it is never saved).
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 1)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Protect = true } });

        Assert.Equal("error.needs_password", job.Files.Single().ErrorKey);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public async Task Remove_info_takes_away_location_and_camera_and_keeps_the_photo_upright()
    {
        var dir = fx.NewFolder();
        var photo = Path.Combine(dir, "beach.jpg");
        using (var image = new MagickImage(MagickColors.SkyBlue, 320, 200))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Apple");
            exif.SetValue(ExifTag.Model, "iPhone 17");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(12, 1), new Rational(58, 1), new Rational(0, 1)]);
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(77, 1), new Rational(35, 1), new Rational(0, 1)]);
            exif.SetValue(ExifTag.DateTimeOriginal, "2026:10:20 09:15:00");
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            image.Orientation = OrientationType.RightTop; // taken with the phone upright: stored sideways (EXIF 6)
            image.SetProfile(exif);
            image.SetAttribute("comment", "home");
            image.Quality = 90;
            image.Write(photo);
        }

        using (var check = new MagickImage(photo))
            Assert.Equal(OrientationType.RightTop, check.Orientation);
        var job = await fx.ConvertAsync([photo], BuiltInData.CreatePresets().Single(p => p.Id == "remove-info"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("beach_clean.jpg", Path.GetFileName(output));
        using var clean = new MagickImage(output);
        var left = clean.GetExifProfile();
        Assert.True(left is null || left.Values.All(v => v.Tag != ExifTag.GPSLatitude && v.Tag != ExifTag.GPSLongitude
            && v.Tag != ExifTag.Make && v.Tag != ExifTag.Model && v.Tag != ExifTag.DateTimeOriginal), "EXIF left: " + string.Join(", ", left?.Values.Select(v => v.Tag) ?? []));
        Assert.Null(clean.GetAttribute("comment"));
        Assert.Null(clean.GetXmpProfile());
        Assert.Equal((200u, 320u), (clean.Width, clean.Height)); // turned the way it looked, so no orientation is needed
        Assert.True(new FileInfo(output).Length <= new FileInfo(photo).Length * 1.1, "not re-saved at a higher quality");
    }

    [Fact]
    public async Task Remove_info_saves_a_photo_its_own_format_cant_be_written_in_as_jpg()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Makes the HEIC with sips.");
            return;
        }
        var dir = fx.NewFolder();
        var jpg = EngineFixture.MakeImage(dir, "seed.jpg", MagickFormat.Jpeg);
        var heic = Path.Combine(dir, "IMG_0042.heic");
        using (var sips = System.Diagnostics.Process.Start("/usr/bin/sips", ["-s", "format", "heic", jpg, "--out", heic])!)
            sips.WaitForExit();

        var job = await fx.ConvertAsync([heic], BuiltInData.CreatePresets().Single(p => p.Id == "remove-info"));

        AssertDone(job);
        // HEIC needs the FFmpeg download to be written: without it the photo comes out as a JPG, with it as a HEIC.
        var expected = fx.Catalog.CreatePlanner().CanConvert("heic", "heic") ? "IMG_0042_clean.heic" : "IMG_0042_clean.jpg";
        Assert.Equal(expected, Path.GetFileName(job.Outputs.Single()));
    }

    [Fact]
    public async Task Watermark_crosses_the_middle_of_every_page_also_a_turned_one()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 2)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            document.Pages[1].Rotate = 90; // shown sideways
            document.Save(pdf);
        }

        var job = await fx.ConvertAsync([pdf], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { Watermark = true, WatermarkText = "Copy for Chinmay" },
            Output = { FileNamePattern = "{name}_watermarked" },
        });

        AssertDone(job);
        Assert.EndsWith("_watermarked.pdf", job.Outputs.Single());
        using var marked = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        Assert.Equal(2, marked.NumberOfPages);
        foreach (var page in marked.GetPages())
        {
            Assert.Contains("Copy for Chinmay", page.Text);
            Assert.Single(page.GetImages()); // the page's own content is still there
            // Across the middle, as the page is shown: from the lower left half to the upper right half.
            var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            Assert.All(letters, l => Assert.True(l.StartBaseLine.X >= 0 && l.StartBaseLine.X <= page.Width
                && l.StartBaseLine.Y >= 0 && l.StartBaseLine.Y <= page.Height, $"page {page.Number}: {l.Value} at {l.StartBaseLine}"));
            Assert.True(letters[0].StartBaseLine.X < page.Width / 2 && letters[0].StartBaseLine.Y < page.Height / 2, $"page {page.Number} starts at {letters[0].StartBaseLine}");
            Assert.True(letters[^1].StartBaseLine.X > page.Width / 2 && letters[^1].StartBaseLine.Y > page.Height / 2, $"page {page.Number} ends at {letters[^1].StartBaseLine}");
        }

        // Seen, but see-through: the middle of the page changed, and isn't covered in solid grey.
#pragma warning disable CA1416 // PDFium ships for every desktop platform the tests run on
        using var before = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(pdf), page: 0);
        using var after = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(job.Outputs.Single()), page: 0);
#pragma warning restore CA1416
        Assert.NotEqual(before.Bytes, after.Bytes);
        var changed = 0;
        for (var x = 0; x < after.Width; x += 4)
            for (var y = 0; y < after.Height; y += 4)
                if (before.GetPixel(x, y) != after.GetPixel(x, y))
                    changed++;
        var sampled = (after.Width / 4) * (after.Height / 4);
        Assert.InRange(changed, 1, sampled / 3);
    }

    [Fact]
    public async Task Watermark_without_text_says_confidential()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 1)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Watermark = true } });

        AssertDone(job);
        using var marked = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        Assert.Contains("CONFIDENTIAL", marked.GetPage(1).Text);
    }

    [Fact]
    public async Task Compress_to_a_size_never_asked_is_refused_not_saved_uncompressed()
    {
        // Run from a watch folder or the CLI there is nobody to ask for the size.
        var dir = fx.NewFolder();
        var photo = EngineFixture.MakeImage(dir, "photo.jpg", MagickFormat.Jpeg);

        var job = await fx.ConvertAsync([photo], new Preset { TargetFormat = BuiltInData.SameAsSource, MaxSizeKb = Preset.AskSize });

        Assert.Equal("error.needs_size", job.Files.Single().ErrorKey);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public async Task Compress_to_size_lowers_quality_until_the_photo_fits()
    {
        var dir = fx.NewFolder();
        var photo = Path.Combine(dir, "photo.jpg");
        using (var image = new MagickImage("plasma:", 1600, 1200))
        {
            image.Quality = 95;
            image.Write(photo);
        }
        var limitKb = (int)(new FileInfo(photo).Length / 1024 / 4);

        var job = await fx.ConvertAsync([photo], new Preset { TargetFormat = BuiltInData.SameAsSource, MaxSizeKb = limitKb });

        AssertDone(job);
        Assert.True(new FileInfo(job.Outputs.Single()).Length <= limitKb * 1024L, $"{new FileInfo(job.Outputs.Single()).Length} > {limitKb} KB");
        Assert.Null(job.Files.Single().NoticeKey);
    }

    [Fact]
    public async Task Unlock_with_the_open_password_of_a_pdf_whose_owner_password_differs()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 2)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var statement = Path.Combine(dir, "statement.pdf");
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            document.SecuritySettings.UserPassword = "1234";
            document.SecuritySettings.OwnerPassword = "bank-only";
            document.Save(statement);
        }

        var job = await fx.ConvertAsync([statement], new Preset { TargetFormat = "pdf", Pdf = { Unlock = true, Password = "1234" } });

        AssertDone(job);
        using var open = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(2, open.PageCount);
    }
}
