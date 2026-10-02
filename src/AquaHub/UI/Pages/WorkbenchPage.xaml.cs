using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Util;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

/// <summary>
/// The Workbench: teach Ask skills (drafted by the local model from a plain description, built only from Ask's own
/// tools, checked, tried and saved by you), keep facts it should remember, and see everything it can do.
/// </summary>
public partial class WorkbenchPage : UserControl, IPage
{
    private static readonly string[] ExampleSkills =
    {
        "Morning check: the weather, my agenda and the three biggest stories, in five bullet points",
        "When I ask what a game costs, search Steam and a price site for it and give me the cheapest link",
        "Search Ars Technica for a topic I name and summarise the newest articles with links",
        "Focus time: pause the music, turn on do not disturb and open Notepad",
    };

    private readonly ObservableCollection<SkillVM> _skills = new();
    private readonly ObservableCollection<MemoryVM> _memories = new();
    private readonly ObservableCollection<StepRowVM> _steps = new();
    private AskSkill? _draft;
    private CancellationTokenSource? _cts;
    private bool _syncing;

    public WorkbenchPage()
    {
        InitializeComponent();
        SkillsList.ItemsSource = _skills;
        MemoryList.ItemsSource = _memories;
        StepsList.ItemsSource = _steps;
        foreach (var example in ExampleSkills)
        {
            var b = new Button { Style = (Style)FindResource("Btn.Standard"), Content = example, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6), FontSize = 12.5 };
            UiProps.SetCornerRadius(b, new CornerRadius(16));
            b.Click += (_, _) => { TeachInput.Text = example; TeachInput.Focus(); TeachInput.CaretIndex = example.Length; };
            Examples.Children.Add(b);
        }
    }

    private static IReadOnlyCollection<ToolInfo> Catalogue => AskAgent.Catalogue(Hub.Ask.Platform);

    public void OnNavigatedTo(string? arg)
    {
        Hub.Core.Workbench.Changed -= OnChanged;
        Hub.Core.Workbench.Changed += OnChanged;
        Refresh();
        BuildAbilities();
        switch (arg)
        {
            case "memory": MemoryTab.IsChecked = true; break;
            case "abilities": AbilitiesTab.IsChecked = true; break;
            case not null when arg.StartsWith("teach:", StringComparison.Ordinal):
                SkillsTab.IsChecked = true;
                TeachInput.Text = arg[6..].Trim();
                if (TeachInput.Text.Length > 0) OnDraft(this, new RoutedEventArgs());
                break;
            case not null when arg.StartsWith("edit:", StringComparison.Ordinal):
                SkillsTab.IsChecked = true;
                if (Hub.Core.Workbench.Skills().FirstOrDefault(k => k.Id == arg[5..]) is { } skill) OpenEditor(skill, Array.Empty<string>(), "Edit skill");
                break;
            default: SkillsTab.IsChecked = true; break;
        }
    }

    public void OnNavigatedFrom()
    {
        Hub.Core.Workbench.Changed -= OnChanged;
        _cts?.Cancel();
    }

    private void OnChanged() => Hub.OnUi(Refresh);

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (SkillsPanel is null) return;
        SkillsPanel.Visibility = SkillsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MemoryPanel.Visibility = MemoryTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AbilitiesPanel.Visibility = AbilitiesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (AbilitiesTab.IsChecked == true) BuildAbilities();
    }

    private void Refresh()
    {
        _syncing = true;
        UseSkills.IsChecked = Hub.S.Ask.UseSkills;
        UseMemory.IsChecked = Hub.S.Ask.UseMemory;
        _syncing = false;

        _skills.Clear();
        foreach (var skill in Hub.Core.Workbench.Skills().OrderByDescending(k => k.Updated))
        {
            var vm = new SkillVM(skill);
            vm.TryCommand = new RelayCommand(() => TryIt(skill));
            vm.EditCommand = new RelayCommand(() => OpenEditor(skill, Array.Empty<string>(), "Edit skill"));
            vm.DeleteCommand = new RelayCommand(() =>
            {
                if (MessageBox.Show(Window.GetWindow(this)!, $"Delete the skill “{skill.Name}”?", "Workbench", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    Hub.Core.Workbench.DeleteSkill(skill.Id);
            });
            _skills.Add(vm);
        }
        NoSkills.Visibility = _skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SkillsHeader.Text = _skills.Count == 0 ? "YOUR SKILLS" : $"YOUR SKILLS ({_skills.Count})";
        SkillsTabText.Text = _skills.Count == 0 ? "Skills" : $"Skills · {_skills.Count}";

        _memories.Clear();
        foreach (var m in Hub.Core.Workbench.Memories().OrderByDescending(m => m.Created))
        {
            var vm = new MemoryVM
            {
                Id = m.Id, Text = m.Text,
                Detail = (m.Source == "chat" ? "You said it in Ask" : "Added here") + " · " + TimeText.AgoPhrase(m.Created),
            };
            vm.EditCommand = new RelayCommand(() => { vm.EditText = vm.Text; vm.IsEditing = true; });
            vm.CancelCommand = new RelayCommand(() => vm.IsEditing = false);
            vm.SaveCommand = new RelayCommand(() => { vm.IsEditing = false; Hub.Core.Workbench.UpdateMemory(vm.Id, vm.EditText); });
            vm.DeleteCommand = new RelayCommand(() => Hub.Core.Workbench.Forget(vm.Id));
            _memories.Add(vm);
        }
        NoMemories.Visibility = _memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoryTabText.Text = _memories.Count == 0 ? "Memory" : $"Memory · {_memories.Count}";
    }

    // ───────────────────────────── Teaching ─────────────────────────────

    private async void OnDraft(object sender, RoutedEventArgs e)
    {
        var request = TeachInput.Text.Trim();
        if (request.Length < 8)
        {
            TeachStatus.Text = "Describe the skill in a sentence or two first.";
            TeachInput.Focus();
            return;
        }
        await DraftAsync(request, current: null, feedback: null, TeachStatus, DraftButton);
    }

    private async void OnImprove(object sender, RoutedEventArgs e)
    {
        var feedback = FeedbackInput.Text.Trim();
        if (feedback.Length < 3) { EditorStatus.Text = "Say what should change first."; FeedbackInput.Focus(); return; }
        var current = FromEditor();
        await DraftAsync(current.TaughtAs.Length > 0 ? current.TaughtAs : current.Description, current, feedback, EditorStatus, ImproveButton);
        FeedbackInput.Text = "";
    }

    private async Task DraftAsync(string request, AskSkill? current, string? feedback, TextBlock status, Button button)
    {
        if (!Hub.Core.Llm.Health.Available)
        {
            status.Text = "The local model is needed to draft skills — start it (Agents › AI), or use “Write it myself”.";
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        button.IsEnabled = false;
        status.Text = current is null ? "Drafting — Aqua is working out the steps…" : "Revising…";
        try
        {
            var (skill, notes) = await Task.Run(() => Hub.Core.SkillDrafter.DraftAsync(request, Catalogue, current, feedback, _cts.Token));
            status.Text = "";
            OpenEditor(skill, notes, current is null ? "New skill — check it, then save" : "Revised — check it, then save");
            if (current is null) TeachInput.Text = "";
        }
        catch (OperationCanceledException) { status.Text = ""; }
        catch (Exception ex)
        {
            Log.Warn("workbench", "Drafting failed", ex);
            status.Text = "Couldn't draft it: " + (ex is LlmUnavailableException ? "the local model isn't available." : ex.Message);
        }
        finally { button.IsEnabled = true; }
    }

    private void OnScratch(object sender, RoutedEventArgs e)
    {
        var text = TeachInput.Text.Trim();
        OpenEditor(new AskSkill { Name = "", Description = text, Instructions = text, TaughtAs = text }, Array.Empty<string>(), "New skill");
    }

    private void OpenEditor(AskSkill skill, IReadOnlyCollection<string> notes, string title)
    {
        _draft = skill;
        EditorTitle.Text = title;
        EditorHint.Text = skill.Uses > 0 ? $"Used {Plural.Of(skill.Uses, "time")}" : "";
        SkillName.Text = skill.Name;
        SkillWhen.Text = skill.Description;
        SkillTriggers.Text = string.Join(Environment.NewLine, skill.Triggers);
        SkillInstructions.Text = skill.Instructions;
        ShowSteps();
        EditorNotes.Text = string.Join(" ", notes);
        EditorNotes.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorStatus.Text = "";
        FeedbackInput.Text = "";
        Editor.Visibility = Visibility.Visible;
        TeachCard.Visibility = Visibility.Collapsed;
        SkillName.Focus();
        Editor.BringIntoView();
    }

    private void ShowSteps()
    {
        _steps.Clear();
        if (_draft is null) return;
        var catalogue = Catalogue;
        for (var i = 0; i < _draft.Steps.Count; i++)
        {
            var step = _draft.Steps[i];
            var index = i;
            _steps.Add(new StepRowVM
            {
                Number = i + 1,
                Text = Friendly(step.Tool) + (step.Args.Count > 0 ? " · " + string.Join(", ", step.Args.Select(a => a.Key + " = " + a.Value)) : "") + (step.Note.Length > 0 ? " — " + step.Note : ""),
                Icon = catalogue.FirstOrDefault(t => t.Name == step.Tool)?.Icon ?? "bolt",
                RemoveCommand = new RelayCommand(() => { _draft.Steps.RemoveAt(index); ShowSteps(); }),
            });
        }
        NoSteps.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The skill as edited (name, when, triggers and instructions from the form; steps from the draft).</summary>
    private AskSkill FromEditor()
    {
        var skill = _draft ?? new AskSkill();
        skill.Name = SkillName.Text.Trim();
        skill.Description = SkillWhen.Text.Trim();
        skill.Triggers = SkillTriggers.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        skill.Instructions = SkillInstructions.Text.Trim();
        return skill;
    }

    private AskSkill? SaveDraft()
    {
        var skill = FromEditor();
        if (skill.Name.Length == 0) { EditorStatus.Text = "Give the skill a name."; SkillName.Focus(); return null; }
        if (skill.Instructions.Length == 0 && skill.Steps.Count == 0) { EditorStatus.Text = "Say what Aqua should do."; SkillInstructions.Focus(); return null; }
        if (skill.Triggers.Count == 0 && skill.Description.Length == 0) { EditorStatus.Text = "Add when to use it, or a few things you might say."; SkillWhen.Focus(); return null; }
        var notes = Workbench.Validate(skill, Catalogue);
        try
        {
            var saved = Hub.Core.Workbench.SaveSkill(skill);
            _draft = null;
            Editor.Visibility = Visibility.Collapsed;
            TeachCard.Visibility = Visibility.Visible;
            TeachStatus.Text = $"Saved “{saved.Name}”." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "") + " Ask uses it when a request fits.";
            return saved;
        }
        catch (InvalidOperationException ex) { EditorStatus.Text = ex.Message; return null; }
    }

    private void OnSaveSkill(object sender, RoutedEventArgs e) => SaveDraft();

    private void OnTryDraft(object sender, RoutedEventArgs e)
    {
        if (SaveDraft() is { } skill) TryIt(skill);
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        _draft = null;
        Editor.Visibility = Visibility.Collapsed;
        TeachCard.Visibility = Visibility.Visible;
        TeachStatus.Text = "";
    }

    private static void TryIt(AskSkill skill)
    {
        var sample = skill.Triggers.FirstOrDefault() ?? skill.Name;
        Hub.Windows.ShowMain("ask", "@skill:" + skill.Id + ":" + sample);
    }

    private void OnUseSkills(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = UseSkills.IsChecked == true;
        Hub.Core.Settings.Update(s => s.Ask.UseSkills = on);
    }

    // ───────────────────────────── Memory ─────────────────────────────

    private void OnRemember(object sender, RoutedEventArgs e)
    {
        var text = MemoryInput.Text.Trim();
        if (text.Length < 3) return;
        try
        {
            Hub.Core.Workbench.Remember(text, "workbench");
            MemoryInput.Text = "";
        }
        catch (ArgumentException ex) { MemoryInput.ToolTip = ex.Message; }
    }

    private void OnMemoryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnRemember(sender, e);
    }

    private void OnUseMemory(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = UseMemory.IsChecked == true;
        Hub.Core.Settings.Update(s => s.Ask.UseMemory = on);
    }

    // ───────────────────────────── Abilities ─────────────────────────────

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["search_hub"] = "Search your feeds", ["get_story"] = "Open a story's full coverage", ["web_search"] = "Search the web",
        ["read_webpage"] = "Open and read a page or link", ["search_files"] = "Find files", ["read_file"] = "Read a document or look at a picture",
        ["list_folder"] = "List a folder", ["take_screenshot"] = "Look at your screen", ["open_item"] = "Open a file, folder or link",
        ["launch_app"] = "Start an app or a Settings page", ["media_control"] = "Control music and volume", ["system_status"] = "Check how this PC is doing",
        ["read_clipboard"] = "Read what you copied", ["run_scene"] = "Run one of your scenes", ["list_windows"] = "See which windows are open",
        ["read_window"] = "Read an app window's controls", ["focus_window"] = "Switch to a window", ["click"] = "Click a button or link in an app",
        ["type_text"] = "Type into an app", ["press_keys"] = "Press keyboard shortcuts in an app", ["close_window"] = "Close a window",
    };

    internal static string Friendly(string tool) => Names.TryGetValue(tool, out var n) ? n : tool;

    private void BuildAbilities()
    {
        var s = Hub.S.Ask;
        var catalogue = Catalogue;
        ToolRowVM Row(ToolInfo t, string badge) => new() { Name = Friendly(t.Name), Id = t.Name, Description = t.Description, Icon = t.Icon, Badge = badge };
        var confirm = s.ConfirmActions ? "asks first" : "";
        var groups = new List<ToolGroupVM>
        {
            new()
            {
                Title = "Your feeds", Icon = "layers", Note = "What your agents collected. Always available.",
                Tools = catalogue.Where(t => t.Access == ToolAccess.Hub).Select(t => Row(t, "")).ToList(),
            },
            new()
            {
                Title = "The web", Icon = "globe",
                Note = s.Web ? "With Web on under the Ask box. Searches go to " + (s.SearchEngine == "brave" ? "Brave Search" : s.SearchEngine == "searxng" ? "your SearXNG" : "DuckDuckGo") + "; pages are read straight from their sites."
                             : "Turned off in Settings › Ask Aqua.",
                Tools = catalogue.Where(t => t.Access == ToolAccess.Web).Select(t => Row(t, s.Web ? "needs Web" : "off")).ToList(),
            },
            new()
            {
                Title = "Your files and screen", Icon = "folder",
                Note = s.Computer ? "With Use my PC on, in " + string.Join(", ", s.Folders.Select(AskPage.FolderLabel)) + ". Passwords, keys and app data are never read."
                                  : "Use my PC is turned off in Settings › Ask Aqua.",
                Tools = catalogue.Where(t => t.Access == ToolAccess.Private).Select(t => Row(t, t.Name is "take_screenshot" && s.ConfirmScreenshots ? "asks first" : s.Computer ? "needs Use my PC" : "off")).ToList(),
            },
            new()
            {
                Title = "Actions on your PC", Icon = "bolt",
                Note = !s.Computer ? "Use my PC is turned off in Settings › Ask Aqua."
                     : "With Use my PC on. " + (s.ConfirmActions ? "Each one shows Allow / Don't allow in the chat first." : "Asking first is off in Settings.") +
                       (s.ControlApps ? " Typing and clicking always ask again after Aqua has read web pages." : " Operating app windows is off in Settings › Ask Aqua."),
                Tools = catalogue.Where(t => t.Access == ToolAccess.Act).Select(t => Row(t, s.Computer ? confirm : "off")).ToList(),
            },
        };
        AbilityGroups.ItemsSource = groups.Where(g => g.Tools.Count > 0).ToList();
    }

    private void OnAskSettings(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "ask");
}
