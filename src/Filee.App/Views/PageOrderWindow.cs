// "Reorder pages": the PDF's pages as thumbnails; dragging one moves it, the others make room. OK closes it with the new
// order as a page list ("3,1,2"), the form PdfOptions.PageOrder reads. Thumbnails render in the background, page by page.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.Core.Localization;
using PDFtoImage;
using SkiaSharp;

namespace Filee.App.Views;

public sealed class PageOrderWindow : Window
{
    private const double TileWidth = 128;
    private const double ThumbHeight = 160;

    private readonly WrapPanel _pages = new();
    private readonly ScrollViewer _scroll;
    private readonly CancellationTokenSource _rendering = new();
    private Control? _dragged;
    private string? _result;

    internal PageOrderWindow(ILocalizer loc, byte[] pdf, int pageCount)
    {
        Title = loc["preset.reorder_pdf"];
        Width = 760;
        Height = 620;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Motion.Track(this);

        for (var i = 0; i < pageCount; i++)
            _pages.Children.Add(Tile(i));
        _pages.PointerPressed += OnPressed;
        _pages.PointerMoved += OnMoved;
        _pages.PointerReleased += OnReleased;
        _pages.PointerCaptureLost += (_, _) => EndDrag();
        _scroll = new ScrollViewer { Content = _pages, Padding = new Thickness(10) };

        var reverse = new Button { Content = loc["reorder.reverse"] };
        reverse.Click += (_, _) =>
        {
            var tiles = _pages.Children.Reverse().ToList();
            _pages.Children.Clear();
            _pages.Children.AddRange(tiles);
        };
        var ok = new Button { Content = loc["common.ok"], IsDefault = true };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Finish(Order);
        var cancel = new Button { Content = loc["common.cancel"], IsCancel = true };
        cancel.Click += (_, _) => Finish(null);

        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
        DockPanel.SetDock(reverse, Dock.Left);
        bar.Children.Add(reverse);
        bar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, ok },
        });
        var hint = new TextBlock { Text = loc["reorder.drag_hint"], Margin = new Thickness(16, 14, 16, 0), TextWrapping = TextWrapping.Wrap };
        hint.Classes.Add("secondary");

        var root = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(bar);
        root.Children.Add(_scroll);
        Content = root;

        Closed += (_, _) =>
        {
            _rendering.Cancel();
            foreach (var tile in _pages.Children)
                (ImageOf((int)tile.Tag!).Source as IDisposable)?.Dispose();
        };
        _ = Task.Run(() => Render(pdf, _rendering.Token));
    }

    /// <summary>
    /// The new page order ("3,1,2"), or null when cancelled. A PDF it can't read (locked, damaged) gives "": the
    /// conversion then runs and says what is wrong with the file.
    /// </summary>
    public static async Task<string?> PickAsync(ILocalizer loc, string pdfPath)
    {
        byte[] pdf;
        int pageCount;
        try
        {
            pdf = await File.ReadAllBytesAsync(pdfPath);
#pragma warning disable CA1416 // PDFium ships for every desktop platform Easy runs on
            pageCount = Conversion.GetPageCount(pdf, null);
#pragma warning restore CA1416
        }
        catch (Exception)
        {
            return "";
        }
        var window = new PageOrderWindow(loc, pdf, pageCount) { Topmost = true };
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Activate();
        await closed.Task;
        return window._result;
    }

    /// <summary>The pages as they are now, 1-based: "3,1,2".</summary>
    internal string Order => string.Join(",", _pages.Children.Select(c => (int)c.Tag! + 1));

    private void Finish(string? order)
    {
        _result = order;
        Close();
    }

    private Border Tile(int page) => new()
    {
        Tag = page,
        Width = TileWidth,
        Margin = new Thickness(6),
        Padding = new Thickness(6),
        CornerRadius = new CornerRadius(8),
        Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.Hand),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Border
                {
                    Height = ThumbHeight,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Application.Current?.FindResource("SubtleBorderBrush") as IBrush,
                    Child = new Image { Stretch = Stretch.Uniform },
                },
                new TextBlock { Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center },
            },
        },
    };

    private void Render(byte[] pdf, CancellationToken ct)
    {
        var options = new PDFtoImage.RenderOptions(Dpi: 36, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
        var page = 0;
        try
        {
#pragma warning disable CA1416
            foreach (var bitmap in Conversion.ToImages(pdf, password: null, options))
#pragma warning restore CA1416
            {
                if (ct.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }
                using (bitmap)
                using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                {
                    var thumb = new Bitmap(data.AsStream());
                    var index = page++;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested)
                            thumb.Dispose();
                        else
                            ImageOf(index).Source = thumb;
                    });
                }
            }
        }
        catch (Exception)
        {
            // A page PDFium can't draw stays blank; its number still shows where it goes.
        }
    }

    private Image ImageOf(int page) =>
        (Image)((Border)((StackPanel)((Border)_pages.Children.First(c => (int)c.Tag! == page)).Child!).Children[0]).Child!;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_pages).Properties.IsLeftButtonPressed || TileAt(e.GetPosition(_pages)) is not { } tile)
            return;
        _dragged = tile;
        tile.Opacity = 0.5;
        e.Pointer.Capture(_pages);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null)
            return;
        var at = e.GetPosition(_pages);
        if (TileAt(at) is { } target && target != _dragged)
            _pages.Children.Move(_pages.Children.IndexOf(_dragged), _pages.Children.IndexOf(target));

        // Near the top or bottom edge the list scrolls, so a page can go anywhere in a long PDF.
        var y = e.GetPosition(_scroll).Y;
        var step = y < 40 ? -20 : y > _scroll.Bounds.Height - 40 ? 20 : 0;
        if (step != 0)
            _scroll.Offset = _scroll.Offset.WithY(Math.Clamp(_scroll.Offset.Y + step, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragged is not null)
            _dragged.Opacity = 1;
        _dragged = null;
    }

    private Control? TileAt(Point p) => _pages.Children.FirstOrDefault(c => c.Bounds.Contains(p));
}
