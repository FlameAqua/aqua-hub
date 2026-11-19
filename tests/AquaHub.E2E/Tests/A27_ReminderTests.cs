using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Reminders;

namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Reminders")]
public sealed class A27_ReminderTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    protected override SessionOptions Options => new()
    {
        Offline = true,
        EditSettings = root =>
        {
            ConfigureNotifications(root);
            ConfigureTheme(root, "dark");
            ConfigureScenes(root);
        },
        SeedProfile = profile =>
        {
            var path = Path.Combine(profile, "hub.db");
            try
            {
                var database = new HubDatabase(path);
                var now = DateTimeOffset.Now;
                var state = new HubState(database);
                var start = new DateTimeOffset(now.LocalDateTime.Date.AddHours(8));
                state.SetEvents([new() { Id = "fixture-calendar", Title = "Fixture calendar event", Kind = EventKind.Calendar,
                    Start = start, End = start.AddHours(1), Source = "Fixture calendar" }]);
                var service = new ReminderService(database, state, new NullPlatform(), new SeedClock(now.AddHours(-3)));
                service.SaveAsync(new("Fixture due reminder", now.AddMinutes(-1).UtcDateTime, ReminderRepeat.Once)).GetAwaiter().GetResult();
                service.SaveAsync(new("Fixture missed reminder", now.AddMinutes(-90).UtcDateTime, ReminderRepeat.Once)).GetAwaiter().GetResult();
                service.SaveAsync(new("Fixture scene reminder", now.AddMinutes(-1).UtcDateTime, ReminderRepeat.Once, "fixture-scene")).GetAwaiter().GetResult();
                service.SaveAsync(new("Fixture stop scene", now.AddMinutes(-1).UtcDateTime, ReminderRepeat.Once, "fixture-stop")).GetAwaiter().GetResult();
                service.StopAsync().GetAwaiter().GetResult();
            }
            finally { HubDatabase.ReleaseFile(path); }
        },
    };

    private static void ConfigureNotifications(JsonObject root) => root["notifications"] = new JsonObject
    {
        ["quietStart"] = "00:00",
        ["quietEnd"] = "00:00",
        ["suppressWhenFullscreen"] = false,
    };

    private static void ConfigureTheme(JsonObject root, string theme)
    {
        if (root["general"] is not JsonObject general) root["general"] = general = new JsonObject();
        general["theme"] = theme;
    }

    private static void ConfigureScenes(JsonObject root)
    {
        root["apps"] = new JsonArray(new JsonObject
        {
            ["id"] = "fixture-app", ["name"] = "Fixture app", ["kind"] = "exe", ["target"] = @"C:\Fixture\App.exe", ["pinned"] = true,
        });
        root["scenes"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "fixture-scene", ["name"] = "Focus fixture",
                ["steps"] = new JsonArray(new JsonObject { ["action"] = "launch", ["target"] = "fixture-app" }),
            },
            new JsonObject
            {
                ["id"] = "fixture-stop", ["name"] = "Stop fixture",
                ["steps"] = new JsonArray(
                    new JsonObject { ["action"] = "launch", ["target"] = "fixture-app" },
                    new JsonObject { ["action"] = "wait", ["value"] = "30" },
                    new JsonObject { ["action"] = "open-url", ["value"] = "https://example.invalid/after-wait" }),
            });
    }

    private AutomationElement Field(string id) => Ui.WaitFind(Main, Ui.Id(id), id);
    private AutomationElement Action(string id, string name) => Ui.WaitFind(Main, Ui.And(Ui.Id(id), Ui.Name(name)), name);
    private void Press(string id) => Ui.Invoke(Field(id));
    private bool Has(string label) => Ui.Texts(PageRoot("upcoming")).Contains(label);

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_DeckMovesStaysAcrossPagesAndReopensFromToday() => Run(() =>
    {
        GoTo("today");
        var grip = Field("deck-move");
        var initial = grip.Current.BoundingRectangle;
        ((TransformPattern)grip.GetCurrentPattern(TransformPattern.Pattern)).Move(initial.X - 150, initial.Y - 90);
        Wait.For(() => Field("deck-move").Current.BoundingRectangle.X < initial.X - 80, "deck moves by UIA");
        GoTo("upcoming");
        Assert.Null(Ui.Find(Main, Ui.Id("deck-add-reminder")));
        Press("deck-move");
        Assert.False(Ui.IsOffscreen(Field("deck-add-reminder")));
        Shot("deck-dark");
        Assert.Null(App.Journal.Find(0, "launch", id => id == "fixture-app"));
        Ui.Invoke(Action("deck-app-1", "Open Fixture app"));
        App.Journal.WaitFor(0, "launch", id => id == "fixture-app");
        Press("deck-move");
        Press("deck-hide");
        App.Settings.WaitForBool("deck.hidden", true);
        GoTo("today");
        Assert.Null(Ui.Find(Main, Ui.Id("deck-add-reminder")));
        Press("today-show-deck");
        Wait.For(() => Field("deck-move").Current.BoundingRectangle.X < initial.X - 80, "deck restores moved position");
        App.Settings.WaitForBool("deck.hidden", false);
        Press("deck-add-reminder");
        PageRoot("upcoming");
        Field("reminder-title");
        Shot("reminder-editor-dark");
        Press("reminder-cancel");
        Wait.For(() => Ui.Find(Main, Ui.Id("reminder-title")) is null, "editor closed");
        GoTo("today");
        Press("today-add-reminder");
        PageRoot("upcoming");
        Field("reminder-title");
        Press("reminder-cancel");
    });

    [Fact]
    public void T02_DayViewShowsCalendarAndRemindersAndSupportsEditingAndSnooze()
    {
        Fixture.Restart(new SessionOptions
        {
            Offline = true,
            EditSettings = root =>
            {
                ConfigureNotifications(root);
                ConfigureTheme(root, "light");
            },
            SeedProfile = Options.SeedProfile,
        });
        Run(() =>
        {
            ((WindowPattern)Main.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            ((TransformPattern)Main.GetCurrentPattern(TransformPattern.Pattern)).Resize(900, 700);
            GoTo("upcoming");
            Wait.For(() => Has("Fixture calendar event") && Has("Fixture due reminder") && Has("Fixture missed reminder"), "mixed day agenda");
            Assert.Null(Ui.Find(Main, Ui.Name("Edit Fixture calendar event")));
            App.Journal.WaitFor(0, "toast", title => title == "Fixture due reminder");
            App.Journal.WaitFor(0, "toast-sound", title => title == "Fixture due reminder");
            Assert.Null(App.Journal.Find(0, "toast", title => title == "Fixture missed reminder"));
            Assert.Null(App.Journal.Find(0, "toast-sound", title => title == "Fixture missed reminder"));
            Press("agenda-next");
            Wait.For(() => !Has("Fixture calendar event"), "next day excludes calendar event");
            Press("agenda-today");
            Wait.For(() => Has("Fixture calendar event"), "return to today");
            Press("agenda-add-reminder");
            var titleBounds = Field("reminder-title").Current.BoundingRectangle;
            Press("reminder-save");
            Wait.For(() => Ui.NameOf(Field("reminder-editor-status")).Contains("Enter a title", StringComparison.Ordinal), "title validation");
            Assert.Equal(titleBounds, Field("reminder-title").Current.BoundingRectangle);
            Ui.SetValue(Field("reminder-title"), "Bins fixture");
            var defaultTime = Ui.ValueOf(Field("reminder-time"));
            Ui.SetValue(Field("reminder-time"), "27:01");
            Press("reminder-save");
            Wait.For(() => Ui.NameOf(Field("reminder-editor-status")).Contains("24-hour time", StringComparison.Ordinal), "time validation");
            Assert.Equal("Bins fixture", Ui.ValueOf(Field("reminder-title")));
            Shot("reminder-editor-light-narrow");
            Ui.SetValue(Field("reminder-time"), defaultTime);
            Ui.SelectComboItem(Field("reminder-repeat"), "Weekly", Pid);
            Press("reminder-save");
            Wait.For(() => Has("Bins fixture") && Has("Reminder · Weekly"), "weekly reminder saved");
            Ui.Invoke(Action("reminder-edit", "Edit Bins fixture"));
            Ui.SetValue(Field("reminder-title"), "Recycling fixture");
            Press("reminder-save");
            Wait.For(() => Has("Recycling fixture") && !Has("Bins fixture"), "title edited");
            Press("agenda-all-reminders");
            Ui.Invoke(Action("reminder-snooze", "Snooze Fixture due reminder for 10 minutes"));
            Wait.For(() => Has("Reminder · Once · Snoozed"), "snoozed reminder");
            Ui.Invoke(Action("reminder-done", "Mark Fixture due reminder done"));
            Wait.For(() => !Has("Fixture due reminder"), "done removes the one-off reminder");
            Ui.Invoke(Action("reminder-delete", "Delete Recycling fixture"));
            var confirmation = Wait.For(() => Ui.Find(Main, Ui.And(Ui.Type(ControlType.Window), Ui.Name("Delete reminder")))
                ?? AppWindows.Window(Pid, "Delete reminder"), "delete confirmation");
            Ui.Invoke(Ui.WaitButtonWithText(confirmation, "OK"));
            Wait.For(() => !Has("Recycling fixture"), "reminder deleted");
        });
    }

    [Fact]
    public void T03_QuickReminderAndTodayShortcutUseTheExistingAgenda()
    {
        Fixture.Restart(Options);
        Run(() =>
        {
            GoTo("upcoming");
            Wait.For(() => Has("Fixture calendar event"), "today's calendar");
            Press("agenda-next");
            Wait.For(() => !Has("Fixture calendar event"), "another day");
            Press("deck-move");
            Press("deck-timer");
            Ui.SetRange(Field("deck-timer-duration"), 5);
            Press("deck-timer-command");
            Wait.For(() => Ui.NameOf(Field("deck-status")).StartsWith("Reminder set for", StringComparison.Ordinal), "saved quick reminder");
            Wait.For(() => Ui.NameOf(Field("deck-timer-readout")).StartsWith("4:", StringComparison.Ordinal), "timer counts down");
            Shot("deck-countdown");
            Press("deck-move");
            Press("deck-move");
            Press("deck-today");
            Wait.For(() => Has("Fixture calendar event"), "deck jumped to today's agenda");
            Press("agenda-all-reminders");
            Wait.For(() => Has("Quick reminder · 5 min"), "quick reminder in all reminders");
            Assert.Single(Ui.Texts(PageRoot("upcoming")), title => title == "Quick reminder · 5 min");
            Press("deck-move");
            Press("deck-timer");
            Assert.Equal("Stop timer", Ui.NameOf(Field("deck-timer-command")));
            Press("deck-timer-command");
            Wait.For(() => !Has("Quick reminder · 5 min"), "stopped timer removed from reminders");
        });
    }

    [Fact]
    public void T04_SceneReminderOnlyRunsAfterReviewAndConfirmation()
    {
        Fixture.Restart(Options);
        Run(() =>
        {
            GoTo("upcoming");
            App.Journal.WaitFor(0, "toast", title => title == "Fixture scene reminder");
            Assert.Null(App.Journal.Find(0, "launch", id => id == "fixture-app"));
            AutomationElement Review() => Action("reminder-run-scene", "Review scene Focus fixture for Fixture scene reminder");
            Ui.Invoke(Review());
            Assert.Contains(Ui.Texts(Field("reminder-scene-review-steps")), step => step.Contains(@"C:\Fixture\App.exe", StringComparison.Ordinal));
            Shot("scene-review");
            Press("reminder-scene-cancel");
            Wait.For(() => Ui.Find(Main, Ui.Id("reminder-scene-confirm")) is null, "review dismissed");
            Assert.Null(App.Journal.Find(0, "launch", id => id == "fixture-app"));

            Ui.Invoke(Review());
            Press("reminder-scene-confirm");
            App.Journal.WaitFor(0, "launch", id => id == "fixture-app");
            Wait.For(() => Ui.NameOf(Field("reminder-scene-status")) == "Scene run finished.", "scene run finished");
            Assert.Null(Ui.Find(Main, Ui.Name("Review scene Focus fixture for Fixture scene reminder")));
            Assert.Single(App.Journal.ReadAll(), entry => entry.Action == "launch" && entry.Detail == "fixture-app");
            Action("reminder-done", "Mark Fixture scene reminder done");

            Press("agenda-add-reminder");
            Ui.SetValue(Field("reminder-title"), "Future fixture");
            Ui.SelectComboItem(Field("reminder-scene"), "Focus fixture", Pid);
            Press("reminder-save");
            Wait.For(() => Has("Future fixture") && Has("Reminder · Once · Focus fixture"), "future scene reminder saved from editor");
            Assert.Null(Ui.Find(Main, Ui.Name("Review scene Focus fixture for Future fixture")));
        });
    }

    [Fact]
    public void T05_StoppingAConfirmedScenePreventsItsLaterAction()
    {
        Fixture.Restart(Options);
        Run(() =>
        {
            GoTo("upcoming");
            Ui.Invoke(Action("reminder-run-scene", "Review scene Stop fixture for Fixture stop scene"));
            Press("reminder-scene-confirm");
            App.Journal.WaitFor(0, "launch", id => id == "fixture-app");
            Press("reminder-scene-stop");
            Wait.For(() => Ui.NameOf(Field("reminder-scene-status")) == "Scene run stopped.", "confirmed scene stopped");
            Assert.Null(App.Journal.Find(0, "open-url", url => url == "https://example.invalid/after-wait"));
            Assert.Null(Ui.Find(Main, Ui.Name("Review scene Stop fixture for Fixture stop scene")));
        });
    }

    private sealed class SeedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => utcNow.ToUniversalTime();
    }
}
