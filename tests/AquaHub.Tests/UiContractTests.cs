using System.Xml.Linq;
using AquaHub.Core.Settings;
using AquaHub.Core.Updates;

namespace AquaHub.Tests;

/// <summary>
/// Static checks on the app's XAML (no WPF needed). UI Automation, and so screen readers and the E2E suite, only see
/// elements that have an automation peer: a name or id on a Border, Grid or StackPanel silently goes nowhere. And the
/// Settings rows that alerts and buttons link to must exist.
/// </summary>
public class UiContractTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Panels and shapes WPF gives no automation peer (c:GroupBorder and c:GroupGrid are the ones with a peer).
    private static readonly HashSet<string> Peerless = new()
    {
        "Border", "Grid", "StackPanel", "DockPanel", "WrapPanel", "Canvas", "UniformGrid", "Viewbox", "Rectangle", "Ellipse", "Path", "Line",
    };

    private static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "AquaHub.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("AquaHub.sln not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string File, XDocument Doc)> Xaml()
    {
        var root = Repo();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "AquaHub"), "*.xaml", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")) continue;
            yield return (rel, XDocument.Load(file, LoadOptions.SetLineInfo));
        }
    }

    [Fact]
    public void TheAppHasXamlToCheck() => Assert.True(Xaml().Count() >= 10);

    [Fact]
    public void NoAccessibleNameOrIdOnAnElementWithoutAPeer()
    {
        var hits = new List<string>();
        foreach (var (file, doc) in Xaml())
            foreach (var el in doc.Descendants().Where(e => e.Name.Namespace == Wpf && Peerless.Contains(e.Name.LocalName)))
                foreach (var a in el.Attributes().Where(a => a.Name.LocalName is "AutomationProperties.AutomationId" or "AutomationProperties.Name"))
                    hits.Add($"{file}:{((System.Xml.IXmlLineInfo)el).LineNumber} <{el.Name.LocalName} {a.Name.LocalName}=\"{a.Value}\">");
        Assert.True(hits.Count == 0,
            "These names/ids never reach UI Automation (screen readers, the E2E suite). Use c:GroupBorder or c:GroupGrid, or put them on a control:\n" +
            string.Join("\n", hits));
    }

    [Theory]
    [InlineData(UpdatePolicy.Target)]            // the update alerts
    [InlineData("settings:location")]            // Today's "Choose a place"
    [InlineData("settings:markets:watchlist")]   // Markets' watchlist "Edit"
    [InlineData("settings:news:sources")]        // a news feed that isn't at its address any more
    [InlineData("settings:social:extrafeeds")]   // …one of your own feeds
    [InlineData("settings:social:youtube")]      // …a YouTube channel's
    public void LinkedSettingsRowsExist(string target)
    {
        // "settings:<section>[:<row>]" opens the section's panel (P_<section>) and scrolls to the row (Row_<row>).
        var parts = target.Split(':');
        Assert.Equal("settings", parts[0]);
        var names = Xaml().Where(x => x.File.EndsWith("SettingsPage.xaml", StringComparison.Ordinal))
            .SelectMany(x => x.Doc.Descendants()).Select(e => (string?)e.Attribute(X + "Name")).OfType<string>().ToHashSet();
        Assert.Contains("P_" + parts[1], names);
        if (parts.Length > 2) Assert.Contains("Row_" + parts[2], names);
    }

    [Fact]
    public void TheShortcutsListedForTheWholeWindowAreTheOnesItHandles()
    {
        // The Shortcuts page lists Aqua's own keys by hand; MainWindow.OnKey is where "anywhere in Aqua" ones are handled.
        var window = File.ReadAllText(Path.Combine(Repo(), "src", "AquaHub", "UI", "Shell", "MainWindow.xaml.cs"));
        var start = window.IndexOf("void OnKey(", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow.OnKey not found");
        var handled = System.Text.RegularExpressions.Regex.Matches(window[start..Math.Min(window.Length, start + 4000)], @"Key\.(\w+)")
            .Select(m => m.Groups[1].Value).ToHashSet();
        static string KeyOf(string caps) => caps.Split('+')[^1] switch
        {
            "Esc" => "Escape", "," => "OemComma", var k when k.StartsWith("1…", StringComparison.Ordinal) => "D1", var k => k,
        };
        var listed = Shortcuts.InApp.Where(e => e.Where == "Anywhere in Aqua").Select(e => KeyOf(e.Keys)).ToList();
        Assert.All(listed, k => Assert.Contains(k, handled));
        // …and nothing handled there goes unlisted (Ctrl+9 is the end of the Ctrl+1…9 range).
        Assert.All(handled.Where(k => k != "D9"), k => Assert.Contains(k, listed));
    }
}
