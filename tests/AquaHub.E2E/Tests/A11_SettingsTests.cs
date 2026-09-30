using System.Globalization;
using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>
/// Settings: every section, every switch / combo / text box / chip editor, news sources, calendars, holdings,
/// scenes editor, data actions — each change verified in settings.json (or the journal).
/// </summary>
public sealed class A11_SettingsTests : E2ETestBase
{
    public A11_SettingsTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private static readonly string[] SectionNames =
    {
        "General", "Location & weather", "News", "Social", "Markets", "Agenda", "Predictions", "AI & models", "Apps & scenes", "Notifications", "Privacy & data", "About",
    };

    // ───────────── helpers ─────────────
    private AutomationElement Page => PageRoot("settings");

    private AutomationElement Section(string name)
    {
        var page = GoTo("settings");
        var list = Ui.WaitFind(page, Ui.Id("Sections"), "sections list");
        var item = Wait.For(() => Ui.FindAll(list, Ui.Type(ControlType.ListItem)).FirstOrDefault(i => Ui.Texts(i).Contains(name)), $"section '{name}'");
        if (!Ui.IsSelected(item)) Ui.Select(item);
        Wait.For(() => Ui.IsSelected(item), $"section '{name}' selected");
        Thread.Sleep(250);
        return PageRoot("settings");
    }

    private AutomationElement Field(string name, ControlType type) =>
        Ui.WaitFind(Page, Ui.And(Ui.Type(type), Ui.Name(name)), $"{type.ProgrammaticName.Replace("ControlType.", "")} '{name}'");

    /// <summary>Flips a switch, checks settings.json, flips it back and checks again.</summary>
    private void Flip(string area, string switchName, string path)
    {
        Check(area, $"switch '{switchName}' ↔ {path}", () =>
        {
            var before = App.Settings.GetBool(path) ?? throw new InvalidOperationException($"{path} missing");
            var cb = Field(switchName, ControlType.CheckBox);
            Expect((Ui.ToggleStateOf(cb) == ToggleState.On) == before, $"switch shows {Ui.ToggleStateOf(cb)} but settings has {before}");
            Ui.Toggle(cb);
            App.Settings.WaitForBool(path, !before);
            Ui.Toggle(Field(switchName, ControlType.CheckBox));
            App.Settings.WaitForBool(path, before);
        });
    }

    /// <summary>Sets a text box bound with UpdateSourceTrigger=PropertyChanged and checks the persisted value.</summary>
    private void Number(string area, string fieldName, string value, string path, double expected)
    {
        Check(area, $"'{fieldName}' = {value} → {path}", () =>
        {
            Ui.SetValue(Field(fieldName, ControlType.Edit), value);
            App.Settings.WaitFor(path, n => n is JsonValue v && v.TryGetValue<double>(out var d) && Math.Abs(d - expected) < 1e-6, $"== {expected}");
        });
    }

    /// <summary>
    /// Edits a text box the way a user does (focus, type, move focus away) and checks it is saved without leaving
    /// the page; then leaves the page and checks again (so a missing save is reported but the value is not lost).
    /// </summary>
    private void TypeAndBlur(string area, string section, string fieldName, string value, string path, string? expected = null)
    {
        expected ??= value;
        Check(area, $"'{fieldName}' = {value} saved on focus change → {path}", () =>
        {
            Section(section);
            App.EnsureForeground(Main);
            var box = Field(fieldName, ControlType.Edit);
            Wait.Retry(box.SetFocus, "focus field");
            Ui.SetValue(box, value);
            // A person types, pauses, then clicks elsewhere: the auto-save (600 ms after the first change) fires while
            // the caret is still in the box, before the binding has pushed the new text.
            Thread.Sleep(1500);
            Wait.Retry(() => Ui.WaitFind(Page, Ui.Id("Sections"), "sections").SetFocus(), "move focus away");
            var saved = Wait.Until(() => App.Settings.GetString(path) == expected, TimeSpan.FromSeconds(3));
            if (!saved)
            {
                GoTo("today");
                var afterLeave = App.Settings.GetString(path);
                throw new Xunit.Sdk.XunitException(
                    $"'{fieldName}' was not saved after editing it and moving focus (settings.json still '{SettingsFile.Value(App.Settings.Get(path))}' 3 s later)." +
                    (afterLeave == expected ? " It is only written when leaving the Settings page." : $" Even after leaving the page it is '{afterLeave}'."));
            }
        });
    }

    private void AddChip(string area, string editorId, string input, string expectedChip, string path)
    {
        Check(area, $"chip editor #{editorId}: add '{input}' → {path}", () =>
        {
            var editor = Ui.WaitFind(Page, Ui.Id(editorId), $"chip editor {editorId}");
            var box = Ui.WaitFind(editor, Ui.Type(ControlType.Edit), "chip input");
            Ui.SetValue(box, input);
            FocusAndPress(Main, box, VK.Enter);
            Ui.WaitFind(Ui.WaitFind(Page, Ui.Id(editorId), editorId), Ui.Button("Remove " + expectedChip), $"chip '{expectedChip}'");
            Wait.For(() => App.Settings.GetStrings(path).Contains(expectedChip) || App.Settings.Symbols(path).Contains(expectedChip), $"{expectedChip} in {path}", E2EConfig.PersistTimeout);
        });
    }

    private void RejectChip(string area, string editorId, string input, string path)
    {
        Check(area, $"chip editor #{editorId}: invalid '{input}' rejected", () =>
        {
            var before = App.Settings.GetStrings(path).Count;
            var editor = Ui.WaitFind(Page, Ui.Id(editorId), $"chip editor {editorId}");
            var box = Ui.WaitFind(editor, Ui.Type(ControlType.Edit), "chip input");
            Ui.SetValue(box, input);
            FocusAndPress(Main, box, VK.Enter);
            Thread.Sleep(1200);
            Expect(App.Settings.GetStrings(path).Count == before, $"invalid value was added: {string.Join(",", App.Settings.GetStrings(path))}");
            Expect(Ui.ValueOf(Ui.WaitFind(Ui.WaitFind(Page, Ui.Id(editorId), editorId), Ui.Type(ControlType.Edit), "input")) == "", "input not cleared");
        });
    }

