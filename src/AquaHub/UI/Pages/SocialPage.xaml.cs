using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Util;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record TopicVM(string Title, string Summary, string Sentiment, Brush SentimentBrush, double HeatValue, List<PostVM> Posts)
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => Title;
}

public sealed class SocialVM : ObservableObject
{
    public string Overview { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Meta { get; set; } = "";
    public List<TopicVM> Topics { get; set; } = new();
    public bool HasTopics => Topics.Count > 0 || Overview.Length > 0;
    /// <summary>Global Bluesky trends: context only, kept out of the local pulse.</summary>
    public List<PostVM> Trending { get; set; } = new();
    public bool HasTrending => Trending.Count > 0;
    public void Changed() => RaiseAll();
}

public partial class SocialPage : UserControl, IPage
{
    private readonly SocialVM _vm = new();
    private readonly UiThrottle _refresh;
    private string _platform = "all";

    public SocialPage()
    {
        InitializeComponent();
        DataContext = _vm;
        _refresh = new UiThrottle(Refresh, 200);
        foreach (var (id, label) in new[] { ("all", "All"), ("following", "Following"), ("reddit", "Reddit"), ("mastodon", "Mastodon"), ("bluesky", "Bluesky"), ("hackernews", "Hacker News"), ("youtube", "YouTube") })
        {
            var chip = new RadioButton { Content = label, Tag = id, Style = (Style)FindResource("Chip"), GroupName = "platform", IsChecked = id == "all" };
            chip.Checked += (_, _) => { _platform = id; RefreshFeed(); };
            Platforms.Children.Add(chip);
        }
    }

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        // A platform to open on ("youtube", "bluesky"…), e.g. from a link elsewhere.
        if (arg is not null && Platforms.Children.OfType<RadioButton>().FirstOrDefault(c => c.Tag as string == arg) is { } chip) chip.IsChecked = true;
        Refresh();
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        if (topic is Topics.Social or Topics.Pulse) _refresh.Request();
    }

    private void Refresh()
    {
        var pulse = Hub.State.Pulse;
        var byId = Hub.State.Social.ToDictionary(p => p.Id, p => p);
        if (pulse is not null)
        {
            _vm.Overview = pulse.Overview;
            _vm.Badge = pulse.IsAi ? "AI" : "";
            _vm.Meta = $"{Plural.Of(pulse.PostCount, "post")} · updated {TimeText.AgoPhrase(pulse.GeneratedAt)}" + (pulse.IsAi ? $" · {pulse.Model}" : " · keyword digest");
            _vm.Topics = pulse.Topics.Select(t => new TopicVM(t.Title, t.Summary, t.Sentiment, Fmt.SentimentBrush(t.Sentiment), t.Heat / 5.0,
                t.ItemIds.Where(byId.ContainsKey).Take(3).Select(id => new PostVM(byId[id])).ToList())).ToList();
        }
        else
        {
            _vm.Overview = Hub.State.Social.Count == 0 ? "Your Social Scout is reading your feeds…" : "Distilling the conversation — the AI pulse will appear shortly.";
        }
        _vm.Trending = Hub.State.Social.Where(IsTrending).OrderByDescending(p => p.Score).Take(8).Select(p => new PostVM(p)).ToList();
        _vm.Changed();
        RefreshFeed();
    }

    private static bool IsTrending(Core.Models.FeedItem p) => p.SourceId == "bluesky:trending";

    /// <summary>Your own timelines (Bluesky Following, Mastodon home).</summary>
    private static bool IsFollowing(Core.Models.FeedItem p) =>
        p.SourceId == "bluesky:timeline" || p.SourceId.EndsWith(":home", StringComparison.Ordinal);

    private void RefreshFeed()
    {
        var filtered = Hub.State.Social.Where(p => !IsTrending(p) &&
            (_platform == "all" || (_platform == "following" ? IsFollowing(p) : p.Platform == _platform)));
        // Round-robin across sources (each already ranked) so local communities are not drowned out by high-score sites.
        var queues = filtered.GroupBy(p => p.SourceId).Select(g => new Queue<Core.Models.FeedItem>(g)).ToList();
        var mixed = new List<Core.Models.FeedItem>();
        while (mixed.Count < 150 && queues.Any(q => q.Count > 0))
            foreach (var q in queues.Where(q => q.Count > 0)) mixed.Add(q.Dequeue());
        var posts = mixed.Take(150).Select(p => new PostVM(p)).ToList();
        Feed.ItemsSource = posts;
        ShowEmpty(posts.Count == 0 ? EmptyFor(_platform) : null);
    }

