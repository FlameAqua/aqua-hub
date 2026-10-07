using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Roleplay;
using AquaHub.Services;

namespace AquaHub.UI.Controls;

/// <summary>A self-contained roleplay session. Its static transcript survives shell tab changes and is separate from Ask.</summary>
public partial class RoleplayPanel : UserControl
{
    private static readonly Session State = new();
    private bool _syncing = true;
    private bool? _compactLayout;

    public RoleplayPanel()
    {
        InitializeComponent();
        Transcript.ItemsSource = State.Turns;
        _syncing = true;
        PersonaBox.Text = State.Persona;
        ScenarioBox.Text = State.Scenario;
        InputBox.Text = State.Draft;
        _syncing = false;
        UpdateView();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        State.Changed -= OnStateChanged;
        State.Changed += OnStateChanged;
        _syncing = true;
        PersonaBox.Text = State.Persona;
        ScenarioBox.Text = State.Scenario;
        InputBox.Text = State.Draft;
        _syncing = false;
        StatusText.Text = State.Status;
        UpdateView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => State.Changed -= OnStateChanged;

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth < 820;
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        if (compact)
        {
            BodyGrid.ColumnDefinitions.Clear();
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            BodyGrid.RowDefinitions.Clear();
            BodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(SceneSetup, 0);
            Grid.SetColumn(SceneSetup, 0);
            Grid.SetColumnSpan(SceneSetup, 1);
            SceneSetup.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(ConversationCard, 1);
            Grid.SetColumn(ConversationCard, 0);
            HeaderActions.SetValue(Grid.RowProperty, 1);
            HeaderActions.HorizontalAlignment = HorizontalAlignment.Left;
            HeaderActions.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            BodyGrid.RowDefinitions.Clear();
            BodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            BodyGrid.ColumnDefinitions.Clear();
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(SceneSetup, 0);
            Grid.SetColumn(SceneSetup, 0);
            Grid.SetColumnSpan(SceneSetup, 1);
            SceneSetup.Margin = new Thickness(0, 0, 0, 0);
            Grid.SetRow(ConversationCard, 0);
            Grid.SetColumn(ConversationCard, 2);
            HeaderActions.SetValue(Grid.RowProperty, 0);
            HeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
            HeaderActions.Margin = new Thickness(0);
        }
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (PersonaBox.Text != State.Persona || ScenarioBox.Text != State.Scenario)
        {
            _syncing = true;
            PersonaBox.Text = State.Persona;
            ScenarioBox.Text = State.Scenario;
            _syncing = false;
        }
        StatusText.Text = State.Status;
        UpdateView();
    }, System.Windows.Threading.DispatcherPriority.DataBind);

    private void OnSceneEdited(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        State.Persona = PersonaBox.Text;
        State.Scenario = ScenarioBox.Text;
        State.NotifyChanged();
    }

    private void OnDraftEdited(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) State.Draft = InputBox.Text;
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || State.Busy) return;
        State.Persona = PersonaBox.Text;
        State.Scenario = ScenarioBox.Text;
        var persona = State.Persona.Trim();
        if (persona.Length == 0) persona = "a thoughtful scene partner";
        var entry = new Turn(text, persona);
        State.Turns.Add(entry);
        State.Draft = "";
        InputBox.Clear();
        var scene = State.SceneId;
        var cts = new CancellationTokenSource();
        State.Cancellation = cts;
        State.Busy = true;
        State.Status = "Starting scene…";
        State.NotifyChanged();
        UpdateView();
        ScrollToEnd();

        try
        {
            var ai = Hub.S.Ai;
            var health = Hub.Core.Llm.Health;
            var creative = ai.Provider.Equals("llamacpp", StringComparison.OrdinalIgnoreCase)
                ? Hub.Llama.Library.Find(ai.Llama.CreativeModel) : null;
            var contextTokens = ai.Provider.Equals("llamacpp", StringComparison.OrdinalIgnoreCase)
                ? creative is not null ? Hub.LocalAi.Fit(creative).ContextTokens : ai.Llama.ContextTokens > 0 ? ai.Llama.ContextTokens : 8192
                : ai.ContextTokens;
            var model = RoleplayPrompt.SelectModel(ai.Provider, creative?.Id ?? ai.Llama.CreativeModel, health.ActiveModel,
                Hub.LocalAi.Models.Select(m => m.Id));
            var request = new LlmRequest
            {
                System = RoleplayPrompt.BuildSystem(State.Persona, State.Scenario),
                Messages = RoleplayPrompt.BuildHistory(State.History, text, maxCharacters: RoleplayPrompt.HistoryBudget(contextTokens)),
                Model = model,
                Temperature = 0.85,
                MaxTokens = 900,
                Priority = LlmPriority.Interactive,
                Purpose = "Roleplay",
                Think = false,
                Tools = Array.Empty<LlmTool>(),
            };
            var answer = new StringBuilder();
            await foreach (var delta in Hub.Core.Llm.ChatStreamAsync(request, reserved: false, cts.Token))
            {
                if (scene != State.SceneId) return;
                if (delta is not LlmTextDelta chunk) continue;
                answer.Append(chunk.Text);
                entry.Answer = answer.ToString();
                State.Status = "Writing…";
                State.NotifyChanged();
                ScrollToEnd();
            }
            if (scene == State.SceneId)
            {
                if (answer.Length == 0) answer.Append("The model returned an empty reply. Try another line.");
                entry.Answer = answer.ToString();
                State.History.Add(new LlmMessage("user", text));
                State.History.Add(new LlmMessage("assistant", answer.ToString()));
                State.Status = "Scene ready for your next line.";
                State.NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            if (scene == State.SceneId)
            {
                if (entry.Answer.Length > 0)
                {
                    State.History.Add(new LlmMessage("user", text));
                    State.History.Add(new LlmMessage("assistant", entry.Answer));
                    State.Status = "Reply stopped. Continue when ready.";
                }
                else
                {
                    State.Turns.Remove(entry);
                    if (InputBox.Text.Length == 0) InputBox.Text = State.Draft = text;
                    State.Status = "Reply stopped before any text arrived. Your line is ready to retry.";
                }
                State.NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            if (scene == State.SceneId)
            {
                State.Turns.Remove(entry);
                if (InputBox.Text.Length == 0) InputBox.Text = State.Draft = text;
                State.Status = ex is LlmUnavailableException or RoleplayModelUnavailableException
                    ? ex.Message
                    : $"Couldn't reach the configured model: {ex.Message}";
                State.NotifyChanged();
            }
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(State.Cancellation, cts))
            {
                State.Cancellation = null;
                State.Busy = false;
                State.NotifyChanged();
                UpdateView();
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        State.Cancellation?.Cancel();
        State.Status = "Stopping reply…";
        State.NotifyChanged();
    }

    private void OnNewScene(object sender, RoutedEventArgs e)
    {
        State.SceneId++;
        State.Cancellation?.Cancel();
        State.Cancellation = null;
        State.Busy = false;
        State.History.Clear();
        State.Turns.Clear();
        State.Persona = "";
        State.Scenario = "";
        _syncing = true;
        PersonaBox.Clear();
        ScenarioBox.Clear();
        _syncing = false;
        InputBox.Clear();
        State.Draft = "";
        State.Status = "New scene. Choose a persona and setting to begin.";
        State.NotifyChanged();
        UpdateView();
        PersonaBox.Focus();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (State.Turns.Count == 0) return;
        var transcript = string.Join(Environment.NewLine + Environment.NewLine,
            State.Turns.Select(t => $"You: {t.UserText}{Environment.NewLine}{t.Persona}: {t.Answer}"));
        if (Sandbox.Intercept("clipboard", transcript))
        {
            State.Status = "Copy skipped in preview mode.";
            State.NotifyChanged();
            return;
        }
        try { Clipboard.SetText(transcript); State.Status = "Transcript copied."; }
        catch (Exception ex) { State.Status = $"Couldn't copy transcript: {ex.Message}"; }
        State.NotifyChanged();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void UpdateView()
    {
        EmptyState.Visibility = State.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = State.Busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = State.Busy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !State.Busy;
        InputBox.IsEnabled = !State.Busy;
        PersonaBox.IsEnabled = !State.Busy;
        ScenarioBox.IsEnabled = !State.Busy;
        ModelLabel.Text = Hub.S.Ai.Provider.Equals("llamacpp", StringComparison.OrdinalIgnoreCase)
            ? "Uses Creative from Models, or your main model. Scenes stay in this session; copy the transcript to keep it."
            : "Uses your configured AI model";
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

    private sealed class Session
    {
        public ObservableCollection<Turn> Turns { get; } = new();
        public List<LlmMessage> History { get; } = new();
        public string Persona { get; set; } = "";
        public string Scenario { get; set; } = "";
        public string Draft { get; set; } = "";
        public CancellationTokenSource? Cancellation { get; set; }
        public bool Busy { get; set; }
        public long SceneId { get; set; }
        public string Status { get; set; } = "Ready when you are.";
        public event Action? Changed;
        public void NotifyChanged() => Changed?.Invoke();
    }

    private sealed class Turn(string userText, string persona) : INotifyPropertyChanged
    {
        private string _answer = "";
        public string UserText { get; } = userText;
        public string Persona { get; } = persona;
        public string Answer { get => _answer; set { _answer = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
    }
}