    private void RemoveChip(string area, string editorId, string chip, string path)
    {
        Check(area, $"chip editor #{editorId}: remove '{chip}' → {path}", () =>
        {
            Ui.Invoke(Ui.WaitFind(Ui.WaitFind(Page, Ui.Id(editorId), editorId), Ui.Button("Remove " + chip), "remove chip"));
            Wait.For(() => !App.Settings.GetStrings(path).Contains(chip) && !App.Settings.Symbols(path).Contains(chip), $"{chip} removed from {path}", E2EConfig.PersistTimeout);
        });
    }

    private void Combo(string area, AutomationElement combo, string item, string path, string expected)
    {
        Check(area, $"combo '{Ui.NameOf(combo)}' → '{item}' → {path}={expected}", () =>
        {
            Ui.SelectComboItem(combo, item, Pid);
            App.Settings.WaitForString(path, expected);
        });
    }

    private JsonObject? WatchRow(string symbol) =>
        (App.Settings.Get("markets.watchlist") as JsonArray)?.OfType<JsonObject>().FirstOrDefault(w => (string?)w["symbol"] == symbol);

    // ───────────── tests ─────────────
    [Fact]
    public void T01_EverySectionOpens() => Run(() =>
    {
        foreach (var name in SectionNames)
            Check("Settings sections", $"section '{name}'", () =>
            {
                var page = Section(name);
                var heading = name == "AI & models" ? "AI & models" : name;
                Wait.For(() => Ui.HasText(page, heading), $"'{heading}' heading visible");
            });
        Check("Settings sections", "section list items have readable accessible names", () =>
        {
            var items = Ui.FindAll(Ui.WaitFind(Page, Ui.Id("Sections"), "sections"), Ui.Type(ControlType.ListItem)).Select(Ui.NameOf).ToList();
            var bad = items.Where(n => n.Contains('{') || n.StartsWith("AquaHub.", StringComparison.Ordinal)).ToList();
            Expect(bad.Count == 0, $"list items are announced as object dumps, e.g. '{bad.FirstOrDefault()}'");
        });
    });

    [Fact]
    public void T02_General() => Run(() =>
    {
        Section("General");
        Check("Settings general", "'Your name' → general.userName", () =>
        {
            Ui.SetValue(Field("Your name", ControlType.Edit), "E2E Tester");
            App.Settings.WaitForString("general.userName", "E2E Tester");
            Ui.SetValue(Field("Your name", ControlType.Edit), "Sam");
            App.Settings.WaitForString("general.userName", "Sam");
        });
        Check("Settings general", "'Start with Windows' → launchAtStartup + journal autostart", () =>
        {
            var before = App.Settings.GetBool("general.launchAtStartup") ?? false;
            ExpectJournal("autostart", () => Ui.Toggle(Field("Start with Windows", ControlType.CheckBox)), d => d == (!before).ToString());
            App.Settings.WaitForBool("general.launchAtStartup", !before);
            ExpectJournal("autostart", () => Ui.Toggle(Field("Start with Windows", ControlType.CheckBox)), d => d == before.ToString());
            App.Settings.WaitForBool("general.launchAtStartup", before);
        });
        Flip("Settings general", "Closing the window keeps Aqua Hub running", "general.closeToTray");
        Flip("Settings general", "24-hour clock", "general.use24Hour");
        Flip("Settings general", "Eco mode", "general.ecoMode");
        Flip("Settings general", "Reduce motion", "general.reduceMotion");

        var hwnd = App.HwndOf(Main);
        double Lum() { Thread.Sleep(900); return Screenshots.MeanLuminance(hwnd); }
        Combo("Settings general", Field("Theme", ControlType.ComboBox), "Light", "general.theme", "light");
        var light = Lum();
        Combo("Settings general", Field("Theme", ControlType.ComboBox), "Dark", "general.theme", "dark");
        var dark = Lum();
        Check("Settings general", "theme change is applied to the window (light brighter than dark)", () =>
        {
            Step($"   mean luminance light={light:0} dark={dark:0}");
            Expect(light > dark + 40, $"light theme luminance {light:0} is not clearly brighter than dark {dark:0}");
        });
        Combo("Settings general", Field("Theme", ControlType.ComboBox), "Use Windows setting", "general.theme", "system");

        Combo("Settings general", Field("Accent colour", ControlType.ComboBox), "Windows accent", "general.accent", "system");
        Check("Settings general", "accent 'Custom…' + #FF8800 → general.accent", () =>
        {
            App.EnsureForeground(Main);
            Ui.SelectComboItem(Field("Accent colour", ControlType.ComboBox), "Custom…", Pid);
            var hex = Ui.WaitFind(Page, Ui.Id("AccentHex"), "custom hex box");
            Wait.Retry(hex.SetFocus, "focus hex");
            Ui.SetValue(hex, "#FF8800");
            Wait.Retry(() => Field("Your name", ControlType.Edit).SetFocus(), "blur hex");
            App.Settings.WaitForString("general.accent", "#FF8800");
        });
        Check("Settings general", "invalid custom accent is ignored", () =>
        {
            var hex = Ui.WaitFind(Page, Ui.Id("AccentHex"), "custom hex box");
            Wait.Retry(hex.SetFocus, "focus hex");
            Ui.SetValue(hex, "#12");
            Wait.Retry(() => Field("Your name", ControlType.Edit).SetFocus(), "blur hex");
            Thread.Sleep(1500);
            Expect(App.Settings.GetString("general.accent") == "#FF8800", $"accent became {App.Settings.GetString("general.accent")}");
        });
        Combo("Settings general", Field("Accent colour", ControlType.ComboBox), "Aqua", "general.accent", "aqua");

        TypeAndBlur("Settings general", "General", "Quick panel hotkey", "Ctrl+Alt+Shift+F9", "general.hotkeyFlyout");
        TypeAndBlur("Settings general", "General", "Command palette hotkey", "Ctrl+Alt+Shift+F10", "general.hotkeyPalette");
    });

