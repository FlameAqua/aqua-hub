using AquaHub.Core.Ai.Assistant;

namespace AquaHub.Core.Speech;

public sealed record ReadAloudRequest(object Owner, string Text, string Language, string Backend, string Voice, bool Markdown = false);
public enum ReadAloudStage { Idle, Preparing, Playing, Failed }
public sealed record ReadAloudState(object? Owner = null, ReadAloudStage Stage = ReadAloudStage.Idle, string? Message = null)
{
    public bool Active => Stage is ReadAloudStage.Preparing or ReadAloudStage.Playing;
}

public sealed class ReadAloudException(string message) : Exception(message);

/// <summary>One player, one synthesis operation and at most one replacement. Both adapters run off the UI thread.</summary>
public sealed class ReadAloud(
    Func<ReadAloudRequest, string, CancellationToken, Task<IDisposable>> synthesize,
    Func<IDisposable, CancellationToken, Task> play)
{
    public const int MaxSource = 32_000;
    public const int MaxSpoken = 8_000;
    public const int MaxChunk = 128;
    private const int MaxFirstChunk = 60;
    private const int MaxChunks = 96;

    private readonly object _gate = new();
    private ReadAloudRequest? _pending;
    private CancellationTokenSource? _current;
    private Task? _worker;
    private bool _closed;
    private ReadAloudState _state = new();
    public ReadAloudState State => Volatile.Read(ref _state);
    public event Action? Changed;

    public void Toggle(ReadAloudRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) return;
        lock (_gate)
        {
            if (_closed) return;
            _current?.Cancel();
            if (State.Active && ReferenceEquals(State.Owner, request.Owner))
            {
                _pending = null;
                Set(new());
            }
            else
            {
                _pending = request with { Text = Clip(request.Text, MaxSource + 1) };
                Set(new(request.Owner, ReadAloudStage.Preparing));
                _worker ??= Task.Run(RunAsync);
            }
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _pending = null;
            _current?.Cancel();
            Set(new());
        }
        Changed?.Invoke();
    }

    public Task ShutdownAsync()
    {
        lock (_gate) _closed = true;
        Stop();
        lock (_gate) return _worker ?? Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            ReadAloudRequest request;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _worker = null;
                    return;
                }
                request = next;
                _pending = null;
                _current = cancellation = new();
            }

            ReadAloudState outcome = new();
            try
            {
                var (chunks, truncated) = Prepare(request.Text, request.Markdown);
                if (chunks.Count == 0) throw new ReadAloudException("There's no text to read aloud.");
                await SpeakAsync(request, chunks, truncated, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                outcome = new(request.Owner, ReadAloudStage.Failed,
                    ex is ReadAloudException ? ex.Message : "Read aloud failed. Try again or choose a different voice in Settings.");
            }
            bool publish;
            lock (_gate)
            {
                _current = null;
                publish = !cancellation.IsCancellationRequested;
                cancellation.Dispose();
                if (publish) Set(outcome);
            }
            if (publish) Changed?.Invoke();
        }
    }

    private async Task SpeakAsync(ReadAloudRequest request, IReadOnlyList<string> chunks, bool truncated, CancellationToken cancellation)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<IDisposable>? next = null;
        try
        {
            next = synthesize(request, chunks[0], preparation.Token);
            for (var i = 0; i < chunks.Count; i++)
            {
                var ready = next!;
                next = null;
                using var audio = await ready.ConfigureAwait(false);
                Task playback;
                lock (_gate)
                {
                    // Stop or replacement can run while synthesis ignores cancellation.
                    cancellation.ThrowIfCancellationRequested();
                    playback = play(audio, cancellation);
                    Set(new(request.Owner, ReadAloudStage.Playing, i == 0 && truncated ? "Reading the first part of this answer." : null));
                }
                Changed?.Invoke();
                if (i + 1 < chunks.Count) next = PrepareNextAsync(chunks[i + 1]);
                await playback.ConfigureAwait(false);
                if (next is { IsCompleted: false })
                {
                    lock (_gate)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Set(new(request.Owner, ReadAloudStage.Preparing));
                    }
                    Changed?.Invoke();
                }
            }
        }
        finally
        {
            preparation.Cancel();
            if (next is not null)
                try { (await next.ConfigureAwait(false)).Dispose(); }
                catch { /* Playback failed or was stopped. Synthesis was joined. */ }
        }

        async Task<IDisposable> PrepareNextAsync(string chunk) =>
            await synthesize(request, chunk, preparation.Token).ConfigureAwait(false);
    }

    private void Set(ReadAloudState state) => Volatile.Write(ref _state, state);

    public static (IReadOnlyList<string> Chunks, bool Truncated) Prepare(string source, bool markdown)
    {
        var text = Clip(source, MaxSource);
        if (markdown)
        {
            using var reader = new StringReader(text);
            var prose = new System.Text.StringBuilder();
            char fence = '\0';
            var fenceLength = 0;
            while (reader.ReadLine() is { } line)
            {
                var content = line.TrimStart();
                var marker = content.Length > 0 ? content[0] : '\0';
                var count = marker is '`' or '~' ? content.TakeWhile(c => c == marker).Count() : 0;
                if (fence == '\0')
                {
                    if (count < 3) { prose.AppendLine(line); continue; }
                    fence = marker;
                    fenceLength = count;
                    prose.AppendLine("Code block omitted.");
                }
                else if (fence == marker && count >= fenceLength && content[count..].Trim().Length == 0)
                    fence = '\0';
            }
            text = AnswerText.Spoken(prose.ToString());
        }

        var truncated = source.Length > MaxSource || text.Length > MaxSpoken;
        text = Clip(text, MaxSpoken).Trim();
        var chunks = new List<string>();
        while (text.Length > (chunks.Count == 0 ? MaxFirstChunk : MaxChunk) && chunks.Count < MaxChunks - 1)
        {
            var limit = chunks.Count == 0 ? MaxFirstChunk : MaxChunk;
            var prefix = Clip(text, limit);
            var end = prefix.Length;
            for (var i = prefix.Length - 1; i >= limit / 3; i--)
            {
                if (char.IsWhiteSpace(prefix[i]) && prefix[i - 1] is '.' or '!' or '?')
                { end = i; break; }
            }
            if (end == prefix.Length)
            {
                var space = prefix.LastIndexOfAny([' ', '\n', '\r', '\t']);
                if (space >= limit / 3) end = space;
            }
            chunks.Add(Normalize(text[..end]));
            text = text[end..].TrimStart();
        }
        if (text.Length > MaxChunk) truncated = true;
        if (text.Length > 0) chunks.Add(Normalize(Clip(text, MaxChunk)));
        return (chunks, truncated);
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string text, int maximum) => text.Length <= maximum ? text
        : text[..(char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum)];
}
