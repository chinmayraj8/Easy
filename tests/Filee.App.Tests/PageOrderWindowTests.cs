// Reorder pages: dragging a page thumbnail moves it, Reverse turns the order around.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    private static Point CenterOf(Window window, int position)
    {
        var tile = window.GetVisualDescendants().OfType<WrapPanel>().Single().Children[position];
        return tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), window)!.Value;
    }

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
        var reverse = window.GetVisualDescendants().OfType<Button>().First();
        reverse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("3,2,1", window.Order);
        window.Close();
    }
}