    [Fact]
    public void T03_LocationAndWeather() => Run(() =>
    {
        Section("Location & weather");
        AutomationElement? Result(string city) =>
            Ui.FindAll(Ui.Find(Main, Ui.Id("CityResults")) ?? Main, Ui.Type(ControlType.ListItem)).FirstOrDefault(li => Ui.NameOf(li).StartsWith(city, StringComparison.Ordinal) || Ui.Texts(li).Any(t => t.StartsWith(city, StringComparison.Ordinal)));
        Check("Settings location", "city search 'Galway' lists places", () =>
        {
            App.EnsureForeground(Main);
            Ui.SetValue(Field("Search a city…", ControlType.Edit), "Galway");
            Wait.For(() => Result("Galway"), "Galway result", TimeSpan.FromSeconds(20));
        });
        Check("Settings location", "keyboard/UIA pick of a city (select + Enter)", () =>
        {
            var item = Wait.For(() => Result("Galway"), "Galway result");
            Ui.Select(item);
            FocusAndPress(Main, item, VK.Enter);
            Expect(Wait.Until(() => App.Settings.GetString("location.city") == "Galway", TimeSpan.FromSeconds(3)),
                "Selecting a city result with the keyboard (or UIA SelectionItem) and pressing Enter does not pick it — only a mouse click works");
        });
        Check("Settings location", "mouse pick → location.city = Galway", () =>
        {
            if (App.Settings.GetString("location.city") != "Galway")
            {
                if (Result("Galway") is null)
                {
                    App.EnsureForeground(Main);
                    Ui.SetValue(Field("Search a city…", ControlType.Edit), "");
                    Ui.SetValue(Field("Search a city…", ControlType.Edit), "Galway");
                }
                var item = Wait.For(() => Result("Galway"), "Galway result", TimeSpan.FromSeconds(20));
                App.EnsureForeground(Main);
                Input.ClickElement(Pid, item);
            }
            App.Settings.WaitForString("location.city", "Galway");
            Wait.For(() => Ui.Texts(Page).Any(t => t.StartsWith("Currently Galway", StringComparison.Ordinal)), "'Currently Galway…' caption");
        });
        Combo("Settings location", Field("Units", ControlType.ComboBox), "Imperial (°F, mph)", "location.units", "imperial");
        Combo("Settings location", Field("Units", ControlType.ComboBox), "Metric (°C, km/h)", "location.units", "metric");
        Combo("Settings location", Field("Language & region", ControlType.ComboBox), "English (UK)", "location.language", "en-GB");
        Combo("Settings location", Field("Language & region", ControlType.ComboBox), "English (Ireland)", "location.language", "en-IE");
        AddChip("Settings location", "LocalKeywords", "Testville", "Testville", "location.localKeywords");
        RemoveChip("Settings location", "LocalKeywords", "Testville", "location.localKeywords");
    });

