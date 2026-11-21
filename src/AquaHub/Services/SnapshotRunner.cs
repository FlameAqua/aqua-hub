using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Ai.Roleplay;
using AquaHub.Core.Speech;
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
            // Focused Phase 4 QA needs no feeds or inference. Read the library and render the real controls only.
            if (only == "models") { await ModelsAsync(outDir); return; }
            if (only == "studio") { await StudioAsync(outDir); return; }
            if (only == "settings-ui") { await SettingsAsync(outDir); return; }
            if (only == "search-ui") { await SearchAsync(outDir); return; }
            if (only == "creativity-ui") { await CreativityAsync(outDir); return; }
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

            var pages = (only ?? "today,news,social,markets,upcoming,system,launchpad,ask,agents,models,settings").Split(',', StringSplitOptions.RemoveEmptyEntries);
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
                if (page is "today" or "markets" or "system" or "launchpad" or "upcoming" or "settings" or "agents" or "models")
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

    private static async Task StudioAsync(string outDir)
    {
        var win = new MainWindow { Width = 1440, Height = 940, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        win.Show();
        foreach (var theme in new[] { "dark", "light" })
        {
            var settings = Hub.Core.Settings.Clone(); settings.General.Theme = theme; Hub.Theme.Apply(settings.General);
            foreach (var width in new[] { 1440, 820 })
            {
                win.Width = width;
                foreach (var mode in new[] { "images", "roleplay", "chat" })
                {
                    win.Navigate("ask");
                    if (win.PageHost.Content is UI.Pages.AskPage page) page.SelectTab(mode);
                    await Settle(400);
                    Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"studio-{mode}-{theme}-{width}.png"));
                    if (mode == "images") CaptureFullPage(win, Path.Combine(outDir, $"studio-{mode}-{theme}-{width}-full.png"));
                }
            }
        }
        win.Close();
    }

    private static async Task SearchAsync(string outDir)
    {
        Hub.Core.Settings.Update(s =>
        {
            s.Ai.Enabled = s.Ai.ManageOllama = s.Ai.StartOllamaOnDemand = false;
            s.General.ReduceMotion = true;
            s.Ask.Computer = s.Privacy.LoadRemoteImages = false;
        });
        var win = new MainWindow { Width = 1440, Height = 880, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        var search = await Hub.Search.GetFilesAsync();
        bool activated = false;
        win.Activated += (_, _) => activated = true;
        win.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE));
        };
        try
        {
            win.Show();
            win.Navigate("search");
            await Settle(300);
            if (win.IsActive || win.Left + win.ActualWidth >= SystemParameters.VirtualScreenLeft)
                throw new InvalidOperationException("Search render must stay inactive and off-screen.");
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, "empty.png"), scale: 1);
            await CheckSearchDeckAsync(win, outDir);
            var folder = Path.Combine(Path.GetDirectoryName(Hub.Core.Paths.Root)!, "Search samples " + Guid.NewGuid().ToString("N"), "College notes");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Workshop notes for the autumn planning meeting and supplier follow-up.txt"),
                "The ceramics kiln cools overnight before the workshop inspects the glaze.");
            await search.AddAsync(folder, true);
            var page = (UI.Pages.FileSearchPage)win.PageHost.Content;
            page.Query.Text = "ceramics";
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (page.Results.Items.Count != 1 && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Settle(75);
            if (page.Results.Items.Count != 1) throw new InvalidOperationException("Expected one authorized lexical result. " + search.Status);
            foreach (var (theme, width) in new[] { ("dark", 1440), ("light", 820) })
            {
                var settings = Hub.Core.Settings.Clone();
                settings.General.Theme = theme;
                Hub.Theme.Apply(settings.General);
                win.Width = width;
                page.Query.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(page), Environment.TickCount, System.Windows.Input.Key.Down)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                await Settle(140);
                if (!page.Open.IsEnabled || !page.Reveal.IsEnabled || page.Query.ActualWidth < 240 || page.Results.ActualHeight < 120)
                    throw new InvalidOperationException("Search controls or result list did not fit.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"results-{theme}.png"), scale: 1);
            }
            if (!Sandbox.Enabled) throw new InvalidOperationException("Search actions require the isolated E2E journal.");
            page.Open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            page.Reveal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var journal = Path.Combine(Hub.Core.Paths.Root, "e2e-journal.log");
            var target = ((Core.Search.SearchHit)page.Results.SelectedItem).Path;
            var actionTimeout = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await Settle(40);
                var entries = File.ReadAllLines(journal);
                if (entries.Any(e => e.EndsWith("\topen-file\t" + target, StringComparison.Ordinal)) &&
                    entries.Any(e => e.EndsWith("\treveal-file\t" + target, StringComparison.Ordinal))) break;
                if (actionTimeout.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException("Selected file actions did not reach the isolated journal.");
            }
            var nested = Directory.CreateDirectory(Path.Combine(folder, "Spring term")).FullName;
            File.WriteAllText(Path.Combine(nested, "Glaze.txt"), "Ceramics requires heat.");
            var palette = new CommandPalette(win.Navigate);
            try
            {
                palette.SetQuery("Search files");
                if (palette.Results.SelectedItem is not PaletteItem { Title: "Search files" })
                    throw new InvalidOperationException("The palette did not rank Search files first.");
                palette.SetQuery("Search files: ceramics");
                if (palette.Results.SelectedItem is not PaletteItem item || palette.Results.Items.Count != 1)
                    throw new InvalidOperationException("The palette did not offer the direct file search.");
                await item.Run();
            }
            finally { palette.Close(); }
            await WaitForResults(2);
            var activation = AppArgs.Parse(["--search-folder", nested + @"\."]);
            if (activation.SearchFolder != nested) throw new InvalidOperationException("Explorer folder argument did not round-trip.");
            win.Navigate("search", "folder:" + activation.SearchFolder);
            await WaitForResults(1);
            if (page.Results.Items[0] is not Core.Search.SearchHit { Name: "Glaze.txt" } || page.ReviewFolder.IsVisible)
                throw new InvalidOperationException("Explorer scope did not limit results to the selected subfolder.");
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, "folder-scope.png"), scale: 1);
            win.Navigate("search", "folder:" + Path.Combine(Path.GetDirectoryName(folder)!, "Unapproved"));
            await WaitForResults(0);
            if (new ButtonAutomationPeer(page.ReviewFolder).GetPattern(PatternInterface.Invoke) is not IInvokeProvider review)
                throw new InvalidOperationException("Folder review could not be invoked.");
            review.Invoke();
            await Settle(100);
            if (!page.Consent.IsVisible || page.IncludeContent.IsChecked == true || (await search.RootsAsync()).Count != 1)
                throw new InvalidOperationException("Explorer opened an unapproved folder without the existing consent controls.");
            win.Navigate("search", "query:ceramics");
            await WaitForResults(2);

            async Task WaitForResults(int count)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Settle(50);
                    if (page.Results.Items.Count == count && page.Summary.Text != "Searching…" && page.Commands.IsEnabled) return;
                }
                throw new InvalidOperationException($"Expected {count} Search results through the shortcut.");
            }

            var other = Path.Combine(Path.GetDirectoryName(folder)!, "Index load " + Guid.NewGuid().ToString("N"));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(other);
                for (var i=0; i<2000; i++) File.WriteAllText(Path.Combine(other,$"entry-{i:D4}.txt"), "Synthetic file");
            });
            var sampling = Task.Run(async () =>
            {
                var delays = new List<double>();
                for (var i=0; i<120; i++)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    await win.Dispatcher.InvokeAsync(() => delays.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds), DispatcherPriority.Input);
                    await Task.Delay(15).ConfigureAwait(false);
                }
                return delays.Order().ElementAt(113);
            });
            await search.AddAsync(other,false);
            var p95 = await sampling;
            File.WriteAllText(Path.Combine(outDir,"dispatcher.txt"), FormattableString.Invariant($"Release check: 120 input-priority samples during a 2,000-file scan, p95 {p95:F2} ms"));
            foreach (var root in await search.RootsAsync()) await search.RemoveAsync(root.Id);
            await Settle(350);
            if (page.Results.Items.Count != 0) throw new InvalidOperationException("Revoked result still visible.");
            if (activated) throw new InvalidOperationException("Search renderer took foreground focus.");
        }
        finally { win.Close(); await search.StopAsync(); }
    }

    private static async Task CheckSearchDeckAsync(MainWindow win, string outDir)
    {
        Hub.Core.Settings.Update(s =>
        {
            s.Apps.Add(new Core.Settings.AppEntry { Id = "scratch-exe", Name = "Aqua Hub", Target = Environment.ProcessPath! });
            s.Apps.Add(new Core.Settings.AppEntry { Id = "scratch-link", Name = "Reference", Kind = "url", Target = "https://example.invalid" });
        });
        win.Navigate("today");
        win.DeckHandle.Visibility = Visibility.Visible;
        win.DeckHandle.ToggleRequested?.Invoke();
        win.DeckHandle.Countdown = "4:59";
        foreach (var (theme, width) in new[] { ("dark", 1440), ("light", 820) })
        {
            var settings = Hub.Core.Settings.Clone();
            settings.General.Theme = theme;
            Hub.Theme.Apply(settings.General);
            win.Width = width;
            await Settle(100);
            Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"deck-search-{theme}.png"), scale: 1);
            if (win.DeckApp1.Content is not Image)
                throw new InvalidOperationException($"The Deck did not display the executable icon ({win.DeckApp1.Content?.GetType().Name ?? "empty"}).");
            if (win.DeckApp2.Content is not UI.Controls.Icon { Kind: "globe" })
                throw new InvalidOperationException("The Deck did not display the link icon.");
            if (win.DeckHandle.Template.FindName("CountdownClock", win.DeckHandle) is not UI.Controls.Icon { IsVisible: true })
                throw new InvalidOperationException("The Deck did not display the countdown clock.");
        }
        if (new ButtonAutomationPeer(win.DeckSearch).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("The Deck did not expose its file search button.");
        invoke.Invoke();
        await Settle(150);
        if (win.PageHost.Content is not UI.Pages.FileSearchPage || win.DeckActions.IsVisible)
            throw new InvalidOperationException("The Deck did not open Search files and close its actions.");
        win.DeckHandle.Visibility = Visibility.Collapsed;
    }

    private static async Task CreativityAsync(string outDir)
    {
        if (!Sandbox.Enabled) throw new InvalidOperationException("Creativity rendering requires an isolated profile.");
        Hub.Core.Settings.Update(s =>
        {
            s.Ai.Enabled = s.Ai.ManageOllama = s.Ai.StartOllamaOnDemand = false;
            s.Ai.Llama.UseOllamaModels = s.Ai.Llama.UseOllamaEngine = false;
            s.Ask.Computer = s.Privacy.LoadRemoteImages = false;
            s.Ask.ReadAloudBackend = "windows";
            s.General.ReduceMotion = true;
        });
        var character = await Task.Run(() => Hub.Core.Personalities.Save(new Personality(Guid.Empty,
            "The Night Archivist and Keeper of Lost Letters from Across the Northern Coast", "A quiet archivist.", "A letter arrives.")));
        var win = new MainWindow { Width = 1440, Height = 880, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        bool activated = false;
        win.Activated += (_, _) => activated = true;
        try
        {
            win.Show();
            foreach (var (theme, width) in new[] { ("dark", 1440), ("light", 820) })
            {
                var options = Hub.Core.Settings.Clone().General;
                options.Theme = theme;
                Hub.Theme.Apply(options);
                win.Width = width;
                win.Height = theme == "light" ? 580 : 880;
                win.Navigate("settings", "ask:readaloud");
                await LayoutAsync();
                var row = (UI.Controls.SettingRow)((FrameworkElement)win.PageHost.Content).FindName("Row_readaloud");
                var voiceSettings = row.Below as UI.Controls.ReadAloudSettings
                    ?? throw new InvalidOperationException("Read-aloud settings did not open.");
                var preview = (Button)voiceSettings.FindName("PreviewButton");
                if (!preview.IsVisible || !preview.IsEnabled || AutomationProperties.GetAutomationId(preview) != "read-aloud-preview")
                    throw new InvalidOperationException("Read-aloud settings did not expose an accessible preview.");
                ((ComboBox)voiceSettings.BackendPicker).SelectedValue = "kokoro";
                await LayoutAsync();
                var install = (Button)voiceSettings.FindName("Install");
                if (KokoroSetup.Supported && (!install.IsVisible || install.TransformToAncestor(row).TransformBounds(new Rect(install.RenderSize)).Right > row.ActualWidth))
                    throw new InvalidOperationException("The Kokoro install controls did not fit.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"voice-{theme}.png"), scale: 1);
                ((ComboBox)voiceSettings.BackendPicker).SelectedValue = "windows";
                Invoke(preview);
                await UntilAsync(() => Hub.Speech.State.Stage == ReadAloudStage.Playing && win.SpeechStop.IsVisible);
                win.Navigate("workbench", "personalities");
                await LayoutAsync();
                if (!win.SpeechStop.IsVisible) throw new InvalidOperationException("Speech could not be stopped after navigation.");
                Invoke(win.SpeechStop);
                await UntilAsync(() => !Hub.Speech.State.Active && !win.SpeechStop.IsVisible);

                var workbench = (UI.Controls.PersonalityWorkbench)((FrameworkElement)win.PageHost.Content).FindName("PersonalitiesPanel");
                var list = (ListBox)workbench.FindName("LibraryList");
                await UntilAsync(() => list.Items.OfType<Personality>().Any(p => p.Id == character.Id));
                list.SelectedItem = list.Items.OfType<Personality>().Single(p => p.Id == character.Id);
                var edit = FindVisual<Button>(workbench, b => AutomationProperties.GetAutomationId(b) == "personality-edit");
                Invoke(edit ?? throw new InvalidOperationException("The Edit action was missing."));
                await LayoutAsync();
                if (((TextBox)workbench.FindName("NameBox")).Text != character.Name)
                    throw new InvalidOperationException("Workbench did not open the saved personality.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"personalities-{theme}.png"), scale: 1);
                if (theme == "dark")
                {
                    character = character with { OpeningScenario = "A letter and a map arrive." };
                    var proposed = new TaskCompletionSource<Personality>(TaskCreationOptions.RunContinuationsAsynchronously);
                    workbench.DraftPersonality = (_, token) => proposed.Task.WaitAsync(token);
                    ((Expander)workbench.FindName("DraftOptions")).IsExpanded = true;
                    ((TextBox)workbench.FindName("Idea")).Text = "A quiet archivist receives a letter and a map.";
                    Invoke((Button)workbench.FindName("DraftButton"));
                    await UntilAsync(() => !((Button)workbench.FindName("SaveButton")).IsEnabled);
                    proposed.SetResult(character);
                    var scenario = (TextBox)workbench.FindName("Scenario");
                    await UntilAsync(() => scenario.Text == character.OpeningScenario);
                    if ((await Task.Run(Hub.Core.Personalities.List)).Single() == character)
                        throw new InvalidOperationException("A draft was stored before Save.");
                    Invoke((Button)workbench.FindName("SaveButton"));
                    await UntilAsync(() => list.Items.OfType<Personality>().Any(p => p == character));
                }
                win.Navigate("ask", "roleplay");
                await LayoutAsync();
                var roleplay = FindVisual<UI.Controls.RoleplayPanel>(win, p => p.IsVisible)
                    ?? throw new InvalidOperationException("Roleplay did not open.");
                var picker = (ComboBox)roleplay.FindName("PersonalityPicker");
                await UntilAsync(() => picker.Items.OfType<Personality>().Any(p => p.Id == character.Id));
                picker.SelectedItem = picker.Items.OfType<Personality>().Single(p => p.Id == character.Id);
                Invoke(FindVisual<Button>(roleplay, b => AutomationProperties.GetAutomationId(b) == "roleplay-new-scene")!);
                await UntilAsync(() => Hub.Roleplay.Personality?.Id == character.Id);
                if (Hub.Roleplay.Scenario != character.OpeningScenario) throw new InvalidOperationException("Roleplay lost the starting scenario.");
                if (roleplay.FindName("TranscriptScroll") is not ScrollViewer { ActualHeight: >= 80 })
                    throw new InvalidOperationException("The Roleplay transcript needs room at the minimum window size.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"roleplay-{theme}.png"), scale: 1);
            }
            win.Navigate("workbench", "personalities");
            await LayoutAsync();
            var library = FindVisual<ListBox>(win, list => AutomationProperties.GetAutomationId(list) == "personalities-list")!;
            await UntilAsync(() => library.Items.OfType<Personality>().Any(p => p == character));
            library.SelectedItem = library.Items.OfType<Personality>().Single(p => p == character);
            Invoke(FindVisual<Button>(win, b => AutomationProperties.GetAutomationId(b) == "personality-delete")!);
            await LayoutAsync();
            Invoke(FindVisual<Button>(win, b => AutomationProperties.GetAutomationId(b) == "personality-delete-confirm")!);
            await UntilAsync(() => library.Items.Count == 0);
            win.Navigate("ask", "roleplay");
            await LayoutAsync();
            if (Hub.Roleplay.Personality != character || (await Task.Run(Hub.Core.Personalities.List)).Count != 0)
                throw new InvalidOperationException("Deleting a template affected the active scene or failed to persist.");
        }
        finally { win.Close(); }

        static void Invoke(Button button)
        {
            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Button has no accessible Invoke pattern.");
            invoke.Invoke();
        }

        async Task LayoutAsync()
        {
            await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            win.UpdateLayout();
            if (activated || win.IsActive || win.Left > -20000 || win.Top > -20000)
                throw new InvalidOperationException("The render window must stay inactive and off-screen.");
        }

        async Task UntilAsync(Func<bool> ready)
        {
            var limit = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                await LayoutAsync();
                if (ready()) return;
            }
            while (limit.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("The creativity control did not reach its expected state.");
        }
    }

    private static async Task SettingsAsync(string outDir)
    {
        Hub.Core.Settings.Update(s =>
        {
            s.Ai.Enabled = s.Ai.ManageOllama = s.Ai.StartOllamaOnDemand = false;
            s.Ask.Computer = s.Privacy.LoadRemoteImages = false;
        });
        var win = new MainWindow { Width = 1440, Height = 880, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            win.Show();
            await LayoutAsync();
            foreach (var (theme, width) in new[] { ("dark", 1440), ("light", 820) })
            {
                var settings = Hub.Core.Settings.Clone();
                settings.General.Theme = theme;
                Hub.Theme.Apply(settings.General);
                win.Width = width;
                win.Height = theme == "light" ? 700 : 880;
                win.Navigate("settings", "social");
                await LayoutAsync();
                var page = (UI.Pages.SettingsPage)win.PageHost.Content;
                var twitch = Disclosure(page, "twitch");
                if (twitch is not { IsVisible: true, IsExpanded: false }) throw new InvalidOperationException("Twitch setup should start collapsed.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"settings-social-{theme}.png"), scale: 1);

                win.Navigate("settings", "social:twitch");
                await LayoutAsync();
                if (!twitch.IsExpanded || page.FindName("TwitchClientId") is not FrameworkElement { IsVisible: true, ActualWidth: > 120 })
                    throw new InvalidOperationException("The Twitch deep link did not reveal its setup form.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"settings-twitch-{theme}.png"), scale: 1);
                twitch.IsExpanded = false;

                win.Navigate("settings", "social:livechannels");
                await LayoutAsync();
                if (page.FindName("UploadChannelsLink") is not Button { IsVisible: true } link
                    || new ButtonAutomationPeer(link).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                    throw new InvalidOperationException("The link from live channels to uploads was not exposed.");
                invoke.Invoke();
                await LayoutAsync();
                var uploads = Disclosure(page, "youtube");
                if (uploads is not { IsExpanded: true } || page.FindName("NewChannelId") is not FrameworkElement { IsVisible: true })
                    throw new InvalidOperationException("The related settings link did not reveal the upload channel form.");
                uploads.IsExpanded = false;
                if (Disclosure(page, "livechannels") is { } liveChannels) liveChannels.IsExpanded = false;

                win.Navigate("settings", "ask:voice");
                await LayoutAsync();
                if (page.FindName("SoundSettingsButton") is not Button { IsVisible: true } sound
                    || page.FindName("Row_microphone") is not FrameworkElement micRow
                    || sound.TransformToAncestor(micRow).TransformBounds(new Rect(sound.RenderSize)).Right > micRow.ActualWidth - 12)
                    throw new InvalidOperationException("The microphone controls must fit inside their setting row.");
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"settings-voice-{theme}.png"), scale: 1);

                win.Navigate("today");
                win.DeckHandle.Visibility = Visibility.Visible;
                win.DeckHandle.ToggleRequested?.Invoke();
                await LayoutAsync();
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"deck-{theme}.png"), scale: 1);
                win.DeckHandle.ToggleRequested?.Invoke();
                win.DeckHandle.Visibility = Visibility.Collapsed;
            }
        }
        finally { win.Close(); }

        static Expander? Disclosure(UI.Pages.SettingsPage page, string row) => (page.FindName("Row_" + row) as UI.Controls.SettingRow)?.Below as Expander;

        async Task LayoutAsync()
        {
            await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            win.UpdateLayout();
            if (win.IsActive || win.Left > -20000 || win.Top > -20000)
                throw new InvalidOperationException("The render window must stay off-screen and inactive.");
        }
    }

    private static async Task ModelsAsync(string outDir)
    {
        await Hub.LocalAi.RefreshAsync();
        var win = new MainWindow { Width = 1440, Height = 940, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        win.Show();
        foreach (var theme in new[] { "dark", "light" })
        {
            var settings = Hub.Core.Settings.Clone(); settings.General.Theme = theme; Hub.Theme.Apply(settings.General);
            foreach (var width in new[] { 1440, 820 })
            {
                win.Width = width;
                win.Navigate("models"); await Settle(600);
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"models-{theme}-{width}.png"));
                CaptureFullPage(win, Path.Combine(outDir, $"models-{theme}-{width}-full.png"));
                win.Navigate("settings", "ai"); await Settle(600);
                Capture((FrameworkElement)win.Content, Path.Combine(outDir, $"ai-settings-{theme}-{width}.png"));
                if (win.PageHost.Content is UI.Pages.SettingsPage page)
                {
                    var advanced = FindVisual<System.Windows.Controls.Expander>(page, e => (string?)e.Header == "Advanced runtime settings");
                    if (advanced is not null) advanced.IsExpanded = true;
                    await Settle(300);
                    CaptureFullPage(win, Path.Combine(outDir, $"ai-settings-{theme}-{width}-full.png"));
                    if (advanced is not null) advanced.IsExpanded = false;
                }
            }
        }
        win.Close();
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T item && match(item)) return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
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