    private sealed record EmptyState(string Icon, string Title, string Body, string Action, string Target);

    private EmptyState? _empty;

    /// <summary>Why a platform tab has no posts: not set up (with a button to the right setting), or nothing new yet.</summary>
    private static EmptyState EmptyFor(string platform)
    {
        var s = Hub.S.Social;
        var blueskySignedIn = s.BlueskyHandle.Length > 0 && Hub.Core.Secrets.Get(Core.Settings.SecretKeys.BlueskyAppPassword) is { Length: > 0 };
        var mastodonSignedIn = s.MastodonInstance.Length > 0 && Hub.Core.Secrets.Get(Core.Settings.SecretKeys.MastodonToken) is { Length: > 0 };
        var configured = platform switch
        {
            "reddit" => s.Subreddits.Count > 0,
            "mastodon" => s.MastodonInstance.Length > 0 && (s.MastodonHashtags.Count > 0 || (s.MastodonHome && mastodonSignedIn)),
            "bluesky" => s.BlueskyAccounts.Count > 0 || s.BlueskyFeeds.Count > 0 || (s.BlueskyTimeline && blueskySignedIn),
            "hackernews" => s.HackerNews,
            "youtube" => s.YouTubeChannels.Count > 0,
            "following" => (s.BlueskyTimeline && blueskySignedIn) || (s.MastodonHome && mastodonSignedIn),
            _ => true,
        };
        if (!configured)
            return platform switch
            {
                "youtube" => new("play", "No YouTube channels yet", "Add channels you like — paste a channel link or @handle — and their new videos show up here.", "Add YouTube channels", "social:youtube"),
                "reddit" => new("social", "No subreddits yet", "Add communities such as r/ireland to see what they're discussing.", "Add subreddits", "social:reddit"),
                "mastodon" => new("hash", "Mastodon isn't set up", "Follow hashtags on your server, or connect your account to read your home timeline.", "Set up Mastodon", "social:mastodon"),
                "bluesky" => new("globe", "No Bluesky posts to show", "Trending topics appear under the pulse. Follow accounts or custom feeds, or connect your account for your timeline.", "Set up Bluesky", "social:bluesky"),
                "hackernews" => new("bolt", "Hacker News is off", "Turn it on to see the stories tech people are discussing.", "Turn on Hacker News", "social:hackernews"),
                "following" => new("user", "No timelines connected", "Connect your Bluesky or Mastodon account to see posts from the people you follow.", "Connect an account", "social:bluesky"),
                _ => new("social", "Nothing here yet", "Add communities, hashtags, accounts or channels to follow.", "Choose sources", "social"),
            };
        var minutes = s.RefreshMinutes;
        return new("clock", "No posts yet", $"New posts appear here as they arrive (checked every {minutes} minutes).", "Check now", "@refresh");
    }

    private void ShowEmpty(EmptyState? state)
    {
        _empty = state;
        FeedEmpty.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        Feed.Visibility = state is null ? Visibility.Visible : Visibility.Collapsed;
        if (state is null) return;
        FeedEmptyIcon.Kind = state.Icon;
        FeedEmptyTitle.Text = state.Title;
        FeedEmptyBody.Text = state.Body;
        FeedEmptyAction.Content = state.Action;
        AutomationProperties.SetName(FeedEmptyAction, state.Action);
    }

    private void OnFeedEmptyAction(object sender, RoutedEventArgs e)
    {
        if (_empty is null) return;
        if (_empty.Target == "@refresh")
        {
            Hub.Core.Agents.RunNow("social-scout");
            FeedEmptyBody.Text = "Checking your sources…";
            return;
        }
        Hub.Windows.ShowMain("settings", _empty.Target);
    }

    private void OnConfigure(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "social");
}
