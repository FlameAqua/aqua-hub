using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AquaHub.Core.Ai;
using AquaHub.Platform;
using AquaHub.Services;

namespace AquaHub.UI.Shell;

public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Icon { get; init; } = "arrow-right";
    public ImageSource? Image { get; init; }
    public string Hint { get; init; } = "";
    public string Keywords { get; init; } = "";
    public required Func<Task> Run { get; init; }
    public bool KeepOpen { get; init; }
    public int Score { get; set; }
}

/// <summary>Spotlight-style palette: fuzzy search over actions/apps/scenes/pages/tickers + natural language + inline Ask.</summary>
public partial class CommandPalette : Window
{
    private CancellationTokenSource? _askCts;
    private string _lastQuestion = "";

    public CommandPalette()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.MakeToolWindow(this);
            var ok = WindowEffects.Apply(this, Backdrop.Acrylic, Hub.Theme.IsDark);
            Fallback.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        };
        Deactivated += (_, _) => { if (!Hub.SnapshotMode) Close(); };
        Closed += (_, _) => _askCts?.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public void ShowNear(Window? owner)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        Native.POINT anchor;
        if (owner is { IsVisible: true })
        {
            var p = owner.PointToScreen(new Point(owner.ActualWidth / 2, owner.ActualHeight / 2));
            anchor = new Native.POINT { X = (int)p.X, Y = (int)p.Y };
        }
        else Native.GetCursorPos(out anchor);
        var (work, scale) = WindowEffects.MonitorAt(anchor);
        var w = (int)(ActualWidth * scale);
        var h = (int)(ActualHeight * scale);
        var x = work.Left + (work.Width - w) / 2;
        var y = work.Top + (int)(work.Height * 0.2);
        WindowEffects.Place(this, x, y, w, h, topmost: true);
        Opacity = 1;
        Activate();
        WindowEffects.ForceForeground(new WindowInteropHelper(this).Handle);
        Input.Text = "";
        Input.Focus();
        Update("");
    }

    public void SetQuery(string q)
    {
        Input.Text = q;
        Update(q);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Update(Input.Text);

    private void Update(string query)
    {
        AnswerPanel.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Visible;
        var items = Build(query.Trim());
        Results.ItemsSource = items;
        if (items.Count > 0) Results.SelectedIndex = 0;
        var ai = Hub.State.Ai;
        Status.Text = ai?.Available == true ? $"{ai.ActiveModel} · on-device" : "AI offline — commands still work";
    }

    private List<PaletteItem> Build(string q)
    {
        var s = Hub.S;
        var all = new List<PaletteItem>();

        PaletteItem Act(string title, string sub, string icon, string action, string keywords, string target = "", string value = "") => new()
        {
            Title = title, Subtitle = sub, Icon = icon, Keywords = keywords,
            Run = async () => await Hub.Actions.ExecuteAsync(new HubCommand(action, target, value)),
        };

        all.Add(Act("Read my brief aloud", "Text-to-speech, on-device", "volume", "read_brief", "brief speak read listen"));
        all.Add(Act("Refresh everything", "Run every collector now", "refresh", "refresh", "sync update reload"));
        all.Add(Act(s.Notifications.DoNotDisturb ? "Turn notifications back on" : "Do not disturb", "Hold alerts and toasts", "moon", s.Notifications.DoNotDisturb ? "dnd_off" : "dnd_on", "dnd quiet focus silence"));
        all.Add(Act(Hub.Core.Llm.UserPaused ? "Resume AI" : "Pause AI & free VRAM", "Unloads the local model", "cube", Hub.Core.Llm.UserPaused ? "resume_ai" : "pause_ai", "gpu vram game model ollama"));
        all.Add(Act("Play / pause", "Controls whatever is playing", "play", "media_toggle", "music media pause resume"));
        all.Add(Act("Next track", "", "next", "media_next", "skip song music"));
        all.Add(Act("Mute / unmute", "", "mute", "mute_toggle", "sound audio volume"));
        all.Add(Act("Summarise clipboard", "TL;DR of the text you copied", "copy", "summarize_clipboard", "tldr clipboard summary"));
        all.Add(new PaletteItem
        {
            Title = "Ask about my screen", Subtitle = "Takes a screenshot (Aqua hidden) and asks what you're looking at", Icon = "screenshot", Hint = "Ask",
            Keywords = "screenshot screen capture explain what see vision",
            Run = AskAboutScreenAsync,
        });
        all.Add(new PaletteItem
        {
            Title = "Ask about a file…", Subtitle = "Attach a document, spreadsheet, PDF or image to a question", Icon = "paperclip", Hint = "Ask",
            Keywords = "attach file document pdf image upload read", Run = () => { Hub.Windows.ShowMain("ask", "@attach"); return Task.CompletedTask; },
        });
        all.Add(new PaletteItem
        {
            Title = "Research a topic", Subtitle = "Several web searches, the best pages read, a cited report", Icon = "research", Hint = "Ask",
            Keywords = "research web investigate report deep sources", Run = () => { Hub.Windows.ShowMain("ask", "@research:"); return Task.CompletedTask; },
        });
        if (s.Ask.Computer)
            all.Add(new PaletteItem
            {
                Title = "Find a file on my PC", Subtitle = "Ask searches the folders you allowed", Icon = "folder", Hint = "Ask",
                Keywords = "find file search documents downloads desktop pictures pc computer photo", Run = () => { Hub.Windows.ShowMain("ask", "@pc:"); return Task.CompletedTask; },
            });
        all.Add(new PaletteItem
        {
            Title = "New chat", Subtitle = "Start a fresh Ask Aqua chat", Icon = "plus", Hint = "Ask",
            Keywords = "chat new conversation clear ask", Run = () => Show("@new"),
        });
        all.Add(new PaletteItem
        {
            Title = "Chat history", Subtitle = "Your earlier Ask Aqua chats — search, star, rename, delete", Icon = "message", Hint = "Ask",
            Keywords = "chats history conversations previous earlier starred", Run = () => Show("@history"),
        });
        if (Hub.Ask.Messages.Count > 0)
            all.Add(new PaletteItem
            {
                Title = "Copy this chat", Subtitle = "The whole Ask chat as Markdown — questions, answers, plans, searches, reasoning and sources", Icon = "copy", Hint = "Ask",
                Keywords = "copy chat export markdown share conversation transcript", Run = () => { Hub.Ask.CopyChat(); return Task.CompletedTask; },
            });
        if (s.Ask.Voice != "off")
            all.Add(new PaletteItem
            {
                Title = "Speak a question", Subtitle = "Dictate into Ask with your microphone", Icon = "mic", Hint = "Ask",
                Keywords = "voice speak dictate microphone mic talk", Run = () => Show("@voice"),
            });
        all.Add(new PaletteItem
        {
            Title = "Teach Aqua a skill…", Subtitle = "Describe what it should learn; it drafts the skill for you to check", Icon = "wand", Hint = "Workbench",
            Keywords = "teach skill train workbench learn routine automate tool", Run = () => { Hub.Windows.ShowMain("workbench"); return Task.CompletedTask; },
        });
        all.Add(new PaletteItem
        {
            Title = "What Aqua remembers", Subtitle = "Facts and preferences it keeps in mind", Icon = "brain", Hint = "Workbench",
            Keywords = "memory remember memories facts preferences forget", Run = () => { Hub.Windows.ShowMain("workbench", "memory"); return Task.CompletedTask; },
        });

        foreach (var scene in s.Scenes)
            all.Add(new PaletteItem
            {
                Title = scene.Name, Subtitle = "Scene · " + scene.Description, Icon = string.IsNullOrEmpty(scene.Icon) ? "wand" : scene.Icon, Hint = "Scene",
                Keywords = "scene mode " + scene.Id, Run = () => Hub.Actions.RunSceneAsync(scene),
            });
        foreach (var app in s.Apps)
            all.Add(new PaletteItem
            {
                Title = app.Name, Subtitle = "Open app", Icon = "launchpad", Image = Hub.Catalog.IconFor(app, 48), Hint = "App",
                Keywords = "open launch " + string.Join(' ', app.Keywords), Run = () => { Hub.Launcher.Launch(app); return Task.CompletedTask; },
            });
        var pages = new (string Id, string Name, string Icon)[]
        {
            ("today", "Today", "home"), ("news", "News", "news"), ("social", "Social pulse", "social"), ("markets", "Markets", "markets"),
            ("upcoming", "Upcoming & predictions", "upcoming"), ("system", "This PC", "system"), ("launchpad", "Launchpad", "launchpad"),
            ("ask", "Ask Aqua", "ask"), ("workbench", "Workbench", "wand"), ("agents", "Agents", "agents"), ("settings", "Settings", "settings"),
        };
        foreach (var (id, name, icon) in pages)
            all.Add(new PaletteItem { Title = name, Subtitle = "Go to page", Icon = icon, Hint = "Page", Keywords = "go show page " + id, Run = () => { Hub.Windows.ShowMain(id); return Task.CompletedTask; } });
        foreach (var w in s.Markets.Watchlist.Concat(s.Markets.Indices).Concat(s.Markets.Macro))
        {
            var quote = Hub.State.Quotes.GetValueOrDefault(w.Symbol);
            all.Add(new PaletteItem
            {
                Title = $"{w.Name} ({w.Symbol})",
                Subtitle = quote is null ? "Open in Markets" : $"{ViewModels.Fmt.Price(quote.Price, quote.Currency)}  {ViewModels.Fmt.Pct(quote.ChangePercent)}",
                Icon = "markets", Hint = "Ticker", Keywords = "stock quote " + w.Symbol,
                Run = () => { Hub.Windows.ShowMain("markets", w.Symbol); return Task.CompletedTask; },
            });
        }

        if (q.Length == 0)
            return all.Take(4).Concat(all.Where(i => i.Hint == "Scene")).Concat(all.Where(i => i.Hint == "Page").Take(3)).Take(10).ToList();

        // Earlier chats whose title matches.
        if (q.Length >= 3)
            foreach (var chat in Hub.Core.Chats.List().Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                var id = chat.Id;
                all.Add(new PaletteItem
                {
                    Title = chat.Title, Subtitle = "Chat · " + Core.Util.TimeText.AgoPhrase(chat.Updated) + (chat.Starred ? " · starred" : ""), Icon = "message", Hint = "Chat",
                    Keywords = "chat", Run = () => Show("@chat:" + id),
                });
            }

        foreach (var item in all) item.Score = Score(item, q);
        var ranked = all.Where(i => i.Score > 0).OrderByDescending(i => i.Score).Take(7).ToList();

        // "remember that …" / "forget …": straight to Aqua's memory.
        if (Core.Ai.Assistant.Workbench.MemoryCommand(q) is { } memory)
        {
            var (kind, fact) = memory;
            ranked.Insert(0, new PaletteItem
            {
                Title = kind == "remember" ? $"Remember: “{fact}”" : $"Forget: “{fact}”",
                Subtitle = kind == "remember" ? "Aqua keeps it in mind in every answer (Workbench › Memory)" : "Removes it from what Aqua remembers",
                Icon = "brain", Hint = "Memory",
                Run = () =>
                {
                    if (kind == "remember") Hub.Core.Workbench.Remember(fact, "chat");
                    else Hub.Core.Workbench.Forget(fact);
                    Hub.Tray?.Notify("Aqua", kind == "remember" ? "I'll remember that." : "Forgotten.", quiet: true);
                    return Task.CompletedTask;
                },
            });
            return ranked;
        }
        // "teach aqua to …": draft a skill in the Workbench.
        if (System.Text.RegularExpressions.Regex.Match(q, @"^teach\s+(?:aqua\s+)?(?:(?:how\s+)?to\s+)?(?<what>.{6,})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } teach)
        {
            var what = teach.Groups["what"].Value.Trim();
            ranked.Insert(0, new PaletteItem
            {
                Title = $"Teach Aqua: “{what}”", Subtitle = "Drafts a skill in the Workbench for you to check and save", Icon = "wand", Hint = "Workbench",
                Run = () => { Hub.Windows.ShowMain("workbench", "teach:" + what); return Task.CompletedTask; },
            });
            return ranked;
        }

        var ask = new PaletteItem
        {
            Title = $"Ask Aqua: “{q}”", Subtitle = "Answer from your news, markets, agenda and the local model", Icon = "sparkle", Hint = "Tab",
            KeepOpen = true, Run = () => AskInlineAsync(q),
        };
        var command = new PaletteItem
        {
            Title = $"Do it: “{q}”", Subtitle = "Let the command agent work out what you mean", Icon = "bolt", Hint = "AI",
            Run = async () =>
            {
                var cmd = await Hub.Core.Commands.InterpretAsync(q);
                if (cmd.Action == "ask" && cmd.Target.StartsWith('@')) { Hub.Windows.ShowMain("ask", cmd.Target); return; }
                if (cmd.Action == "ask") { await AskInlineAsync(q); return; }
                var result = await Hub.Actions.ExecuteAsync(cmd);
                Hub.Tray?.Notify("Aqua", string.IsNullOrEmpty(result) ? (cmd.Reply.Length > 0 ? cmd.Reply : "Done") : result, quiet: true);
            },
        };
        var fast = Hub.Core.Commands.TryFastPath(q);
        // "research …", "what's on my screen", "find my file …": straight to the Ask page in that mode.
        if (fast is { Action: "ask" } f && f.Target.StartsWith('@'))
        {
            var (label, icon) = f.Target.StartsWith("@research:") ? ("Research", "research") : f.Target.StartsWith("@web:") ? ("Search the web", "globe")
                : f.Target.StartsWith("@pc:") ? ("Search my PC", "folder") : ("Ask about my screen", "screenshot");
            var target = f.Target;
            ranked.Insert(0, new PaletteItem
            {
                Title = target == "@screen" ? label : $"{label}: “{target[(target.IndexOf(':') + 1)..]}”", Subtitle = "Opens Ask", Icon = icon, Hint = "Ask",
                Run = () => target == "@screen" ? AskAboutScreenAsync() : Show(target),
            });
            return ranked;
        }
        var looksLikeQuestion = q.EndsWith('?') || fast?.Action == "ask";
        var bestScore = ranked.FirstOrDefault()?.Score ?? 0;
        if (looksLikeQuestion || bestScore < 60) ranked.Insert(0, ask); else ranked.Add(ask);
        if (!looksLikeQuestion && bestScore < 90) ranked.Insert(Math.Min(1, ranked.Count), command);
        // Questions can also go out to the web as research, or into your files.
        if (looksLikeQuestion || q.Contains(' '))
        {
            var at = ranked.IndexOf(ask) + 1;
            if (s.Ask.Web)
                ranked.Insert(at++, new PaletteItem
                {
                    Title = $"Research: “{q}”", Subtitle = "Searches the web, reads the best pages, writes a cited report", Icon = "research", Hint = "Web",
                    Run = () => Show("@research:" + q),
                });
            if (s.Ask.Computer)
                ranked.Insert(at, new PaletteItem
                {
                    Title = $"Search my PC: “{q}”", Subtitle = "Looks through the folders you allowed", Icon = "folder", Hint = "PC",
                    Run = () => Show("@pc:" + q),
                });
        }
        return ranked;
    }

    private static Task Show(string askArg)
    {
        Hub.Windows.ShowMain("ask", askArg);
        return Task.CompletedTask;
    }

    /// <summary>Captures the screen first (the palette is already hidden), then opens Ask with it attached.</summary>
    private static Task AskAboutScreenAsync() => Hub.Ask.AskAboutScreenAsync();

    private static int Score(PaletteItem item, string q)
    {
        var title = item.Title.ToLowerInvariant();
        var query = q.ToLowerInvariant();
        if (title == query) return 120;
        if (title.StartsWith(query)) return 100;
        if (title.Split(' ', '(', ')', '/').Any(w => w.StartsWith(query))) return 85;
        if (item.Keywords.Split(' ').Any(k => k.Length > 0 && (k.StartsWith(query) || query.StartsWith(k) && k.Length >= 4))) return 70;
        if (title.Contains(query)) return 60;
        var i = 0;
        foreach (var ch in title)
            if (i < query.Length && ch == query[i]) i++;
        return i == query.Length && query.Length >= 2 ? 30 : 0;
    }

    private async void OnInputKey(object sender, KeyEventArgs e)
    {
        var items = Results.ItemsSource as List<PaletteItem> ?? new();
        switch (e.Key)
        {
            case Key.Down:
                Results.SelectedIndex = Math.Min(items.Count - 1, Results.SelectedIndex + 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Tab:
                if (Input.Text.Trim().Length > 0) await AskInlineAsync(Input.Text.Trim());
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await RunSelected();
                break;
        }
    }

    private async void OnResultClick(object sender, MouseButtonEventArgs e) => await RunSelected();

    private async Task RunSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        if (!item.KeepOpen) Hide();
        try { await item.Run(); }
        catch (Exception ex) { Core.Util.Log.Warn("palette", "Action failed", ex); }
        if (!item.KeepOpen) Close();
    }

    private async Task AskInlineAsync(string question)
    {
        _askCts?.Cancel();
        _askCts = new CancellationTokenSource();
        var ct = _askCts.Token;
        _lastQuestion = question;
        Results.Visibility = Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Visible;
        AnswerLabel.Text = "Aqua · thinking…";
        AnswerText.Text = "";
        var ctx = await Task.Run(() => Hub.Core.Ask.BuildContext(question), ct);
        var sb = new StringBuilder();
        try
        {
            await foreach (var piece in Hub.Core.Ask.AskAsync(question, Array.Empty<LlmMessage>(), ctx, ct))
            {
                sb.Append(piece);
                MarkdownLite.Render(AnswerText, sb.ToString(), ctx.Citations);
                AnswerLabel.Text = "Aqua";
            }
            if (ctx.Citations.Count > 0) AnswerLabel.Text = $"Aqua · {ctx.Citations.Count} sources";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AnswerText.Text = ex is LlmUnavailableException ? "The local model isn't available right now — " + ex.Message : "Sorry, something went wrong: " + ex.Message;
        }
    }

    private void OnContinueInAsk(object sender, RoutedEventArgs e)
    {
        Close();
        Hub.Windows.ShowMain("ask", _lastQuestion);
    }
}
