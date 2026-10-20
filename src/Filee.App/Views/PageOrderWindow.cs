// "Edit pages": the PDF's pages as thumbnails. Dragging one moves it (the others make room), ↻ turns it a quarter clockwise,
// ✕ leaves it out (pressed again, it stays). OK closes it with the pages to keep as a page list ("3@90,1,2"), the form
// PdfOptions.PageOrder reads with DropUnlisted. Thumbnails render in the background, page by page.

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
    private readonly Button _ok;
    private readonly CancellationTokenSource _rendering = new();
    private Control? _dragged;
    private string? _result;

    /// <summary>A page's tile: which page it is, how far it is turned, whether it is left out.</summary>
    private sealed class PageTile(int page)
    {
        public int Page { get; } = page;
        public int Rotation { get; set; }
        public bool Removed { get; set; }
        public required Image Image { get; init; }
    }

    internal PageOrderWindow(ILocalizer loc, byte[] pdf, int pageCount)
    {
        Title = loc["preset.reorder_pdf"];
        Width = 760;
        Height = 620;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Motion.Track(this);

        _ok = new Button { Content = loc["common.ok"], IsDefault = true };
        for (var i = 0; i < pageCount; i++)
            _pages.Children.Add(Tile(i, loc));
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
        _ok.Classes.Add("accent");
        _ok.Click += (_, _) => Finish(Order);
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
            Children = { cancel, _ok },
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
                (StateOf(tile).Image.Source as IDisposable)?.Dispose();
        };
        _ = Task.Run(() => Render(pdf, _rendering.Token));
    }

    /// <summary>
    /// The pages to keep in their new order ("3@90,1,2"), or null when cancelled. A PDF it can't read (locked, damaged)
    /// gives "": the conversion then runs and says what is wrong with the file.
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

    /// <summary>The pages kept, as they are now, 1-based and turned: "3@90,1,2".</summary>
    internal string Order => string.Join(",", _pages.Children.Select(StateOf).Where(t => !t.Removed)
        .Select(t => t.Rotation == 0 ? $"{t.Page + 1}" : $"{t.Page + 1}@{t.Rotation}"));

    private void Finish(string? order)
    {
        _result = order;
        Close();
    }

    private static PageTile StateOf(Control tile) => (PageTile)tile.Tag!;

    private Border Tile(int page, ILocalizer loc)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        var turn = new LayoutTransformControl { Child = image };
        var state = new PageTile(page) { Image = image };
        var rotate = new Button { Content = "↻", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(rotate, loc["reorder.rotate"]);
        var remove = new Button { Content = "✕", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(remove, loc["reorder.remove"]);
        var tile = new Border
        {
            Tag = state,
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
                        Child = turn,
                    },
                    new DockPanel
                    {
                        Children =
                        {
                            Docked(rotate, Dock.Left),
                            Docked(remove, Dock.Right),
                            new TextBlock { Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                },
            },
        };
        rotate.Click += (_, _) =>
        {
            state.Rotation = (state.Rotation + 90) % 360;
            turn.LayoutTransform = state.Rotation == 0 ? null : new RotateTransform(state.Rotation);
        };
        remove.Click += (_, _) =>
        {
            state.Removed = !state.Removed;
            turn.Opacity = state.Removed ? 0.25 : 1;
            // At least one page stays: a PDF without pages can't be saved.
            _ok.IsEnabled = _pages.Children.Select(StateOf).Any(t => !t.Removed);
        };
        return tile;

        static Control Docked(Control control, Dock dock)
        {
            DockPanel.SetDock(control, dock);
            return control;
        }
    }

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
                            StateOf(_pages.Children.First(c => StateOf(c).Page == index)).Image.Source = thumb;
                    });
                }
            }
        }
        catch (Exception)
        {
            // A page PDFium can't draw stays blank; its number still shows where it goes.
        }
    }

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
