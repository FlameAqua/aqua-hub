namespace AquaHub.E2E.Tests;

/// <summary>Social pulse: platform chips, Sources → Settings, topic posts and feed rows.</summary>
public sealed class A05_SocialTests : E2ETestBase
{
    public A05_SocialTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private static readonly (string Label, string Platform)[] Platforms =
    {
        ("All", ""), ("Reddit", "Reddit"), ("Mastodon", "Mastodon"), ("Bluesky", "Bluesky"), ("Hacker News", "Hacker News"), ("YouTube", "YouTube"),
    };

    private AutomationElement Feed() => Ui.WaitFind(PageRoot("social"), Ui.Id("Feed"), "latest posts list");
    private List<AutomationElement> FeedRows() =>
        Ui.FindAll(Feed(), Ui.Type(ControlType.ListItem), TreeScope.Children)
          .Select(li => Ui.Find(li, Ui.Type(ControlType.Button), TreeScope.Children)).Where(b => b is not null).Select(b => b!).ToList();

    [Fact]
    public void T01_PlatformChipsFilterTheFeed() => Run(() =>
    {
        GoTo("social");
        foreach (var (label, platform) in Platforms)
        {
            Check("Social filters", $"chip '{label}'", () =>
            {
                var chip = Ui.WaitFind(PageRoot("social"), Ui.And(Ui.Type(ControlType.RadioButton), Ui.Name(label)), label);
                Ui.Select(chip);
                Wait.For(() => Ui.IsSelected(chip), "chip selected");
                Thread.Sleep(400);
                var rows = FeedRows();
                if (label == "All") Expect(rows.Count > 0, "feed empty under All");
                if (platform.Length > 0)
                {
                    var wrong = rows.Take(6).Where(r => !Ui.Texts(r).Contains(platform)).Select(Ui.NameOf).ToList();
                    Expect(wrong.Count == 0, $"posts from other platforms under {label}: {string.Join(" | ", wrong.Take(3))}");
                }
                Step($"   {label}: {rows.Count} posts");
            });
        }
        Ui.Select(Ui.WaitFind(PageRoot("social"), Ui.And(Ui.Type(ControlType.RadioButton), Ui.Name("All")), "All"));
    });

    [Fact]
    public void T02_SourcesButtonOpensSocialSettings() => Run(() =>
    {
        Check("Social", "Sources → Settings › Social", () =>
        {
            Ui.Invoke(Ui.WaitButtonWithText(GoTo("social"), "Sources"));
            ExpectPage("settings");
            var settings = PageRoot("settings");
            Ui.WaitFind(settings, Ui.Id("Subreddits"), "Subreddits editor (Social section)");
            var selected = Ui.FindAll(Ui.WaitFind(settings, Ui.Id("Sections"), "sections"), Ui.Type(ControlType.ListItem)).FirstOrDefault(Ui.IsSelected);
            Expect(selected is not null && Ui.Texts(selected).Contains("Social"), "Social section should be selected");
        });
    });

    [Fact]
    public void T03_TopicPostsAndFeedRowsOpenLinks() => Run(() =>
    {
        var page = GoTo("social");
        var snap = TreeSnapshot.Capture(page);
        var feed = snap.Descendants().First(n => n.AutomationId == "Feed");
        var feedNames = feed.Descendants().Where(n => n.Type == ControlType.Button).Select(n => n.Name).ToHashSet();
        var topicPosts = snap.Descendants()
            .Where(n => n.Type == ControlType.Button && n.Name.Length > 0 && n.Parent?.Type == ControlType.DataItem && !feedNames.Contains(n.Name))
            .Select(n => n.Name).Distinct().ToList();
        Check("Social", "topic post rows present", () => Expect(topicPosts.Count > 0, "no posts under the pulse topics"));
        foreach (var name in topicPosts.Take(8))
            Check("Social topics", $"post '{Short(name)}' → journal open-url", () =>
                ExpectJournal("open-url", () => Ui.Invoke(Ui.WaitFind(PageRoot("social"), Ui.Button(name), "post")), d => d.StartsWith("http", StringComparison.Ordinal)));

        var rows = FeedRows().Take(6).Select(Ui.NameOf).ToList();
        Check("Social", "feed rows present", () => Expect(rows.Count > 0, "feed empty"));
        foreach (var name in rows)
            Check("Social feed", $"feed row '{Short(name)}' → journal open-url", () =>
                ExpectJournal("open-url", () => Ui.Invoke(Ui.WaitFind(Feed(), Ui.Button(name), "feed row")), d => d.StartsWith("http", StringComparison.Ordinal)));
    });

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
