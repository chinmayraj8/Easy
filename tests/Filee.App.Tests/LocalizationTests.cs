using System.Text.Json;
using System.Text.RegularExpressions;

namespace Filee.App.Tests;

/// <summary>Easy ships one language, English: checks its file against the XAML and the rebrand.</summary>
public partial class LocalizationTests
{
    private static string I18nDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "Filee.App", "Assets", "i18n");
        }
    }

    private static Dictionary<string, string> Load(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(I18nDirectory, code + ".json")))!;

    [Fact]
    public void English_is_the_only_language()
    {
        Assert.Equal(["en.json"], Directory.GetFiles(I18nDirectory).Select(Path.GetFileName));
    }

    /// <summary>Easy is a rebrand: only the attribution line may still name Filee.</summary>
    [Fact]
    public void No_string_shows_the_original_name_except_the_attribution()
    {
        var named = Load("en").Where(e => e.Value.Contains("Filee", StringComparison.Ordinal)).Select(e => e.Key);
        Assert.Equal(["about.based_on"], named);
    }

    [Fact]
    public void Every_key_used_in_xaml_exists()
    {
        var english = Load("en");
        var src = Path.GetFullPath(Path.Combine(I18nDirectory, "..", ".."));
        var missing = Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => DynamicResourceKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => k.Contains('.') && !k.StartsWith("Icon.", StringComparison.Ordinal))
            .Where(k => !english.ContainsKey(k))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    [GeneratedRegex(@"\{DynamicResource ([\w.]+)\}")]
    private static partial Regex DynamicResourceKey();
}