    [Fact]
    public void T04_NewsSettingsAndSources() => Run(() =>
    {
        Section("News");
        AddChip("Settings news", "Interests", "robotics", "robotics", "news.interests");
        RemoveChip("Settings news", "Interests", "robotics", "news.interests");
        AddChip("Settings news", "Muted", "e2e muted topic", "e2e muted topic", "news.muted");
        RemoveChip("Settings news", "Muted", "e2e muted topic", "news.muted");
        Number("Settings news", "Refresh every (minutes)", "15", "news.refreshMinutes", 15);
        Number("Settings news", "Stories to summarise with AI", "8", "news.summarizeTop", 8);

        Check("Settings news", "source switch → news.sources[0].enabled", () =>
        {
            var list = Ui.WaitFind(Page, Ui.Id("SourcesList"), "sources list");
            var sw = Ui.FindAll(list, Ui.Type(ControlType.CheckBox)).First();
            var before = App.Settings.GetBool("news.sources[0].enabled") ?? true;
            Ui.Toggle(sw);
            App.Settings.WaitForBool("news.sources[0].enabled", !before);
            Ui.Toggle(Ui.FindAll(Ui.WaitFind(Page, Ui.Id("SourcesList"), "sources list"), Ui.Type(ControlType.CheckBox)).First());
            App.Settings.WaitForBool("news.sources[0].enabled", before);
        });

        const string url = "https://example.com/e2e-feed.xml";
        bool HasSource(string u) => (App.Settings.Get("news.sources") as JsonArray)!.OfType<JsonObject>().Any(s => (string?)s["url"] == u);
        void TryAdd(string name, string u, string? category = null)
        {
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("NewSourceName"), "source name"), name);
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("NewSourceUrl"), "source url"), u);
            if (category is not null) Ui.SelectComboItem(Ui.WaitFind(Page, Ui.Id("NewSourceCat"), "category"), category, Pid);
            Ui.Invoke(Ui.WaitFind(Page, Ui.Button("Add"), "Add"));
        }
        string Error() => Ui.NameOf(Ui.WaitFind(Page, Ui.Id("SourceError"), "source error"));

        Check("Settings news", "add https feed (tech) → news.sources", () =>
        {
            TryAdd("E2E Feed", url, "tech");
            Wait.For(() => HasSource(url), "feed saved", E2EConfig.PersistTimeout);
            var src = (App.Settings.Get("news.sources") as JsonArray)!.OfType<JsonObject>().First(s => (string?)s["url"] == url);
            Expect((string?)src["category"] == "tech" && (string?)src["name"] == "E2E Feed", $"saved as {src.ToJsonString()}");
            Expect(Error().Length == 0, "error shown: " + Error());
        });
        Check("Settings news", "http:// feed rejected with a message", () =>
        {
            TryAdd("Plain", "http://example.com/rss");
            Wait.For(() => Error() == "Please enter an https:// feed URL.", $"https error (now '{Error()}')");
            Expect(!HasSource("http://example.com/rss"), "http feed was saved");
        });
        Check("Settings news", "garbage URL rejected with a message", () =>
        {
            TryAdd("Junk", "not a url");
            Wait.For(() => Error() == "Please enter an https:// feed URL.", "error message");
        });
        Check("Settings news", "duplicate feed rejected", () =>
        {
            TryAdd("Again", url);
            Wait.For(() => Error() == "That feed is already added.", $"duplicate message (now '{Error()}')");
        });
        Check("Settings news", "remove the added feed", () =>
        {
            var row = Wait.For(() => Ui.FindAll(Ui.WaitFind(Page, Ui.Id("SourcesList"), "list"), Ui.Type(ControlType.DataItem)).FirstOrDefault(d => Ui.Texts(d).Contains("E2E Feed")), "E2E Feed row");
            Ui.Invoke(Ui.WaitFind(row, Ui.Button("Remove source"), "remove"));
            Wait.For(() => !HasSource(url), "feed removed", E2EConfig.PersistTimeout);
        });
    });

    [Fact]
    public void T05_Social() => Run(() =>
    {
        Section("Social");
        AddChip("Settings social", "Subreddits", "r/irishpolitics", "irishpolitics", "social.subreddits");
        RemoveChip("Settings social", "Subreddits", "irishpolitics", "social.subreddits");
        AddChip("Settings social", "Hashtags", "#e2etag", "e2etag", "social.mastodonHashtags");
        RemoveChip("Settings social", "Hashtags", "e2etag", "social.mastodonHashtags");
        AddChip("Settings social", "BskyAccounts", "@someone.bsky.social", "someone.bsky.social", "social.blueskyAccounts");
        RemoveChip("Settings social", "BskyAccounts", "someone.bsky.social", "social.blueskyAccounts");
        Flip("Settings social", "Bluesky trending topics", "social.blueskyTrending");
        Flip("Settings social", "Hacker News front page", "social.hackerNews");
        Number("Settings social", "Refresh every (minutes)", "20", "social.refreshMinutes", 20);
        TypeAndBlur("Settings social", "Social", "Mastodon instance", "mastodon.social", "social.mastodonInstance");
    });

    [Fact]
    public void T06_MarketsHoldingsAndLists() => Run(() =>
    {
        Section("Markets");
        Check("Settings markets", "NVDA holdings fields → shares / costBasis / alertAbove / alertBelow", () =>
        {
            var row = Wait.For(() => Ui.FindAll(Ui.WaitFind(Page, Ui.Id("WatchList"), "watchlist"), Ui.Type(ControlType.DataItem))
                .FirstOrDefault(d => Ui.Texts(d).Any(t => t.StartsWith("NVDA", StringComparison.Ordinal))), "NVDA row");
            var edits = Ui.FindAll(row, Ui.Type(ControlType.Edit));
            Expect(edits.Count == 4, $"{edits.Count} edit boxes in the row");
            var values = new[] { "10", "150.5", "300", "100" };
            for (var i = 0; i < 4; i++) Ui.SetValue(edits[i], values[i]);
            Wait.For(() => WatchRow("NVDA") is { } w && (double?)w["shares"] == 10 && (double?)w["costBasis"] == 150.5 && (double?)w["alertAbove"] == 300 && (double?)w["alertBelow"] == 100,
                "holdings saved", E2EConfig.PersistTimeout);
        });
        Check("Settings markets", "remove IWDA.AS from the watchlist", () =>
        {
            var row = Wait.For(() => Ui.FindAll(Ui.WaitFind(Page, Ui.Id("WatchList"), "watchlist"), Ui.Type(ControlType.DataItem))
                .FirstOrDefault(d => Ui.Texts(d).Any(t => t.StartsWith("IWDA.AS", StringComparison.Ordinal))), "IWDA.AS row");
            Ui.Invoke(Ui.WaitFind(row, Ui.Button("Remove"), "remove"));
            Wait.For(() => WatchRow("IWDA.AS") is null, "IWDA.AS removed", E2EConfig.PersistTimeout);
        });
        AddChip("Settings markets", "Indices", "^gdaxi", "^GDAXI", "markets.indices");
        RemoveChip("Settings markets", "Indices", "^GDAXI", "markets.indices");
        AddChip("Settings markets", "Macro", "gbpeur=x", "GBPEUR=X", "markets.macro");
        RemoveChip("Settings markets", "Macro", "GBPEUR=X", "markets.macro");
        Number("Settings markets", "Big-move alert (%)", "4.5", "markets.alertMovePercent", 4.5);
        Combo("Settings markets", Field("Risk profile", ControlType.ComboBox), "Growth", "markets.riskProfile", "growth");
        Combo("Settings markets", Field("Investment horizon", ControlType.ComboBox), "Short-term (weeks)", "markets.horizon", "short-term");
        Number("Settings markets", "Quote refresh while markets are open (seconds)", "300", "markets.refreshSeconds", 300);
    });

    [Fact]
    public void T07_AgendaCalendarsAndKeys() => Run(() =>
    {
        Section("Agenda");
        bool HasCalendar(string u) => (App.Settings.Get("events.calendars") as JsonArray)!.OfType<JsonObject>().Any(c => (string?)c["url"] == u);
        string CalError() => Ui.NameOf(Ui.WaitFind(Page, Ui.Id("CalError"), "calendar error"));
        void Add(string name, string url)
        {
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("NewCalName"), "calendar name"), name);
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("NewCalUrl"), "calendar url"), url);
            var add = Ui.FindAll(Page, Ui.Button("Add")).First();
            Ui.Invoke(add);
        }
        const string https = "https://example.com/e2e/calendar.ics";
        Check("Settings agenda", "add https .ics calendar", () =>
        {
            Add("E2E Web", https);
            Wait.For(() => HasCalendar(https), "calendar saved", E2EConfig.PersistTimeout);
            Expect(CalError().Length == 0, "error: " + CalError());
        });
        var ics = Path.Combine(E2EConfig.RunDir, "e2e-calendar.ics");
        File.WriteAllText(ics, "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//AquaHub E2E//EN\r\nBEGIN:VEVENT\r\nUID:e2e-1@test\r\n" +
                               $"DTSTART:{DateTime.UtcNow.AddDays(2):yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{DateTime.UtcNow.AddDays(2).AddHours(1):yyyyMMdd'T'HHmmss'Z'}\r\n" +
                               "SUMMARY:E2E test event\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        Check("Settings agenda", "add local .ics file", () =>
        {
            Add("E2E File", ics);
            Wait.For(() => HasCalendar(ics), "file calendar saved", E2EConfig.PersistTimeout);
        });
        Check("Settings agenda", "invalid calendar URLs rejected with a message", () =>
        {
            foreach (var bad in new[] { "http://example.com/cal.ics", "ftp://example.com/x.ics", "C:\\does\\not\\exist.ics", "garbage" })
            {
                Add("Bad", bad);
                Wait.For(() => CalError() == "Enter an https:// ICS link or pick a .ics file.", $"error for '{bad}' (now '{CalError()}')");
                Expect(!HasCalendar(bad), $"'{bad}' was saved");
            }
        });
        Check("Settings agenda", "calendar switch → events.calendars[0].enabled", () =>
        {
            var sw = Ui.FindAll(Ui.WaitFind(Page, Ui.Id("CalendarList"), "calendar list"), Ui.Type(ControlType.CheckBox)).First();
            Ui.Toggle(sw);
            App.Settings.WaitForBool("events.calendars[0].enabled", false);
            Ui.Toggle(Ui.FindAll(Ui.WaitFind(Page, Ui.Id("CalendarList"), "calendar list"), Ui.Type(ControlType.CheckBox)).First());
            App.Settings.WaitForBool("events.calendars[0].enabled", true);
        });
        Check("Settings agenda", "remove calendars", () =>
        {
            foreach (var u in new[] { https, ics })
            {
                var row = Wait.For(() => Ui.FindAll(Ui.WaitFind(Page, Ui.Id("CalendarList"), "list"), Ui.Type(ControlType.DataItem)).FirstOrDefault(d => Ui.Texts(d).Contains(u)), $"row {u}");
                Ui.Invoke(Ui.WaitFind(row, Ui.Type(ControlType.Button), "trash button"));
                Wait.For(() => !HasCalendar(u), $"{u} removed", E2EConfig.PersistTimeout);
            }
        });
        Check("Settings agenda", "Browse… opens a file dialog; picking the .ics fills the fields", () =>
        {
            App.EnsureForeground(Main);
            Ui.Invoke(Ui.WaitFind(Page, Ui.Button("Browse…"), "Browse…"));
            var dialog = Wait.For(() => AppWindows.TopLevel(Pid).FirstOrDefault(w => Ui.NameOf(w) == "Choose a calendar file"), "file dialog", TimeSpan.FromSeconds(15));
            try
            {
                var nameBox = Wait.For(() => Ui.Find(dialog, Ui.And(Ui.Type(ControlType.Edit), Ui.Id("1148"))) ?? Ui.Find(dialog, Ui.Type(ControlType.Edit)), "file name box");
                Ui.SetValue(nameBox, ics);
                var open = Wait.For(() => Ui.Find(dialog, Ui.And(Ui.Type(ControlType.Button), Ui.Id("1"))), "Open button");
                Ui.Invoke(open);
                Wait.For(() => AppWindows.TopLevel(Pid).All(w => Ui.NameOf(w) != "Choose a calendar file"), "dialog closed");
            }
            finally
            {
                if (AppWindows.TopLevel(Pid).FirstOrDefault(w => Ui.NameOf(w) == "Choose a calendar file") is { } still)
                    try { ((WindowPattern)still.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch { }
            }
            Wait.For(() => Ui.ValueOf(Ui.WaitFind(Page, Ui.Id("NewCalUrl"), "url")) == ics, "URL box filled with the file path");
            Expect(Ui.ValueOf(Ui.WaitFind(Page, Ui.Id("NewCalName"), "name")).Length > 0, "name box filled from the file name");
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("NewCalName"), "name"), "");
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("NewCalUrl"), "url"), "");
        });
        AddChip("Settings agenda", "Holidays", "gb", "GB", "events.holidayCountries");
        RejectChip("Settings agenda", "Holidays", "xyz", "events.holidayCountries");
        RemoveChip("Settings agenda", "Holidays", "GB", "events.holidayCountries");
        Flip("Settings agenda", "Economic calendar", "events.economic");
        AddChip("Settings agenda", "Currencies", "jpy", "JPY", "events.economicCurrencies");
        RejectChip("Settings agenda", "Currencies", "ab", "events.economicCurrencies");
        RemoveChip("Settings agenda", "Currencies", "JPY", "events.economicCurrencies");
        Check("Settings agenda", "Finnhub key 'Save key' (in-memory secret store in e2e)", () =>
        {
            Ui.SetValue(Ui.WaitFind(Page, Ui.Id("FinnhubKey"), "key box"), "e2e-test-key-000");
            var save = Ui.ButtonWithText(Page, "Save key") ?? Ui.WaitFind(Page, Ui.Button("Earnings dates (Finnhub) (2)"), "save key");
            Ui.Invoke(save);
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Page, Ui.Id("SavedText"), "saved text")) == "API key saved to Credential Manager", "confirmation text");
            Expect(!(App.Settings.Read().ToJsonString().Contains("e2e-test-key-000")), "the API key was written to settings.json");
        });
        Number("Settings agenda", "Remind me before events (minutes)", "15", "events.reminderMinutes", 15);
        Number("Settings agenda", "Look ahead (days)", "21", "events.lookaheadDays", 21);
    });

    [Fact]
    public void T08_Predictions() => Run(() =>
    {
        Section("Predictions");
        Flip("Settings predictions", "Polymarket", "predictions.polymarket");
        AddChip("Settings predictions", "PmTags", "Science", "science", "predictions.polymarketTags");
        RemoveChip("Settings predictions", "PmTags", "science", "predictions.polymarketTags");
        Flip("Settings predictions", "Kalshi", "predictions.kalshi");
        Number("Settings predictions", "Alert on swings of (percentage points)", "12", "predictions.swingAlertPoints", 12);
        Number("Settings predictions", "Minimum 24h volume (USD)", "10000", "predictions.minVolume24h", 10000);
    });

    [Fact]
    public void T09_AiAndModels() => Run(() =>
    {
        Section("AI & models");
        Flip("Settings AI", "Use local AI", "ai.enabled");
        Combo("Settings AI", Field("Model server", ControlType.ComboBox), "OpenAI-compatible", "ai.provider", "openai");
        Combo("Settings AI", Field("Model server", ControlType.ComboBox), "Ollama", "ai.provider", "ollama");
        Check("Settings AI", "Model combo lists installed models → ai.model", () =>
        {
            var combo = Ui.WaitFind(Page, Ui.Id("ModelCombo"), "model combo");
            Ui.Expand(combo);
            var names = Wait.For(() => Ui.FindAll(combo, Ui.Type(ControlType.ListItem)).Select(Ui.NameOf).ToList() is { Count: > 1 } l ? l : null, "installed models listed");
            Ui.Collapse(combo);
            var model = names.First(n => n != "Automatic (recommended)");
            Ui.SelectComboItem(combo, model, Pid);
            App.Settings.WaitForString("ai.model", model);
            Ui.SelectComboItem(Ui.WaitFind(Page, Ui.Id("ModelCombo"), "model combo"), "Automatic (recommended)", Pid);
            App.Settings.WaitForString("ai.model", "auto");
        });
        Check("Settings AI", "Deep model combo → ai.deepModel", () =>
        {
            var combo = Ui.WaitFind(Page, Ui.Id("DeepModelCombo"), "deep model combo");
            Ui.Expand(combo);
            var names = Wait.For(() => Ui.FindAll(combo, Ui.Type(ControlType.ListItem)).Select(Ui.NameOf).ToList() is { Count: > 1 } l ? l : null, "models listed");
            Ui.Collapse(combo);
            var model = names.First(n => n != "Same as main model");
            Ui.SelectComboItem(combo, model, Pid);
            App.Settings.WaitForString("ai.deepModel", model);
            Ui.SelectComboItem(Ui.WaitFind(Page, Ui.Id("DeepModelCombo"), "deep model combo"), "Same as main model", Pid);
            App.Settings.WaitForString("ai.deepModel", "");
        });
        Check("Settings AI", "Test (Connection) reports the model server", () =>
        {
            var test = Ui.ButtonWithText(Page, "Test") ?? Ui.WaitFind(Page, Ui.Button("Connection"), "test button");
            Ui.Invoke(test);
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Page, Ui.Id("AiTest"), "test result")).StartsWith("Connected to", StringComparison.Ordinal), "'Connected to …'", TimeSpan.FromSeconds(20));
        });
        Flip("Settings AI", "Pause AI while gaming", "ai.pauseWhenFullscreen");
        AddChip("Settings AI", "BriefTimes", "12:30", "12:30", "ai.briefTimes");
        RejectChip("Settings AI", "BriefTimes", "25:99", "ai.briefTimes");
        RemoveChip("Settings AI", "BriefTimes", "12:30", "ai.briefTimes");
        Flip("Settings AI", "Allow model 'thinking'", "ai.think");
        Flip("Settings AI", "Allow a non-local endpoint", "ai.allowRemoteEndpoint");
        TypeAndBlur("Settings AI", "AI & models", "Keep model loaded for", "15m", "ai.keepAlive");
        TypeAndBlur("Settings AI", "AI & models", "Model server (2)", "http://localhost:11434", "ai.endpoint");
        TypeAndBlur("Settings AI", "AI & models", "Model server (2)", "http://127.0.0.1:11434", "ai.endpoint");
    });

    [Fact]
    public void T10_AppsAndScenesEditor() => Run(() =>
    {
        Section("Apps & scenes");
        Check("Settings apps", "Pinned / Music switches → apps[0]", () =>
        {
            var list = Ui.WaitFind(Page, Ui.Id("AppsList"), "apps list");
            var pinned0 = App.Settings.GetBool("apps[0].pinned") ?? true;
            Ui.Toggle(Ui.FindAll(list, Ui.And(Ui.Type(ControlType.CheckBox), Ui.Name("Pinned"))).First());
            App.Settings.WaitForBool("apps[0].pinned", !pinned0);
            Ui.Toggle(Ui.FindAll(Ui.WaitFind(Page, Ui.Id("AppsList"), "apps list"), Ui.And(Ui.Type(ControlType.CheckBox), Ui.Name("Pinned"))).First());
            App.Settings.WaitForBool("apps[0].pinned", pinned0);
            var music0 = App.Settings.GetBool("apps[0].isMusicPlayer") ?? false;
            Ui.Toggle(Ui.FindAll(Ui.WaitFind(Page, Ui.Id("AppsList"), "apps list"), Ui.And(Ui.Type(ControlType.CheckBox), Ui.Name("Music"))).First());
            App.Settings.WaitForBool("apps[0].isMusicPlayer", !music0);
            Ui.Toggle(Ui.FindAll(Ui.WaitFind(Page, Ui.Id("AppsList"), "apps list"), Ui.And(Ui.Type(ControlType.CheckBox), Ui.Name("Music"))).First());
            App.Settings.WaitForBool("apps[0].isMusicPlayer", music0);
        });
        Check("Settings apps", "remove the last app", () =>
        {
            var before = App.Settings.Count("apps");
            var rows = Ui.FindAll(Ui.WaitFind(Page, Ui.Id("AppsList"), "apps list"), Ui.Type(ControlType.DataItem));
            Ui.Invoke(Ui.FindAll(rows.Last(), Ui.Type(ControlType.Button)).Last());
            Wait.For(() => App.Settings.Count("apps") == before - 1, "apps count -1", E2EConfig.PersistTimeout);
        });

        int Scenes() => App.Settings.Count("scenes");
        AutomationElement SceneGroup(string prefix) =>
            Wait.For(() => Ui.FindAll(Page, Ui.Type(ControlType.Group)).FirstOrDefault(g => Ui.NameOf(g).StartsWith(prefix, StringComparison.Ordinal)), $"scene expander '{prefix}'");
        AutomationElement Expanded(string prefix)
        {
            var g = SceneGroup(prefix);
            if (Ui.ExpandStateOf(g) != ExpandCollapseState.Expanded) Ui.Expand(g);
            Wait.For(() => Ui.ExpandStateOf(SceneGroup(prefix)) == ExpandCollapseState.Expanded, "expanded");
            return SceneGroup(prefix);
        }
        JsonObject LastScene() => (App.Settings.Get("scenes") as JsonArray)!.OfType<JsonObject>().Last();

        var baseline = Scenes();
        Check("Settings scenes", "New scene → scenes +1, editor expanded", () =>
        {
            Ui.Invoke(Ui.WaitButtonWithText(Page, "New scene"));
            Wait.For(() => Scenes() == baseline + 1, "scene added", E2EConfig.PersistTimeout);
            Expect(Ui.ExpandStateOf(SceneGroup("New scene")) == ExpandCollapseState.Expanded, "new scene editor not expanded");
        });
        Check("Settings scenes", "edit name + description → saved", () =>
        {
            var g = Expanded("New scene");
            Ui.SetValue(Ui.WaitFind(g, Ui.And(Ui.Type(ControlType.Edit), Ui.Name("Scene name")), "scene name"), "E2E Scene");
            Ui.SetValue(Ui.WaitFind(Expanded("E2E Scene"), Ui.And(Ui.Type(ControlType.Edit), Ui.Name("What it does")), "description"), "Created by the E2E suite");
            Wait.For(() => (string?)LastScene()["name"] == "E2E Scene" && (string?)LastScene()["description"] == "Created by the E2E suite", "name/description saved", E2EConfig.PersistTimeout);
        });
        Check("Settings scenes", "Add step → steps +1 (default: do-not-disturb on)", () =>
        {
            Ui.Invoke(Ui.WaitFind(Expanded("E2E Scene"), Ui.Button("Add step"), "Add step"));
            Wait.For(() => (LastScene()["steps"] as JsonArray)?.Count == 1, "one step saved", E2EConfig.PersistTimeout);
        });
        Check("Settings scenes", "editor stays open after adding a step", () =>
            Expect(Ui.ExpandStateOf(SceneGroup("E2E Scene")) == ExpandCollapseState.Expanded,
                "Adding a step rebuilds the list and collapses the scene being edited — the user has to re-open it after every change"));
        Check("Settings scenes", "change step action to 'volume' and value 30", () =>
        {
            var g = Expanded("E2E Scene");
            var action = Ui.FindAll(g, Ui.Type(ControlType.ComboBox)).First();
            Ui.SelectComboItem(action, "volume", Pid);
            Wait.For(() => (string?)LastScene()["steps"]![0]!["action"] == "volume", "action saved", E2EConfig.PersistTimeout);
            var value = Ui.WaitFind(Expanded("E2E Scene"), Ui.And(Ui.Type(ControlType.Edit), Ui.Name("0–100")), "value box (0–100)");
            Ui.SetValue(value, "30");
            Wait.For(() => (string?)LastScene()["steps"]![0]!["value"] == "30", "value saved", E2EConfig.PersistTimeout);
        });
        Check("Settings scenes", "second step: launch Calculator (app picker)", () =>
        {
            Ui.Invoke(Ui.WaitFind(Expanded("E2E Scene"), Ui.Button("Add step"), "Add step"));
            Wait.For(() => (LastScene()["steps"] as JsonArray)?.Count == 2, "two steps", E2EConfig.PersistTimeout);
            var combos = Ui.FindAll(Expanded("E2E Scene"), Ui.Type(ControlType.ComboBox));
            Ui.SelectComboItem(combos[1], "launch", Pid);
            Wait.For(() => (string?)LastScene()["steps"]![1]!["action"] == "launch", "launch saved", E2EConfig.PersistTimeout);
            var appCombo = Ui.FindAll(Expanded("E2E Scene"), Ui.Type(ControlType.ComboBox))[2];
            Ui.Expand(appCombo);
            var item = Wait.For(() => Ui.FindAll(appCombo, Ui.Type(ControlType.ListItem)).FirstOrDefault(i => Ui.Texts(i).Contains("Calculator") || Ui.NameOf(i).Contains("Calculator")), "Calculator entry");
            Ui.Select(item);
            try { Ui.Collapse(appCombo); } catch { }
            Wait.For(() => (string?)LastScene()["steps"]![1]!["target"] == "calculator", "target saved", E2EConfig.PersistTimeout);
        });
        Check("Settings scenes", "Run now → journal volume 30 + launch calculator", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Ui.WaitFind(Expanded("E2E Scene"), Ui.Button("Run now"), "Run now"));
            App.Journal.WaitFor(mark, "volume", d => d == "30");
            App.Journal.WaitFor(mark, "launch", d => d == "calculator");
        });
        Check("Settings scenes", "Remove step → steps -1", () =>
        {
            Ui.Invoke(Ui.FindAll(Expanded("E2E Scene"), Ui.Button("Remove step")).Last());
            Wait.For(() => (LastScene()["steps"] as JsonArray)?.Count == 1, "one step left", E2EConfig.PersistTimeout);
        });
        Check("Settings scenes", "Delete scene → removed", () =>
        {
            Ui.Invoke(Ui.WaitFind(Expanded("E2E Scene"), Ui.Button("Delete scene"), "Delete scene"));
            Wait.For(() => Scenes() == baseline, "scene removed", E2EConfig.PersistTimeout);
        });
    });

    [Fact]
    public void T11_Notifications() => Run(() =>
    {
        Section("Notifications");
        foreach (var (name, path) in new[]
                 {
                     ("Notifications", "notifications.enabled"), ("Do not disturb", "notifications.doNotDisturb"),
                     ("Hold notifications while gaming or presenting", "notifications.suppressWhenFullscreen"), ("Market moves & price targets", "notifications.priceAlerts"),
                     ("Breaking stories", "notifications.bigStories"), ("Prediction market swings", "notifications.predictionSwings"),
                     ("Calendar reminders", "notifications.calendarReminders"), ("PC health warnings", "notifications.systemWarnings"), ("Brief is ready", "notifications.briefReady"),
                 })
            Flip("Settings notifications", name, path);
        AddChip("Settings notifications", "Keywords", "e2e-keyword", "e2e-keyword", "notifications.keywords");
        RemoveChip("Settings notifications", "Keywords", "e2e-keyword", "notifications.keywords");
        TypeAndBlur("Settings notifications", "Notifications", "Quiet hours", "22:00", "notifications.quietStart");
        TypeAndBlur("Settings notifications", "Notifications", "Quiet hours (2)", "06:30", "notifications.quietEnd");
    });

    [Fact]
    public void T12_PrivacyDataAndAbout() => Run(() =>
    {
        Section("Privacy & data");
        Flip("Settings privacy", "Load article images", "privacy.loadRemoteImages");
        Number("Settings privacy", "Keep items for (days)", "14", "privacy.retentionDays", 14);
        Check("Settings privacy", "Open data folder → journal open-folder", () =>
            ExpectJournal("open-folder", () => Ui.Invoke(Ui.ButtonWithText(Page, "Open data folder") ?? Field("Local database", ControlType.Button)), d => d.Contains(App.ProfileDir, StringComparison.OrdinalIgnoreCase)));
        Check("Settings privacy", "Open log → journal open-log", () =>
            ExpectJournal("open-log", () => Ui.Invoke(Ui.ButtonWithText(Page, "Open log") ?? Field("Diagnostics log", ControlType.Button)), d => d.EndsWith("aquahub.log", StringComparison.OrdinalIgnoreCase)));
        Check("Settings privacy", "Clear caches → confirmation + agents rerun", () =>
        {
            var mark = App.Log.Mark();
            Ui.Invoke(Ui.ButtonWithText(Page, "Clear caches") ?? Field("Local database (2)", ControlType.Button));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Page, Ui.Id("SavedText"), "saved text")) == "Caches cleared", "'Caches cleared'");
            App.Log.WaitForLine(mark, l => l.Contains("[agents] "), "agents rerun", TimeSpan.FromSeconds(30));
        });
        Check("Settings privacy", "Edit JSON → journal edit-json", () =>
            ExpectJournal("edit-json", () => Ui.Invoke(Ui.WaitFind(Page, Ui.Button("Open settings.json in Notepad (power users)"), "Edit JSON")), d => d.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase)));
        Section("About");
        Check("Settings about", "version and profile path shown", () =>
        {
            Expect(Ui.NameOf(Ui.WaitFind(Page, Ui.Id("VersionText"), "version")).StartsWith("Version ", StringComparison.Ordinal), "version text");
            Expect(Ui.NameOf(Ui.WaitFind(Page, Ui.Id("PathsText"), "paths")).Contains(App.ProfileDir, StringComparison.OrdinalIgnoreCase), "profile path");
        });
        Check("Settings about", "Updates › Check now → journal update-check (a test session never contacts GitHub)", () =>
        {
            ExpectJournal("update-check", () => Ui.Invoke(Ui.WaitFind(Page, Ui.Id("update-check"), "Check now")), d => d == "user");
            Expect(Ui.NameOf(Ui.WaitFind(Page, Ui.Id("update-status"), "update status")).Contains("simulated", StringComparison.OrdinalIgnoreCase), "status says the check is simulated");
            Expect(Ui.Find(Page, Ui.Id("update-action")) is null, "no Download / Restart now button without an update");
        });
        Flip("Settings about", "Check for updates automatically", "general.checkForUpdates");
    });

    [Fact]
    public void T13_ChangesMadeElsewhereSurviveLeavingSettings() => Run(() =>
    {
        Check("Settings consistency", "DND turned on from the title bar while Settings is open survives leaving Settings", () =>
        {
            Section("General");
            var before = App.Settings.GetBool("notifications.doNotDisturb") ?? false;
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("DndButton"), "DND button"));
            App.Settings.WaitForBool("notifications.doNotDisturb", !before);
            GoTo("today");
            Thread.Sleep(2000);
            var after = App.Settings.GetBool("notifications.doNotDisturb");
            if (after != !before)
            {
                Ui.Invoke(Ui.WaitFind(Main, Ui.Id("DndButton"), "DND button"));
                throw new Xunit.Sdk.XunitException(
                    $"Leaving Settings wrote its stale copy back: doNotDisturb reverted from {!before} to {after} (title-bar/tray/scene changes are lost)");
            }
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("DndButton"), "DND button"));
            App.Settings.WaitForBool("notifications.doNotDisturb", before);
        });
        Check("Settings consistency", "Settings page reflects a change made elsewhere when reopened", () =>
        {
            var before = App.Settings.GetBool("notifications.doNotDisturb") ?? false;
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("DndButton"), "DND button"));
            App.Settings.WaitForBool("notifications.doNotDisturb", !before);
            Section("Notifications");
            var sw = Field("Do not disturb", ControlType.CheckBox);
            Expect((Ui.ToggleStateOf(sw) == ToggleState.On) == !before, "Notifications › Do not disturb switch shows a stale value");
            Ui.Toggle(sw);
            App.Settings.WaitForBool("notifications.doNotDisturb", before);
        });
    });

    [Fact]
    public void T14_InvalidQuietHoursAreNormalised() => Run(() =>
    {
        Check("Settings notifications", "quiet hours '99:99' falls back to a valid time", () =>
        {
            Section("Notifications");
            App.EnsureForeground(Main);
            var box = Field("Quiet hours", ControlType.Edit);
            Wait.Retry(box.SetFocus, "focus");
            Ui.SetValue(box, "99:99");
            Wait.Retry(() => Ui.WaitFind(Page, Ui.Id("Sections"), "sections").SetFocus(), "blur");
            GoTo("today");
            Wait.For(() => App.Settings.GetString("notifications.quietStart") is { } s && TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "a valid HH:mm quietStart", E2EConfig.PersistTimeout);
        });
    });

    [Fact]
    public void T15_VoiceInputAndWhisper() => Run(() =>
    {
        Section("Ask Aqua");
        Combo("Settings voice", Field("Voice input", ControlType.ComboBox), "Whisper (on this PC)", "ask.voice", "whisper");
        Combo("Settings voice", Ui.WaitFind(Page, Ui.Id("whisper-model"), "Whisper model"), "Base — fastest, 57 MB", "ask.whisperModel", "base");
        Combo("Settings voice", Ui.WaitFind(Page, Ui.Id("whisper-model"), "Whisper model"), "Small — recommended, 181 MB", "ask.whisperModel", "small");
        Check("Settings voice", "Whisper › Install → journal whisper-install (simulated: nothing is downloaded)", () =>
            ExpectJournal("whisper-install", () => Ui.Invoke(Ui.WaitFind(Page, Ui.Id("whisper-install"), "Install Whisper"))));
        Flip("Settings voice", "Suggest Whisper while I dictate", "ask.whisperTip");
        Combo("Settings voice", Field("Voice input", ControlType.ComboBox), "Windows, on this PC", "ask.voice", "offline");
    });
}
