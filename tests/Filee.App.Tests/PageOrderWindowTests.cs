// Edit pages: dragging a page thumbnail moves it, ↻ turns it, ✕ leaves it out, Reverse turns the order around.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.Views;
using Filee.Core.Localization;

namespace Filee.App.Tests;

public sealed class PageOrderWindowTests
{
    private static PageOrderWindow Open()
    {
        TestServices.EnsureInitialized("en");
        // Not a PDF: the thumbnails stay blank, the pages are still there to move.
        var window = new PageOrderWindow(AppHost.Get<ILocalizer>(), [], 3);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Control TileAt(Window window, int position) =>
        window.GetVisualDescendants().OfType<WrapPanel>().Single().Children[position];

    private static Point CenterOf(Window window, int position)
    {
        var tile = TileAt(window, position);
        return tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 3), window)!.Value;
    }

    /// <summary>The tile's button showing <paramref name="glyph"/> (↻ or ✕).</summary>
    private static Button ButtonOf(Window window, int position, string glyph) =>
        TileAt(window, position).GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == glyph);

    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Dragging_the_first_page_onto_the_last_moves_it_to_the_end()
    {
        var window = Open();
        window.MouseMove(CenterOf(window, 0));
        window.MouseDown(CenterOf(window, 0), MouseButton.Left);
        window.MouseMove(CenterOf(window, 2));
        window.MouseUp(CenterOf(window, 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("2,3,1", window.Order);
        window.Close();
    }

    [AvaloniaFact]
    public void Reverse_turns_the_order_around()
    {
        var window = Open();
        Press(window.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Reverse order"));

        Assert.Equal("3,2,1", window.Order);
        window.Close();
    }

    [AvaloniaFact]
    public void Turning_goes_round_a_quarter_at_a_time()
    {
        var window = Open();
        Press(ButtonOf(window, 1, "↻"));
        Assert.Equal("1,2@90,3", window.Order);
        Press(ButtonOf(window, 1, "↻"));
        Press(ButtonOf(window, 1, "↻"));
        Assert.Equal("1,2@270,3", window.Order);
        Press(ButtonOf(window, 1, "↻"));
        Assert.Equal("1,2,3", window.Order); // all the way round
        window.Close();
    }

    [AvaloniaFact]
    public void A_left_out_page_comes_back_when_pressed_again_and_one_page_always_stays()
    {
        var window = Open();
        var ok = window.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "OK");
        Press(ButtonOf(window, 0, "✕"));
        Assert.Equal("2,3", window.Order);
        Press(ButtonOf(window, 0, "✕"));
        Assert.Equal("1,2,3", window.Order);

        foreach (var position in new[] { 0, 1, 2 })
            Press(ButtonOf(window, position, "✕"));
        Assert.False(ok.IsEnabled);
        Press(ButtonOf(window, 2, "✕"));
        Assert.True(ok.IsEnabled);
        Assert.Equal("3", window.Order);
        window.Close();
    }

    [AvaloniaFact]
    public void Pressing_a_tile_button_does_not_start_a_drag()
    {
        var window = Open();
        var turn = ButtonOf(window, 0, "↻");
        var at = turn.TranslatePoint(new Point(turn.Bounds.Width / 2, turn.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(CenterOf(window, 2));
        window.MouseUp(CenterOf(window, 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("1", window.Order); // page 1 stayed first
        window.Close();
    }
}
