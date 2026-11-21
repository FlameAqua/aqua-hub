using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;

namespace AquaHub.Core.Ai.Roleplay;

public sealed record CreativeRoute(string? Model, int ContextTokens, bool RequireModel);

/// <summary>One tool-free scene, used on the presentation context. A reset invalidates every callback from the old scene.</summary>
public sealed class RoleplaySession
{
    public const int MaxLine = 1200;
    private const int MaxReply = 6000;
    private const int MaxTurns = 80;
    private readonly ObservableCollection<RoleplayTurn> _turns = [];
    private readonly Func<CreativeRoute> _route;
    private readonly Func<LlmRequest, CancellationToken, IAsyncEnumerable<LlmDelta>> _stream;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private long _scene;
    private bool _closed;

    public RoleplaySession(Func<CreativeRoute> route, Func<LlmRequest, CancellationToken, IAsyncEnumerable<LlmDelta>> stream)
    {
        Turns = new(_turns);
        _route = route;
        _stream = stream;
    }

    public ReadOnlyObservableCollection<RoleplayTurn> Turns { get; }
    public Personality? Personality { get; private set; }
    public string Persona { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string Draft { get; set; } = "";
    public bool Busy => _cancellation is not null;
    public string Notice { get; private set; } = "";
    public bool Error { get; private set; }
    public bool ModelMissing { get; private set; }
    public event Action? Changed;

    public void Start(Personality? personality = null)
    {
        _scene++;
        _cancellation?.Cancel();
        Personality = personality;
        Persona = personality?.Instructions ?? "";
        Scenario = personality?.OpeningScenario ?? "";
        Draft = "";
        Notice = "";
        Error = ModelMissing = false;
        _turns.Clear();
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (_cancellation is not { IsCancellationRequested: false } current) return;
        current.Cancel();
        Notice = "Stopping reply…";
        Changed?.Invoke();
    }

    public Task ShutdownAsync()
    {
        _closed = true;
        Start();
        return _worker;
    }

    public Task SendAsync()
    {
        var line = Draft.Trim();
        if (_closed || Busy || line.Length is 0 or > MaxLine) return Task.CompletedTask;
        var source = _cancellation = new();
        return _worker = GenerateAsync(line, _scene, source);
    }

    private async Task GenerateAsync(string line, long scene, CancellationTokenSource source)
    {
        using var cancellation = source;
        var label = Personality?.Name ?? (string.IsNullOrWhiteSpace(Persona) ? "Scene partner" : ShortLabel(Persona));
        var turn = new RoleplayTurn(line, label);
        var history = _turns.SelectMany(t => new[] { new LlmMessage("user", t.UserText), new LlmMessage("assistant", t.Answer) });
        var context = history.ToArray();
        _turns.Add(turn);
        Draft = "";
        Error = ModelMissing = false;
        Notice = "Starting reply…";
        Changed?.Invoke();

        try
        {
            var model = _route();
            var system = RoleplayPrompt.BuildSystem(Persona, Scenario);
            var historyBudget = Math.Max(0, RoleplayPrompt.HistoryBudget(model.ContextTokens) - system.Length - line.Length);
            var request = new LlmRequest
            {
                System = system,
                Messages = RoleplayPrompt.BuildHistory(context, line, maxCharacters: historyBudget),
                Model = model.Model,
                RequireModel = model.RequireModel,
                CaptureOutput = false,
                Temperature = 0.85,
                MaxTokens = 900,
                Priority = LlmPriority.Interactive,
                Purpose = "Roleplay",
                Think = false,
                Tools = [],
            };
            var answer = new StringBuilder();
            await foreach (var delta in _stream(request, cancellation.Token))
            {
                if (scene != _scene) return;
                cancellation.Token.ThrowIfCancellationRequested();
                if (delta is not LlmTextDelta chunk || chunk.Text.Length == 0) continue;
                var length = Math.Min(chunk.Text.Length, MaxReply - answer.Length);
                if (length > 0 && char.IsHighSurrogate(chunk.Text[length - 1]) && length < chunk.Text.Length) length--;
                answer.Append(chunk.Text.AsSpan(0, length));
                turn.Answer = answer.ToString();
                if (Notice == "Starting reply…")
                {
                    Notice = "Writing…";
                    Changed?.Invoke();
                }
                if (answer.Length >= MaxReply - 1) break;
            }
            if (scene != _scene) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(turn.Answer))
                Fail(turn, "The model returned an empty reply. Try again.");
            else
                Notice = "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (scene != _scene) return;
            if (turn.Answer.Length == 0)
            {
                _turns.Remove(turn);
                Draft = line;
            }
            Notice = "Reply stopped.";
        }
        catch (Exception ex)
        {
            if (scene != _scene) return;
            ModelMissing = ex is RoleplayModelUnavailableException;
            Fail(turn, ModelMissing ? "The selected Creative model is missing. Choose an installed model or use Main."
                : "The configured model couldn't reply. Check Models or try again.");
        }
        finally
        {
            _cancellation = null;
            while (_turns.Count > MaxTurns) _turns.RemoveAt(0);
            Changed?.Invoke();
        }
    }

    private void Fail(RoleplayTurn turn, string notice)
    {
        _turns.Remove(turn);
        Draft = turn.UserText;
        Notice = notice;
        Error = true;
    }

    private static string ShortLabel(string value)
    {
        var text = value.Trim();
        return text.Length <= 80 ? text : text[..(char.IsHighSurrogate(text[79]) ? 79 : 80)] + "…";
    }
}

public sealed class RoleplayTurn(string userText, string persona) : INotifyPropertyChanged
{
    private string _answer = "";
    public string UserText { get; } = userText;
    public string Persona { get; } = persona;
    public string Answer
    {
        get => _answer;
        internal set { _answer = value; PropertyChanged?.Invoke(this, new(nameof(Answer))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
