using System.Text.Json.Nodes;
using AquaHub.Core.Data;
using AquaHub.Core.Models;

namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Streams")]
public sealed class A26_StreamsTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    private const string ChannelId = "UCabcdefghijklmnopqrstuv";
    protected override SessionOptions Options => new()
    {
        Offline = true,
        EditSettings = root => root["social"] = new JsonObject
        {
            ["streams"] = new JsonObject { ["youTube"] = true },
            ["youTubeChannels"] = new JsonArray(new JsonObject { ["channelId"] = ChannelId, ["name"] = "Fixture channel" }),
        },
        SeedProfile = profile =>
        {
            var database = Path.Combine(profile, "hub.db");
            try
            {
                var stream = new LiveStream("youtube:abcDEF123_-", "youtube", ChannelId, "Fixture channel", "Fixture live broadcast", "https://www.youtube.com/watch?v=abcDEF123_-");
                new HubDatabase(database).PutJson("state:streams", new StreamsSnapshot(new[]
                {
                    new StreamObservation("youtube:" + ChannelId, "youtube", "Fixture channel", DateTimeOffset.UtcNow, true, "", new[]
                    { stream, stream with { Id = "youtube:badDEF123_-", Url = "https://evil.example/" } }),
                }));
            }
            finally { HubDatabase.ReleaseFile(database); }
        },
    };
    private AutomationElement Field(string id) => Ui.WaitFind(Main, Ui.Id(id), id);

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_ObservedStreamsExposeSafeLinksAndRefreshIsIntercepted() => Run(() =>
    {
        GoTo("social");
        Wait.For(() => Ui.FindAll(Field("streams-list"), Ui.Id("stream-open")).Count == 1, "one validated stream card");
        var open = Field("stream-open");
        Assert.Contains("Fixture live broadcast", Ui.NameOf(open));
        Assert.Contains("YouTube", Ui.NameOf(open));
        Shot("streams-card");
        ExpectJournal("open-url", () => Ui.Invoke(open), detail => detail == "https://www.youtube.com/watch?v=abcDEF123_-");
        ExpectJournal("streams-refresh", () => Ui.Invoke(Field("streams-refresh")));
    });

    [Fact]
    public void T02_DisablingSourceRemovesCachedRowsAndPersists() => Run(() =>
    {
        GoTo("social");
        Ui.Invoke(Field("streams-sources"));
        PageRoot("settings");
        Ui.SetToggle(Field("settings-streams-youtube"), false);
        App.Settings.WaitForBool("social.streams.youTube", false);
        GoTo("social");
        Wait.For(() => Ui.FindAll(Field("streams-list"), Ui.Id("stream-open")).Count == 0, "disabled channel removed");
        Assert.Contains("Connect Twitch or enable YouTube", Ui.NameOf(Field("streams-summary")));
    });

    [Fact]
    public void T03_TwitchRequiresPublicClientIdAndNeverSignsInDuringOfflineE2E() => Run(() =>
    {
        GoTo("social"); Ui.Invoke(Field("streams-sources")); PageRoot("settings");
        Assert.Null(Ui.Find(PageRoot("settings"), Ui.Id("settings-twitch-connect")));
        Ui.Expand(Field("settings-twitch-details"));
        Ui.SetValue(Field("settings-twitch-client-id"), "");
        Ui.Invoke(Field("settings-twitch-connect"));
        Wait.For(() => Ui.NameOf(Field("twitch-feedback")).Contains("public Twitch client ID"), "client ID explanation");
        Ui.SetValue(Field("settings-twitch-client-id"), "FlameAqua-AquaHub");
        Ui.Invoke(Field("settings-twitch-connect"));
        Wait.For(() => Ui.NameOf(Field("twitch-feedback")).Contains("app name isn't the Client ID"), "app name explanation");
        Ui.SetValue(Field("settings-twitch-client-id"), "fixtureClient");
        ExpectJournal("twitch-connect", () => Ui.Invoke(Field("settings-twitch-connect")));
        App.Settings.WaitForString("social.streams.twitchClientId", "fixtureClient");
        Assert.Contains("intercepted", Ui.NameOf(Field("twitch-feedback")));
        Shot("streams-settings");
    });

    [Fact]
    public void T04_LightThemeAtNarrowWidthKeepsStreamActionsVisible()
    {
        var seed = Options;
        Fixture.Restart(new SessionOptions
        {
            Offline = true,
            EditSettings = root =>
            {
                seed.EditSettings?.Invoke(root);
                root["general"] = new JsonObject { ["theme"] = "light" };
            },
            SeedProfile = seed.SeedProfile,
        });
        Run(() =>
        {
            ((WindowPattern)Main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            ((TransformPattern)Main.GetCurrentPattern(TransformPattern.Pattern)).Resize(1000, 760);
            GoTo("social");
            Wait.For(() => Ui.FindAll(Field("streams-list"), Ui.Id("stream-open")).Count == 1, "light theme stream card");
            Assert.False(Ui.IsOffscreen(Field("stream-open")));
            Assert.False(Ui.IsOffscreen(Field("streams-refresh")));
            Shot("streams-light-narrow");
        });
    }

    [Fact]
    public void T05_GoogleSignInIsInterceptedAndLiveChannelSelectionIsExplicit()
    {
        var seed = Options;
        Fixture.Restart(new SessionOptions
        {
            Offline = true,
            EditSettings = root => { seed.EditSettings?.Invoke(root); root["general"] = new JsonObject { ["theme"] = "light" }; },
            SeedProfile = seed.SeedProfile,
        });
        Run(() =>
        {
            ((WindowPattern)Main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            ((TransformPattern)Main.GetCurrentPattern(TransformPattern.Pattern)).Resize(1000, 760);
            GoTo("social"); Ui.Invoke(Field("streams-sources")); PageRoot("settings");
            Ui.Expand(Field("settings-google-details"));
            ExpectJournal("google-connect", () => Ui.Invoke(Field("settings-google-connect")));
            Assert.Contains("intercepted", Ui.NameOf(Field("google-feedback")));
            Ui.Expand(Field("settings-livechannels-details"));
            Ui.SetValue(Field("settings-youtube-filter"), "Fixture");
            ShowChoice(Field("settings-youtube-live-choice"));
            Shot("google-channel-chooser-light");
            var choice = Ui.WaitFind(Field("settings-youtube-live-choices"), Ui.Id("settings-youtube-live-choice"), "saved live channel choice");
            Ui.SetToggle(choice, false);
            Wait.For(() => Ui.NameOf(Field("settings-youtube-selection-status")).Contains("0 of 6"), "empty live selection saved");
            GoTo("social");
            Wait.For(() => Ui.FindAll(Field("streams-list"), Ui.Id("stream-open")).Count == 0, "deselected channel removed");
            Assert.Contains("Choose YouTube live channels", Ui.NameOf(Field("streams-status")));
        });
    }

    [Fact]
    public void T06_ChooserRefusesSeventhChannelAndAllowsReplacement()
    {
        Fixture.Restart(new SessionOptions
        {
            Offline = true,
            EditSettings = root => root["social"] = new JsonObject
            {
                ["streams"] = new JsonObject { ["youTube"] = true },
                ["youTubeChannels"] = new JsonArray(Enumerable.Range(0, 7).Select(i => (JsonNode)new JsonObject
                { ["name"] = "Channel " + (char)('A' + i), ["channelId"] = "UC" + new string((char)('a' + i), 22) }).ToArray()),
            },
        });
        Run(() =>
        {
            GoTo("social"); Ui.Invoke(Field("streams-sources")); PageRoot("settings");
            Ui.Expand(Field("settings-livechannels-details"));
            Ui.SetValue(Field("settings-youtube-filter"), "Channel G");
            var seventh = Field("settings-youtube-live-choice");
            Ui.Toggle(seventh);
            Wait.For(() => Ui.ToggleStateOf(seventh) == ToggleState.Off && Ui.NameOf(Field("youtube-selection-feedback")).Contains("Six channels"), "seventh choice rejected with explanation");
            Ui.SetValue(Field("settings-youtube-filter"), "Channel A");
            Ui.SetToggle(Field("settings-youtube-live-choice"), false);
            Ui.SetValue(Field("settings-youtube-filter"), "Channel G");
            Ui.SetToggle(Field("settings-youtube-live-choice"), true);
            Wait.For(() => Ui.NameOf(Field("settings-youtube-selection-status")).Contains("6 of 6"), "replacement selection saved");
            ShowChoice(Field("settings-youtube-live-choice"));
            Shot("google-channel-chooser-dark");
        });
    }
    private static void ShowChoice(AutomationElement target)
    {
        ScrollPattern? outer = null;
        for (var parent = Ui.Parent(target); parent is not null; parent = Ui.Parent(parent))
            if (parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable) outer = (ScrollPattern)pattern;
        outer?.SetScrollPercent(ScrollPattern.NoScroll, 15);
        target.SetFocus();
    }
}
