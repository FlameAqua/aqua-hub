namespace AquaHub.E2E.Tests;

/// <summary>News page: filter chips, text filter, per-story Save / Copy link / Ask / Open and source chips.</summary>
public sealed class A04_NewsTests : E2ETestBase
{
    public A04_NewsTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private static readonly string[] Chips = { "All", "Local", "World", "Europe", "Business", "Tech", "Gaming & internet", "Saved" };

    private AutomationElement News() => GoTo("news");
    private AutomationElement List() => Ui.WaitFind(PageRoot("news"), Ui.Id("List"), "stories list");
    private List<AutomationElement> Items() => Ui.FindAll(List(), Ui.Type(ControlType.ListItem), TreeScope.Children);

    /// <summary>Headline of a story card: the first text after the card's action buttons.</summary>
    private static string TitleOf(AutomationElement item)
    {
        var snap = TreeSnapshot.Capture(item);
        var kids = snap.Children;
        var open = kids.FindIndex(k => k.Type == ControlType.Button && k.Name == "Open the article");
        return kids.Skip(open + 1).FirstOrDefault(k => k.Type == ControlType.Text)?.Name ?? "";
    }

    private static string CategoryOf(AutomationElement item) =>
        TreeSnapshot.Capture(item).Children.FirstOrDefault(k => k.Type == ControlType.Text)?.Name ?? "";

    private AutomationElement Chip(string label) =>
        Ui.WaitFind(PageRoot("news"), Ui.And(Ui.Type(ControlType.RadioButton), Ui.Name(label)), $"chip {label}");

    private AutomationElement ItemWithTitle(string title) =>
        Wait.For(() => Items().FirstOrDefault(i => TitleOf(i) == title), $"story card '{title}'");

    [Fact]
    public void T01_FilterChips() => Run(() =>
    {
        News();
        foreach (var chip in Chips)
        {
            Check("News filters", $"chip '{chip}'", () =>
            {
                Ui.Select(Chip(chip));
                Wait.For(() => Ui.IsSelected(Chip(chip)), "chip selected");
                Thread.Sleep(400);
                var items = Items();
                if (chip is "All")
                    Expect(items.Count > 0, "no stories under All");
                if (chip is "Local" or "World" or "Europe" or "Business" or "Tech" or "Gaming & internet")
                {
                    var wrong = items.Take(6).Select(CategoryOf).Where(c => c != chip).ToList();
                    Expect(wrong.Count == 0, $"stories from other categories under '{chip}': {string.Join(", ", wrong)}");
                }
                if (items.Count == 0)
                    Ui.WaitFind(PageRoot("news"), Ui.Id("EmptyText"), "empty-state text");
            });
        }
        Ui.Select(Chip("All"));
    });

    [Fact]
    public void T02_FilterTextBox() => Run(() =>
    {
        News();
        Ui.Select(Chip("All"));
        var search = Ui.WaitFind(PageRoot("news"), Ui.Id("Search"), "filter box");
        Check("News filters", "text filter narrows to matching stories", () =>
        {
            var title = TitleOf(Items()[0]);
            var word = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.All(char.IsLetter)).OrderByDescending(w => w.Length).First();
            Ui.SetValue(search, word);
            Wait.For(() => Items().Count > 0 && Items().Take(5).All(i => Ui.Texts(i).Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase))),
                $"only stories mentioning '{word}'");
        });
        Check("News filters", "nonsense filter shows the empty state", () =>
        {
            Ui.SetValue(search, "zzqxjv");
            Wait.For(() => Items().Count == 0, "no stories");
            var empty = Ui.WaitFind(PageRoot("news"), Ui.Id("EmptyText"), "empty text");
            Expect(Ui.NameOf(empty) == "No stories match this filter.", $"empty text was '{Ui.NameOf(empty)}'");
        });
        Check("News filters", "clearing the filter restores the list", () =>
        {
            Ui.SetValue(search, "");
            Wait.For(() => Items().Count > 3, "stories back");
        });
    });

    [Fact]
    public void T03_SaveShowsUnderSavedAndUnsave() => Run(() =>
    {
        News();
        Ui.Select(Chip("All"));
        var title = TitleOf(Items()[0]);
        void ClickSave() => Ui.Invoke(Ui.WaitFind(ItemWithTitle(title), Ui.Button("Save for later"), "save button"));
        void Refilter(string chip) { Ui.Select(Chip("All")); Ui.Select(Chip(chip)); Thread.Sleep(300); }
        Check("News story", "Save then Save again on the same card (All) → not listed under Saved", () =>
        {
            ClickSave();
            ClickSave();
            Refilter("Saved");
            Wait.For(() => Items().All(i => TitleOf(i) != title), "story not saved");
            Ui.Select(Chip("All"));
        });
        Check("News story", "Save for later → appears under Saved", () =>
        {
            ClickSave();
            Refilter("Saved");
            ItemWithTitle(title);
        });
        Check("News story", "bookmark on a saved story (Saved filter) unsaves it", () =>
        {
            ClickSave();
            Refilter("Saved");
            var gone = Wait.Until(() => Items().All(i => TitleOf(i) != title), TimeSpan.FromSeconds(4));
            Expect(gone, "Clicking the bookmark of a saved story in the Saved list saves it again instead of removing it " +
                         "(the rebuilt card does not know it is saved, and the button exposes no toggle state)");
        });
        Ui.Select(Chip("All"));
    });

    [Fact]
    public void T04_CopyAskOpenAndSourceChips() => Run(() =>
    {
        News();
        Ui.Select(Chip("All"));
        var title = TitleOf(Items()[0]);
        Check("News story", "Copy link → journal clipboard", () =>
            ExpectJournal("clipboard", () => Ui.Invoke(Ui.WaitFind(ItemWithTitle(title), Ui.Button("Copy link"), "copy")), d => d.StartsWith("http", StringComparison.Ordinal)));
        Check("News story", "Open the article → journal open-url", () =>
            ExpectJournal("open-url", () => Ui.Invoke(Ui.WaitFind(ItemWithTitle(title), Ui.Button("Open the article"), "open")), d => d.StartsWith("http", StringComparison.Ordinal)));

        var sources = Ui.FindAllWhere(ItemWithTitle(title), ControlType.Button, n => n.StartsWith("Read on ", StringComparison.Ordinal)).Select(Ui.NameOf).ToList();
        Check("News story", "source chips present", () => Expect(sources.Count > 0, "no 'Read on …' chips"));
        foreach (var s in sources)
        {
            Check("News story", $"source chip '{s}' → journal open-url", () =>
            {
                var chip = Ui.WaitFind(ItemWithTitle(title), Ui.Button(s), s);
                var url = chip.Current.HelpText;
                ExpectJournal("open-url", () => Ui.Invoke(chip), d => d.StartsWith("http", StringComparison.Ordinal) && (url.Length == 0 || d.Length > 10));
            });
        }
        Check("News story", "Ask Aqua about this story → Ask page with the question", () =>
        {
            Ui.Invoke(Ui.WaitFind(ItemWithTitle(title), Ui.Button("Ask Aqua about this story"), "ask"));
            ExpectPage("ask");
            var ask = PageRoot("ask");
            Wait.For(() => Ui.AllTexts(ask).Any(t => t.StartsWith("Tell me more about:", StringComparison.Ordinal)), "question in chat");
            if (Ui.Find(ask, Ui.Id("StopButton")) is { } stop) Ui.Invoke(stop);
        });
        GoTo("news");
    });
}
