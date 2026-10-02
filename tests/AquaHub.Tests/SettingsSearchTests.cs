using AquaHub.Core.Settings;

namespace AquaHub.Tests;

/// <summary>The Settings search box, and the shortcut boxes' rules on which keys they take.</summary>
public class SettingsSearchTests
{
    private static readonly SettingsSearch.Entry[] Rows =
    {
        new("General", "Theme", "", "dark mode light mode appearance"),
        new("General", "Eco mode", "Refresh less often while you're away or on battery.", "battery power saving"),
        new("Shortcuts", "Quick panel hotkey", "Opens the quick panel from any app.", "shortcut keyboard flyout widget"),
        new("Notifications", "Quiet hours", "No pop-ups between these times."),
        new("Privacy & data", "Keep Ask history"),
        new("About", "Updates", "Aqua checks for a new version about once a day."),
    };

    private static List<string> Find(string query, int max = 8) => SettingsSearch.Find(query, Rows, r => r, max).Select(r => r.Title).ToList();

    [Fact]
    public void TheWordsPeopleUseFindTheSetting()
    {
        Assert.Equal("Theme", Find("dark mode")[0]);
        Assert.Equal("Quick panel hotkey", Find("shortcut")[0]);
        Assert.Equal("Keep Ask history", Find("privacy")[0]);
    }

    [Fact]
    public void PartWordsAndPluralsMatch()
    {
        Assert.Equal(new[] { "Updates" }, Find("upd"));
        Assert.Equal(new[] { "Updates" }, Find("update"));
        Assert.Equal(new[] { "Quiet hours" }, Find("HOUR"));
        Assert.Equal(new[] { "Quick panel hotkey" }, Find("hotkeys"));
    }

    [Fact]
    public void EveryWordHasToMatch()
    {
        Assert.Empty(Find("dark battery"));
        Assert.Empty(Find("nothing like this"));
    }

    [Fact]
    public void ATitleMatchComesFirst()
    {
        // "mode" is in Eco mode's title and only in Theme's extra words.
        Assert.Equal(new[] { "Eco mode", "Theme" }, Find("mode"));
        // A title that starts with what was typed beats one that has it further in.
        var voice = new SettingsSearch.Entry[] { new("Ask Aqua", "Test voice input"), new("Ask Aqua", "Voice input"), new("Ask Aqua", "Voice training") };
        Assert.Equal(new[] { "Voice input", "Voice training", "Test voice input" }, SettingsSearch.Find("voice", voice, r => r).Select(r => r.Title));
    }

    [Fact]
    public void NothingTypedFindsNothing()
    {
        Assert.Empty(Find(""));
        Assert.Empty(Find("  — "));
    }

    [Fact]
    public void ResultsAreCappedInPageOrder()
    {
        var all = Enumerable.Range(1, 20).Select(i => new SettingsSearch.Entry("News", $"Source {i}")).ToList();
        var hits = SettingsSearch.Find("source", all, r => r, max: 5);
        Assert.Equal(new[] { "Source 1", "Source 2", "Source 3", "Source 4", "Source 5" }, hits.Select(h => h.Title));
    }

    [Theory]
    [InlineData("ctrl+alt+h", "Ctrl+Alt+H")]
    [InlineData("Alt+Ctrl+Space", "Ctrl+Alt+Space")]
    [InlineData("Win+Shift+f12", "Shift+Win+F12")]
    [InlineData(" Ctrl + Alt + 7 ", "Ctrl+Alt+7")]
    [InlineData("control+windows+enter", "Ctrl+Win+Enter")]
    public void KeysAreWrittenOneWay(string typed, string expected)
    {
        Assert.True(Shortcuts.TryNormalize(typed, out var keys, out _));
        Assert.Equal(expected, keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("H")]
    [InlineData("Shift+H")]      // Shift alone types a capital
    [InlineData("Ctrl+Alt")]     // no key
    [InlineData("Ctrl+H+J")]     // two keys
    [InlineData("Ctrl+Space+Enter")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Tab")]
    public void NotAShortcut(string typed) => Assert.False(Shortcuts.TryNormalize(typed, out _, out _));

    [Fact]
    public void AquasOtherShortcutCantBeTakenTwice()
    {
        var palette = new Dictionary<string, string> { ["the command palette"] = "Ctrl+Alt+Space" };
        Assert.Equal("Ctrl+Alt+Space already opens the command palette.", Shortcuts.Clash("alt+ctrl+space", palette));
        Assert.Null(Shortcuts.Clash("Ctrl+Alt+H", palette));
        // A cleared shortcut holds nothing.
        Assert.Null(Shortcuts.Clash("Ctrl+Alt+Space", new Dictionary<string, string> { ["the command palette"] = "" }));
    }

    [Fact]
    public void KeysAquasWindowAndOtherAppsUseAreRefused()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("Ctrl+K opens the command palette inside Aqua.", Shortcuts.Clash("Ctrl+K", none));
        Assert.Equal("Ctrl+4 opens Markets inside Aqua.", Shortcuts.Clash("ctrl+4", none));
        Assert.Equal("Most apps use Ctrl+C themselves. Try adding Shift.", Shortcuts.Clash("Ctrl+C", none));
        Assert.Equal("Most apps use Alt+F4 themselves. Try adding Shift.", Shortcuts.Clash("Alt+F4", none));
        Assert.Null(Shortcuts.Clash("Ctrl+Shift+C", none));
        Assert.Null(Shortcuts.Clash("Win+Alt+K", none));
    }

    [Fact]
    public void TheDefaultsAndTheirFallbacksAreAllowed()
    {
        // App.HotkeyFallbacks: what Aqua tries when another app already has a default.
        foreach (var flyout in new[] { "Ctrl+Alt+H", "Ctrl+Alt+Q", "Win+Alt+H" })
            Assert.Null(Shortcuts.Clash(flyout, new Dictionary<string, string> { ["the command palette"] = "Ctrl+Alt+Space" }));
        foreach (var palette in new[] { "Ctrl+Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J", "Win+Alt+K" })
            Assert.Null(Shortcuts.Clash(palette, new Dictionary<string, string> { ["the quick panel"] = "Ctrl+Alt+H" }));
    }

    [Fact]
    public void TheInAppListShowsEachShortcutOnce()
    {
        var keys = Shortcuts.InApp.Select(s => s.Keys + "|" + s.Where).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains(Shortcuts.InApp, s => s.Keys == "Ctrl+K");
        Assert.Contains(Shortcuts.InApp, s => s.Keys == "Ctrl+,");
        Assert.All(Shortcuts.InApp, s => Assert.False(string.IsNullOrWhiteSpace(s.Does)));
    }
}
