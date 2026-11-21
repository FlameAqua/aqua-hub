using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Roleplay;
using AquaHub.Services;

namespace AquaHub.UI.Controls;

public partial class RoleplayPanel : UserControl
{
    private static readonly Personality CustomScene = new(Guid.Empty, "Custom scene", "", "");
    private RoleplaySession Session => Hub.Roleplay;
    private bool _syncing = true, _attached;
    private bool? _compact;
    private int _libraryRevision;
    private Guid _selection;
    private (string Message, bool Busy, bool Error)? _lastNotice;

    public RoleplayPanel()
    {
        InitializeComponent();
        Transcript.ItemsSource = Session.Turns;
        Loaded += (_, _) => Observe();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) Observe(); else Detach(); };
        _syncing = false;
        ModelHint.HelpText = "Roleplay uses your configured model for fiction. A model's training affects what it will write. " +
                             "The scene stays in memory until Aqua closes. Copy the transcript to keep it.";
    }

    private void Observe()
    {
        if (!_attached)
        {
            _attached = true;
            Session.Changed += RefreshScene;
            Hub.LocalAi.Changed += RefreshModel;
            Hub.Core.Llm.HealthChanged += OnHealthChanged;
            Hub.Core.Personalities.Changed += OnLibraryChanged;
            OnLibraryChanged();
        }
        RefreshModel();
        RefreshScene();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _libraryRevision++;
        Session.Changed -= RefreshScene;
        Hub.LocalAi.Changed -= RefreshModel;
        Hub.Core.Llm.HealthChanged -= OnHealthChanged;
        Hub.Core.Personalities.Changed -= OnLibraryChanged;
        _lastNotice = null;
        RoleplayFeedback.Hide();
    }

    private void OnLibraryChanged() => Hub.OnUi(() => { if (_attached) _ = RefreshLibraryAsync(); });

    private async Task RefreshLibraryAsync()
    {
        var revision = ++_libraryRevision;
        try
        {
            var library = await Task.Run(Hub.Core.Personalities.List);
            if (!_attached || revision != _libraryRevision) return;
            _syncing = true;
            PersonalityPicker.ItemsSource = new[] { CustomScene }.Concat(library.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
            var selected = library.FirstOrDefault(p => p.Id == _selection) ?? CustomScene;
            _selection = selected.Id;
            PersonalityPicker.SelectedItem = selected;
            _syncing = false;
            RefreshPersonality();
        }
        catch (Exception)
        {
            if (_attached && revision == _libraryRevision) RoleplayFeedback.Show("The personality library couldn't be opened.", error: true);
        }
    }

    private void RefreshModel()
    {
        CurrentModel.Text = CreativeModels.Description();
        RemoteNotice.Visibility = CreativeModels.IsRemote ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHealthChanged(LlmHealth _) => Hub.OnUi(() => { if (_attached) RefreshModel(); });

    private void RefreshScene()
    {
        _syncing = true;
        if (PersonaBox.Text != Session.Persona) PersonaBox.Text = Session.Persona;
        if (ScenarioBox.Text != Session.Scenario) ScenarioBox.Text = Session.Scenario;
        if (InputBox.Text != Session.Draft) InputBox.Text = Session.Draft;
        _syncing = false;
        RefreshPersonality();
        EmptyState.Visibility = Session.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyTranscript.IsEnabled = Session.Turns.Count > 0;
        ResetWarning.Visibility = Session.Turns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SendButton.Visibility = Session.Busy && Session.Turns.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = SendButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SendButton.IsEnabled = !Session.Busy;
        InputBox.IsEnabled = !Session.Busy;
        PersonaBox.IsReadOnly = ScenarioBox.IsReadOnly = Session.Turns.Count > 0 || Session.Busy;
        var notice = (Session.Notice, Session.Busy, Session.Error);
        if (notice != _lastNotice)
        {
            _lastNotice = notice;
            RoleplayFeedback.Show(Session.Notice, "roleplay-feedback", Session.Error, Session.Busy,
                actionLabel: Session.ModelMissing ? "Choose Creative model" : null,
                action: Session.ModelMissing ? OpenCreativeSettings : null);
        }
        if (Session.Turns.Count > 0)
            Dispatcher.BeginInvoke(TranscriptScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void RefreshPersonality()
    {
        var template = Session.Personality;
        ActivePersonality.Text = template is null ? "" : $"In this scene · {template.Name}";
        ActivePersonality.Visibility = template is not null && !Equals(template, PersonalityPicker.SelectedItem)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth < 820;
        if (_compact == compact) return;
        _compact = compact;
        BodyGrid.ColumnDefinitions.Clear();
        BodyGrid.RowDefinitions.Clear();
        if (compact)
        {
            BodyGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            BodyGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            BodyGrid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        }
        else
        {
            BodyGrid.ColumnDefinitions.Add(new() { Width = new(250) });
            BodyGrid.ColumnDefinitions.Add(new() { Width = new(18) });
            BodyGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            BodyGrid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        }
        Grid.SetRow(ConversationCard, compact ? 1 : 0);
        Grid.SetColumn(ConversationCard, compact ? 0 : 2);
        PanelLayout.Margin = compact ? new(24, 18, 24, 20) : new(28, 26, 28, 24);
        Header.Margin = new(0, 0, 0, compact ? 12 : 18);
        SceneSetup.Margin = compact ? new(0, 0, 0, 10) : new(0);
        SceneSetup.MaxHeight = compact ? 212 : double.PositiveInfinity;
        SceneDetails.IsExpanded = !compact;
    }

    private void OnSceneEdited(object sender, TextChangedEventArgs e)
    {
        if (_syncing || Session.Busy || Session.Turns.Count > 0) return;
        Session.Persona = PersonaBox.Text;
        Session.Scenario = ScenarioBox.Text;
    }

    private void OnDraftEdited(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) Session.Draft = InputBox.Text;
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await Session.SendAsync();
    private void OnCancel(object sender, RoutedEventArgs e) => Session.Cancel();

    private void OnPersonalityPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PersonalityPicker.SelectedItem is not Personality personality) return;
        _selection = personality.Id;
        RefreshPersonality();
    }

    private void OnNewScene(object sender, RoutedEventArgs e)
    {
        Session.Start(PersonalityPicker.SelectedItem is Personality template && template.Id != Guid.Empty ? template : null);
        if (InputBox.IsEnabled && !Hub.SnapshotMode) InputBox.Focus();
    }

    private void OnManage(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("workbench", "personalities");
    private void OnModelSettings(object sender, RoutedEventArgs e) => OpenCreativeSettings();
    private static void OpenCreativeSettings()
    {
        if (Hub.LocalAi.InUse) Hub.Windows.ShowMain("models", "creative");
        else Hub.Windows.ShowMain("settings", "ai:provider");
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Session.Turns.Count == 0) return;
        var transcript = string.Join(Environment.NewLine + Environment.NewLine,
            Session.Turns.Select(t => $"You: {t.UserText}{Environment.NewLine}{t.Persona}: {t.Answer}"));
        if (Sandbox.Intercept("clipboard")) { RoleplayFeedback.Show("Copy skipped in preview mode."); return; }
        try { Clipboard.SetText(transcript); RoleplayFeedback.Show("Transcript copied."); }
        catch (System.Runtime.InteropServices.COMException) { RoleplayFeedback.Show("Couldn't copy the transcript.", error: true); }
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        await Session.SendAsync();
    }
}
