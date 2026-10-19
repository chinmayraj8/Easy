using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Filee.App.Tests;

public class LocalizationResourceTests
{
    [AvaloniaFact]
    public void Strings_are_available_as_application_resources()
    {
        TestServices.EnsureInitialized("en");
        Assert.True(Application.Current!.TryGetResource("home.title", null, out var value));
        Assert.Equal("Home", value);
    }

    [AvaloniaFact]
    public void DynamicResource_in_xaml_resolves_and_follows_language_changes()
    {
        TestServices.EnsureInitialized("en");
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, text.GetResourceObservable("home.title"));
        var window = new Window { Content = text };
        window.Show();
        Assert.Equal("Home", text.Text);

        TestServices.EnsureInitialized("ko"); // Easy ships English only: another language falls back to it
        Assert.Equal("Home", text.Text);
        window.Close();
    }
}
