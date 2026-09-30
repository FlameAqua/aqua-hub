using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.UI.Shell;

namespace AquaHub.Services;

/// <summary>
/// Headless visual QA: `AquaHub.exe --snapshot &lt;dir&gt; [--data-dir &lt;profile&gt;] [--wait &lt;seconds&gt;]`
/// runs the agents against live data, renders every page off-screen and writes PNGs. Nothing is shown on screen.
/// It seeds demo chats, skills and memories, so run it against a scratch profile (--data-dir): there, each run
/// starts with no chats, so the captures never show an earlier run's demo chats twice.
/// </summary>
public static class SnapshotRunner
{
    public static async Task RunAsync(string outDir, int waitSeconds, string? only, bool scratchProfile = false)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            if (scratchProfile) Hub.Core.Chats.DeleteAll();
            // QA mode renders AI content even if a game is running (normal mode pauses AI then).
            Hub.Core.Llm.PauseReason = () => null;
            Hub.Core.Start();
            await Hub.Media.InitAsync();
            Hub.System.DetailedProcesses = true;
            Hub.System.Start(TimeSpan.FromSeconds(1));
            await Task.Run(() => Hub.Core.Agents.WaitForFirstPassAsync(TimeSpan.FromSeconds(waitSeconds)));
            // Make sure the hero brief reflects the AI digests (it may have run before the model was ready).
            if (Hub.State.Ai?.Available == true && Hub.State.Brief is not { IsAi: true })
            {
                Core.Agents.BriefingAgent.ManualFlag.Request();
                Hub.Core.Agents.RunNow("briefing");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(Math.Min(180, waitSeconds)) && Hub.State.Brief is not { IsAi: true })
                    await Task.Delay(1000);
            }
            await Task.Delay(1500);

            var pages = (only ?? "today,news,social,markets,upcoming,system,launchpad,ask,agents,settings").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var win = new MainWindow
            {
                Width = 1440, Height = 940, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
            };
            win.Show();
            await Settle(1200);
            var n = 1;
            foreach (var page in pages)
            {
                win.Navigate(page);
                await Settle(page is "news" or "social" or "today" ? 3500 : 1500);
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-{page}.png"));
                if (page is "today" or "markets" or "system" or "launchpad" or "upcoming" or "settings" or "agents")
                    CaptureFullPage(win, Path.Combine(outDir, $"{n - 1:00}-{page}-full.png"));
            }

            // Ask with the local model (AQUAHUB_SNAPSHOT_ASK=1): a story question, a research report, and the composer
            // with an attachment and a pending approval.
            if (Environment.GetEnvironmentVariable("AQUAHUB_SNAPSHOT_ASK") == "1" && Hub.State.Ai?.Available == true)
                n = await AskDemoAsync(win, outDir, n);

            // Chat history, editing a question, the context meter and the Workbench (demo data in this scratch profile).
            n = await ChatsAndWorkbenchAsync(win, outDir, n);

            // Onboarding (first-run) overlay
            win.ShowOverlay(new Onboarding());
            await Settle(900);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-onboarding.png"));
            win.HideOverlay();

            // Settings deep links and a social platform that isn't set up.
            foreach (var (page, arg, name) in new[] { ("settings", "ask", "settings-ask"), ("settings", "ask:voice", "settings-ask-voice"), ("settings", "social:youtube", "settings-youtube"), ("settings", "about:updates", "settings-about-updates"), ("social", "bluesky", "social-bluesky") })
            {
                win.Navigate(page, arg);
                await Settle(1500);
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-{name}.png"));
            }
            win.Navigate("social", "all");

            // This PC after a health check (AQUAHUB_SNAPSHOT_HEALTH=1: it reads this machine's own state — nothing is changed).
            if (Environment.GetEnvironmentVariable("AQUAHUB_SNAPSHOT_HEALTH") == "1")
            {
                win.Navigate("system", "health");
                await Settle(1500);
                var checking = System.Diagnostics.Stopwatch.StartNew();
                while (Hub.Health.Running && checking.Elapsed < TimeSpan.FromSeconds(150)) await Task.Delay(500);
                await Settle(1200);
                CaptureFullPage(win, Path.Combine(outDir, $"{n++:00}-system-health-full.png"));
                // An app's details (Aqua's own: it can't be ended from here, so nothing can go wrong).
                if (win.PageHost.Content is UI.Pages.SystemPage systemPage)
                {
                    systemPage.SelectForSnapshot(System.Diagnostics.Process.GetCurrentProcess().ProcessName);
                    await Settle(1500);
                    CaptureFullPage(win, Path.Combine(outDir, $"{n++:00}-system-process-full.png"));
                }
            }

            // Wide screens: page headers, filters and cards must line up.
            win.Width = 2400; win.Height = 1100;
            foreach (var page in new[] { "news", "social", "today" })
            {
                win.Navigate(page);
                await Settle(1800);
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-{page}-wide.png"));
            }

            // Narrow layout
            win.Width = 1000; win.Height = 900;
            win.Navigate("today");
            await Settle(1500);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-today-narrow.png"));
            // A small window: the title bar's command box shrinks, the Ask header wraps.
            win.Width = 760; win.Height = 700;
            Hub.Ask.NewChat();
            win.Navigate("ask");
            await Settle(1200);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-small.png"));
            win.Width = 1000; win.Height = 900;

            // Light theme
            var s = Hub.Core.Settings.Clone();
            s.General.Theme = "light";
            Hub.Theme.Apply(s.General);
            win.Width = 1440; win.Height = 940;
            win.Navigate("today");
            await Settle(1500);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-today-light.png"));
            win.Navigate("markets");
            await Settle(1200);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-markets-light.png"));
            s.General.Theme = "dark";
            Hub.Theme.Apply(s.General);

            // Flyout + palette
            var fly = new FlyoutWindow { Left = -32000, Top = -32000, ShowActivated = false };
            fly.Show();
            await Settle(1500);
            Capture((FrameworkElement)fly.Content, Path.Combine(outDir, $"{n++:00}-flyout.png"));
            fly.Close();

            var palette = new CommandPalette { Left = -32000, Top = -32000, ShowActivated = false };
            palette.Show();
            palette.SetQuery("sp");
            await Settle(800);
            Capture((FrameworkElement)palette.Content, Path.Combine(outDir, $"{n++:00}-palette.png"));
            palette.Close();

            win.Close();
            Log.Info("snapshot", $"Wrote {n - 1} snapshots to {outDir}");
            File.WriteAllText(Path.Combine(outDir, "agents.txt"),
                string.Join(Environment.NewLine, Hub.Core.Agents.Statuses.Select(a => $"{a.Name,-18} {a.State,-8} {a.LastMessage}")));
        }
        catch (Exception ex)
        {
            Log.Error("snapshot", "Snapshot failed", ex);
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
        }
        finally
        {
            await Task.Delay(300);
            Application.Current.Shutdown();
        }
    }

    private static async Task<int> ChatsAndWorkbenchAsync(MainWindow win, string outDir, int n)
    {
        var now = DateTimeOffset.Now;
        SavedChatMessage Q(string text) => new(true, text, null, null, true) { Options = new AskOptions { Web = true } };
        SavedChatMessage A(string text, params Citation[] cites) => new(false, text, cites.ToList(),
            "qwen3.5:9b · 6.1s · " + Plural.Of(cites.Length, "source") + " cited" + (cites.Count(c => c.Kind == "result") is > 0 and var k ? $" ({k} only from search snippets)" : "") + " · used the web · on-device", true,
            new List<string> { "Plan: search “Re:Zero leaker Ice twitter”; look on x.com", "Searched the web for “Re:Zero leaker Ice twitter” · 8 results" }, null, null, "Web", "q");
        var chats = new (string Title, int DaysAgo, bool Starred, SavedChatMessage[] Messages)[]
        {
            ("Re:Zero leaker Ice on X", 0, false, new[]
            {
                Q("Find the Re:Zero leaker Ice on Twitter and link me"),
                A("Ice posts Re:Zero news and leaks as **@rezero_ice** on X: https://x.com/rezero_ice [1]. The account covers Season 4 episode news, Light Novel releases and Q&As [1][2].",
                  new Citation(1, "Re: Zero News by Ice (@rezero_ice) / X", "x.com", "https://x.com/rezero_ice", "web"),
                  new Citation(2, "Re:Zero leaks roundup", "reddit.com", "https://www.reddit.com/r/Re_Zero/", "result")),
            }),
            ("My blue flame logo", 0, true, new[]
            {
                Q("Find me my logo photo of a blue flame"),
                A("It's **my_logo5.png** in your Pictures folder — `C:\\Users\\You\\Pictures\\my_logo5.png` — a blue flame on a dark background.",
                  new Citation(1, "my_logo5.png", "Your files", @"C:\Users\You\Pictures\my_logo5.png", "file")),
            }),
            ("Boiler service due date", 1, false, new[] { Q("When is my boiler service due?"), A("On 14 October, according to your notes [1].", new Citation(1, "boiler notes.txt", "Your files", null, "file")) }),
            ("Starship orbital flight", 5, false, new[] { Q("Tell me more about: SpaceX Starship reaches orbit"), A("Starship reached orbit despite losing one engine [1].", new Citation(1, "Starship reaches orbit", "Ars Technica", "https://arstechnica.com/", "news")) }),
            ("Irish budget 2027 highlights", 12, false, new[] { Q("What were the budget highlights?"), A("Income tax bands widened and energy credits returned [1].", new Citation(1, "Budget 2027", "RTÉ", "https://www.rte.ie/", "news")) }),
        };
        // The demo chats from an earlier run are replaced, not repeated.
        foreach (var old in Hub.Core.Chats.List().Where(c => chats.Any(d => d.Title == c.Title)))
            Hub.Core.Chats.Delete(old.Id);
        foreach (var (title, days, starred, messages) in chats)
        {
            var when = now.AddDays(-days).AddMinutes(-Array.IndexOf(chats.Select(c => c.Title).ToArray(), title) * 7);
            Hub.Core.Chats.Save(new ChatSummary { Id = ChatStore.NewId(when) + title.Length, Title = title, Created = when, Updated = when, Starred = starred, Named = true, ContextUsed = 5400, ContextWindow = 16384 }, messages);
        }
        var ask = Hub.Ask;
        ask.RefreshChats();
        var first = Hub.Core.Chats.List().First(c => c.Title.StartsWith("Re:Zero", StringComparison.Ordinal));
        win.Navigate("ask", "@chat:" + first.Id);
        await Settle(1500);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-history.png"));

        // Saying "remember …" (with Undo / Answer it instead), and an action that needs an OK every time.
        var added = new List<UI.ViewModels.ChatMessageVM>
        {
            new() { IsUser = true, IsChat = true, IsDone = true, Text = "Remember that my logos are in Pictures\\Brand" },
            new()
            {
                IsChat = true, IsDone = true, Text = "Got it — I'll remember that: “My logos are in Pictures\\Brand”.\n\nYou can see and change what I remember in Workbench › Memory.",
                Footer = "Saved to memory · on-device", HasMemoryActions = true,
                UndoMemoryCommand = new UI.ViewModels.RelayCommand(() => { }), AnswerInsteadCommand = new UI.ViewModels.RelayCommand(() => { }),
            },
            new() { IsUser = true, IsChat = true, IsDone = true, Text = "Reply to Sam in Outlook: the numbers are attached, see you Thursday" },
            new()
            {
                IsChat = true,
                Approval = new UI.ViewModels.ApprovalVM
                {
                    Title = "Type “The numbers are attached — see you Thursday.” into Outlook and press Enter",
                    Detail = "This answer has read text that isn't yours (web pages, feeds, files or app windows). Make sure this is what you asked for — nothing Aqua reads can tell it what to type or click.",
                    Icon = "edit", CanAllowForChat = false,
                    AllowCommand = new UI.ViewModels.RelayCommand(() => { }), AllowForChatCommand = new UI.ViewModels.RelayCommand(() => { }),
                    DenyCommand = new UI.ViewModels.RelayCommand(() => { }),
                },
            },
        };
        foreach (var m in added) ask.Messages.Add(m);
        await Settle(1200);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-approval.png"));
        foreach (var m in added) ask.Messages.Remove(m);
        await Settle(300);

        // A question being edited in place, and the context popup.
        var question = ask.Messages.FirstOrDefault(m => m.IsUser);
        if (question?.EditCommand is not null)
        {
            question.EditCommand.Execute(null);
            await Settle(700);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-edit.png"));
            question.CancelEditCommand?.Execute(null);
        }
        if (win.Content is FrameworkElement root && FindChild<UI.Pages.AskPage>(root) is { } page && page.FindName("ContextPopup") is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.IsOpen = true;
            await Settle(600);
            if (popup.Child is FrameworkElement child) Capture(child, Path.Combine(outDir, $"{n++:00}-ask-context.png"));
            popup.IsOpen = false;
        }

        // The Workbench: a taught skill, the editor, memory and abilities.
        var skill = Hub.Core.Workbench.SaveSkill(new AskSkill
        {
            Name = "Focus time", Description = "When I want to concentrate or say “focus time”",
            Triggers = new() { "focus time", "help me concentrate", "time to work" },
            Instructions = "Pause the music and turn on do not disturb, open Notepad for notes, then tell me it's done in one line.",
            Steps = new()
            {
                new SkillStep { Tool = "media_control", Args = new() { ["action"] = "pause" }, Note = "quiet first" },
                new SkillStep { Tool = "do_not_disturb", Args = new() { ["on"] = "true" } },
                new SkillStep { Tool = "launch_app", Args = new() { ["name"] = "Notepad" } },
            },
            TaughtAs = "Focus time: pause the music, turn on do not disturb and open Notepad",
        });
        Hub.Core.Workbench.SaveSkill(new AskSkill
        {
            Name = "Game price check", Description = "When I ask what a game costs",
            Triggers = new() { "how much is", "what does it cost on Steam" },
            Instructions = "Search the web for the game's current price on Steam and one price-comparison site; give the cheapest price with its link first.",
        });
        Hub.Core.Workbench.Remember("My logos and brand images are in Pictures\\Brand", "workbench");
        Hub.Core.Workbench.Remember("I prefer answers in bullet points when comparing things", "chat");
        win.Navigate("workbench");
        await Settle(1200);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-workbench.png"));
        win.Navigate("workbench", "edit:" + skill.Id);
        await Settle(1000);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-workbench-editor.png"));
        win.Navigate("workbench", "memory");
        await Settle(900);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-workbench-memory.png"));
        win.Navigate("workbench", "abilities");
        await Settle(900);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-workbench-abilities.png"));
        ask.NewChat();
        return n;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private static async Task<int> AskDemoAsync(MainWindow win, string outDir, int n)
    {
        async Task WaitForAnswer(int seconds)
        {
            await Task.Delay(500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Hub.Ask.IsBusy && sw.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(500);
            await Settle(1200);
        }

        var ask = Hub.Ask;
        ask.NewChat();
        ask.Web = true;
        var story = Hub.State.Stories.FirstOrDefault(c => c.SourceCount > 2) ?? Hub.State.Stories.FirstOrDefault();
        win.Navigate("ask", story is null ? "What's the biggest story today?" : "@story:" + story.Id);
        await WaitForAnswer(240);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-story.png"));

        ask.NewChat();
        ask.Research = true;
        win.Navigate("ask", "@research:" + (story is null ? "What happened in Irish politics this week?" : "What do we know so far about: " + (story.Summary?.Headline ?? story.Title)));
        await WaitForAnswer(400);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-research.png"));
        ask.Research = false;

        // Composer and approval card (rendering check with fixture data; nothing is opened).
        var fixture = Directory.GetFiles(outDir, "*-today.png").FirstOrDefault();
        if (fixture is not null) await ask.AttachImageAsync(await File.ReadAllBytesAsync(fixture), "Today dashboard.png");
        var demo = new UI.ViewModels.ChatMessageVM { IsUser = false, Question = "Open my boiler notes", IsChat = false };
        demo.Steps.Add(new UI.ViewModels.StepVM { Id = 1, Icon = "folder", Text = "Searched your files for “boiler” · 1 file", State = "ok" });
        demo.Approval = new UI.ViewModels.ApprovalVM
        {
            Title = "Open boiler service notes.txt", Detail = "Aqua will do this on your PC.", Icon = "external",
            AllowCommand = new UI.ViewModels.RelayCommand(() => { }), AllowForChatCommand = new UI.ViewModels.RelayCommand(() => { }),
            DenyCommand = new UI.ViewModels.RelayCommand(() => { }),
        };
        demo.IsThinking = true;
        ask.Messages.Add(demo);
        ask.Computer = true;
        win.Navigate("ask");
        await Settle(1500);
        Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"{n++:00}-ask-composer.png"));
        ask.Messages.Remove(demo);
        ask.Pending.Clear();
        ask.Computer = false;
        return n;
    }

    /// <summary>
    /// Footprint check: `--snapshot &lt;dir&gt; --measure` shows the dashboard off-screen (no captures, which would
    /// allocate large bitmaps) and writes memory.txt: in the tray, on Today, after visiting every page, hidden and
    /// trimmed (as when you close it to the tray), and after five more show/hide cycles — which must not grow.
    /// </summary>
    public static async Task MeasureAsync(string outDir, int waitSeconds)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            Directory.CreateDirectory(outDir);
            Hub.Core.Llm.PauseReason = () => null;
            Hub.Core.Start();
            await Hub.Media.InitAsync();
            Hub.System.Start(TimeSpan.FromSeconds(2.5));
            await Task.Run(() => Hub.Core.Agents.WaitForFirstPassAsync(TimeSpan.FromSeconds(waitSeconds)));
            OsSignals.TrimMemory();
            await Settle(5000);
            report.AppendLine($"tray only (after trim): {MemoryStats.Current()}");

            var win = new MainWindow
            {
                Width = 1440, Height = 940, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
            };
            win.Show();
            win.Navigate("today");
            await Settle(30000);
            report.AppendLine($"dashboard open on Today: {MemoryStats.Current()}");
            var pages = new[] { "news", "social", "markets", "upcoming", "system", "launchpad", "ask", "agents", "settings", "today" };
            foreach (var page in pages)
            {
                win.Navigate(page);
                await Settle(2500);
            }
            await Settle(30000);
            report.AppendLine($"after visiting every page: {MemoryStats.Current()}");

            async Task HideAndTrim()
            {
                win.Hide();
                await Settle(20000);
                OsSignals.TrimMemory();
                await Settle(10000);
            }
            await HideAndTrim();
            report.AppendLine($"closed to the tray (hidden) + trim: {MemoryStats.Current()}");
            for (var cycle = 0; cycle < 5; cycle++)
            {
                win.Show();
                win.Navigate(pages[cycle * 2 % pages.Length]);
                await Settle(3000);
                win.Hide();
                await Settle(1000);
            }
            await HideAndTrim();
            report.AppendLine($"after 5 more open/close cycles + trim: {MemoryStats.Current()}");
            win.Close();
        }
        catch (Exception ex)
        {
            report.AppendLine("failed: " + ex);
        }
        finally
        {
            File.WriteAllText(Path.Combine(outDir, "memory.txt"), report.ToString());
            Application.Current.Shutdown();
        }
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Captures the full (unscrolled) length of the current page's scroll content.</summary>
    public static void CaptureFullPage(MainWindow win, string path)
    {
        if (win.PageHost.Content is not DependencyObject page) return;
        // The page's main scroller is the widest one (Settings, for example, also has a scrolling section list).
        var scroller = FindAll<System.Windows.Controls.ScrollViewer>(page).MaxBy(s => s.ViewportWidth);
        if (scroller?.Content is FrameworkElement content && content.ActualHeight > scroller.ViewportHeight + 40)
        {
            // VisualBrush includes the element's own margin offset, so capture the margin box.
            var m = content.Margin;
            Capture(content, path, 1.0, Math.Min(content.ActualHeight + m.Top + m.Bottom, 6000), content.ActualWidth + m.Left + m.Right);
        }
    }

    /// <summary>Outermost descendants of type T (does not look inside a match).</summary>
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            else foreach (var found in FindAll<T>(child)) yield return found;
        }
    }

    public static void Capture(FrameworkElement element, string path, double scale = 1.25, double? height = null, double? width = null)
    {
        element.UpdateLayout();
        var w = Math.Max(1, width ?? element.ActualWidth);
        var h = Math.Max(1, height ?? element.ActualHeight);
        var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Application.Current.TryFindResource("B.WindowOpaque") as Brush ?? Brushes.Black, null, new Rect(0, 0, w, h));
            // Map the element's own coordinate space 1:1 (no stretching to its content bounds).
            var brush = new VisualBrush(element) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
