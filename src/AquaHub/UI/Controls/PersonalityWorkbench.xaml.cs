using System.Windows;
using System.Windows.Controls;
using AquaHub.Core.Ai.Roleplay;
using AquaHub.Core.Settings;
using AquaHub.Services;

namespace AquaHub.UI.Controls;

public partial class PersonalityWorkbench : UserControl
{
    private Guid _editing;
    private Personality? _deleting;
    private CancellationTokenSource? _draft;
    private bool _attached, _writing;
    private int _refreshRevision;
    public FeedbackPopup? Feedback { get; set; }
    internal Func<string, CancellationToken, Task<Personality>> DraftPersonality { get; set; } = CreativeModels.DraftAsync;

    public PersonalityWorkbench()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (IsVisible) Attach(); };
        IsVisibleChanged += (_, _) => { if (IsLoaded && IsVisible) Attach(); else Detach(); };
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        Hub.Core.Personalities.Changed += OnChanged;
        Hub.Core.Settings.Changed += OnSettingsChanged;
        RefreshProvider();
        _ = RefreshAsync();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _refreshRevision++;
        Hub.Core.Personalities.Changed -= OnChanged;
        Hub.Core.Settings.Changed -= OnSettingsChanged;
        StopDraft();
        HideDelete();
    }

    private void OnSettingsChanged(HubSettings _) => Hub.OnUi(() => { if (_attached) RefreshProvider(); });
    private void RefreshProvider() => RemoteNotice.Visibility = CreativeModels.IsRemote ? Visibility.Visible : Visibility.Collapsed;
    private void OnChanged() => Hub.OnUi(() => { if (_attached) _ = RefreshAsync(); });
    private Personality? Selected => LibraryList.SelectedItem as Personality;

    private async Task RefreshAsync(Guid? select = null)
    {
        var revision = ++_refreshRevision;
        try
        {
            var entries = await Task.Run(Hub.Core.Personalities.List);
            if (!_attached || revision != _refreshRevision) return;
            var id = select ?? Selected?.Id;
            LibraryList.ItemsSource = entries.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            LibraryList.SelectedItem = entries.FirstOrDefault(p => p.Id == id);
            LibraryList.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyLibrary.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshActions();
        }
        catch (Exception)
        {
            if (_attached && revision == _refreshRevision) Feedback?.Show("The personality library couldn't be opened.", "personalities-feedback", error: true);
        }
    }

    private void RefreshActions() => LibraryActions.Visibility = Selected is not null && _deleting is null ? Visibility.Visible : Visibility.Collapsed;

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_deleting is not null && Selected?.Id != _deleting.Id) HideDelete();
        RefreshActions();
    }

    private void OnNew(object sender, RoutedEventArgs e) => OpenEditor(new(Guid.Empty, "", "", ""));
    private void OnEdit(object sender, RoutedEventArgs e) { if (Selected is { } item) OpenEditor(item); }
    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var name = item.Name.Length <= PersonalityLibrary.MaxName - 5 ? item.Name + " copy" : item.Name;
        OpenEditor(item with { Id = Guid.Empty, Name = name });
        NameBox.SelectAll();
    }

    private void OpenEditor(Personality fields)
    {
        if (_writing) return;
        StopDraft();
        HideDelete();
        _editing = fields.Id;
        EditorTitle.Text = fields.Id == Guid.Empty ? "New personality" : "Edit personality";
        FillEditor(fields);
        Idea.Clear();
        DraftOptions.IsExpanded = false;
        Editor.Visibility = Visibility.Visible;
        Editor.BringIntoView();
        if (!Hub.SnapshotMode) NameBox.Focus();
    }

    private void FillEditor(Personality fields)
    {
        NameBox.Text = fields.Name;
        Instructions.Text = fields.Instructions;
        Scenario.Text = fields.OpeningScenario;
    }

    private async void OnDraft(object sender, RoutedEventArgs e)
    {
        if (_draft is not null) { StopDraft(); return; }
        var idea = Idea.Text.Trim();
        if (idea.Length == 0) { Feedback?.Show("Describe the character first.", "personalities-feedback", error: true); return; }
        using var work = new CancellationTokenSource();
        _draft = work;
        SetDrafting(true);
        Feedback?.Show("Drafting personality…", "personalities-feedback", busy: true);
        try
        {
            var result = await DraftPersonality(idea, work.Token);
            if (work.IsCancellationRequested || !_attached) return;
            FillEditor(result);
            Feedback?.Show("Draft ready to review.", "personalities-feedback");
            if (!Hub.SnapshotMode) NameBox.Focus();
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (work.IsCancellationRequested || !_attached) return;
            if (ex is RoleplayModelUnavailableException)
                Feedback?.Show("The selected Creative model is missing.", "personalities-feedback", error: true,
                    actionLabel: "Choose Creative model", action: OpenModelSettings);
            else Feedback?.Show("The model couldn't draft an editable personality. Try again or fill in the fields.", "personalities-feedback", error: true);
        }
        finally
        {
            _draft = null;
            SetDrafting(false);
        }
    }

    private void SetDrafting(bool busy)
    {
        DraftButton.Content = busy ? "Stop draft" : "Generate draft";
        DraftButton.IsEnabled = true;
        Idea.IsReadOnly = NameBox.IsReadOnly = Instructions.IsReadOnly = Scenario.IsReadOnly = busy;
        SaveButton.IsEnabled = !busy && !_writing;
    }

    private void StopDraft()
    {
        if (_draft is not { IsCancellationRequested: false } draft) return;
        draft.Cancel();
        DraftButton.Content = "Stopping…";
        DraftButton.IsEnabled = false;
        Feedback?.Hide("personalities-feedback");
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_draft is not null || _writing) return;
        var entry = new Personality(_editing, NameBox.Text, Instructions.Text, Scenario.Text);
        await WriteAsync(async () =>
        {
            var saved = await Task.Run(() => Hub.Core.Personalities.Save(entry));
            if (_attached)
            {
                CloseEditor();
                await RefreshAsync(saved.Id);
                Feedback?.Show("Personality saved.", "personalities-feedback");
            }
        });
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || _writing) return;
        _deleting = item;
        DeleteWarning.Text = $"Delete “{item.Name}” from the library? A scene already using it can continue.";
        DeleteReview.Visibility = Visibility.Visible;
        RefreshActions();
    }

    private async void OnConfirmDelete(object sender, RoutedEventArgs e)
    {
        if (_deleting is not { } item || _writing) return;
        await WriteAsync(async () =>
        {
            await Task.Run(() => Hub.Core.Personalities.Delete(item.Id));
            if (!_attached) return;
            if (_editing == item.Id) CloseEditor();
            HideDelete();
            await RefreshAsync();
            Feedback?.Show("Personality deleted.", "personalities-feedback");
        });
    }

    private async Task WriteAsync(Func<Task> action)
    {
        _writing = true;
        SaveButton.IsEnabled = false;
        try { await action(); }
        catch (InvalidOperationException ex) { if (_attached) Feedback?.Show(ex.Message, "personalities-feedback", error: true); }
        catch (Exception) { if (_attached) Feedback?.Show("The library couldn't be updated. Try again.", "personalities-feedback", error: true); }
        finally { _writing = false; SetDrafting(_draft is not null); }
    }

    private void OnKeep(object sender, RoutedEventArgs e) => HideDelete();
    private void HideDelete() { _deleting = null; DeleteReview.Visibility = Visibility.Collapsed; RefreshActions(); }
    private void OnClose(object sender, RoutedEventArgs e) { if (!_writing) CloseEditor(); }
    private void CloseEditor() { StopDraft(); Editor.Visibility = Visibility.Collapsed; _editing = Guid.Empty; }
    private void OnRoleplay(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("ask", "roleplay");
    private void OnModelSettings(object sender, RoutedEventArgs e) => OpenModelSettings();
    private static void OpenModelSettings()
    {
        if (Hub.LocalAi.InUse) Hub.Windows.ShowMain("models", "creative");
        else Hub.Windows.ShowMain("settings", "ai:provider");
    }
}
