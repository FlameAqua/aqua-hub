using System.Runtime.CompilerServices;
using System.Text.Json;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Roleplay;
using AquaHub.Core.Data;

namespace AquaHub.Tests;

public class PersonalityTests
{
    [Fact]
    public void SaveDuplicateRenameAndDeleteRoundTripInTheProfile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "profile.db");
        var library = new PersonalityLibrary(new(path));
        var original = library.Save(new(Guid.Empty, " Mira ", "Archivist", "A letter arrives."));
        var separate = new PersonalityLibrary(new(path));
        Assert.NotEqual(Guid.Empty, original.Id);
        Assert.Equal("Mira", original.Name);
        Assert.Equal(original, Assert.Single(separate.List()));
        Assert.Throws<InvalidOperationException>(() => separate.Save(original with { Id = Guid.Empty, Name = "mira" }));
        var copy = separate.Save(original with { Id = Guid.Empty, Name = "Mira copy" });
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original.Id, library.Save(original with { Name = "Mira Renamed" }).Id);
        Assert.True(separate.Delete(original.Id));
        Assert.False(separate.Delete(original.Id));
        Assert.Throws<InvalidOperationException>(() => library.Save(original));
        Assert.Equal(copy, Assert.Single(library.List()));
    }

    [Fact]
    public void PreviousArrayMigratesAndBadRecordsCannotHideValidOnes()
    {
        using var dir = new TempDir();
        var database = new HubDatabase(Path.Combine(dir.Path, "profile.db"));
        var entry = new Personality(Guid.NewGuid(), "Kept", "Quiet scholar", "");
        database.PutJson(PersonalityLibrary.StorageKey, new object[] { new { id = "broken" }, entry, entry, entry with { Id = Guid.Empty } });
        var library = new PersonalityLibrary(database);
        Assert.Equal(entry, Assert.Single(library.List()));
        library.Save(entry with { OpeningScenario = "A quiet room." });
        var saved = database.GetJson<JsonElement>(PersonalityLibrary.StorageKey);
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        Assert.Single(saved.GetProperty("entries").EnumerateArray());
        database.PutJson(PersonalityLibrary.StorageKey, new { version = 99, entries = new[] { entry } });
        Assert.Throws<InvalidOperationException>(() => library.Save(entry));
        Assert.Equal(99, database.GetJson<JsonElement>(PersonalityLibrary.StorageKey).GetProperty("version").GetInt32());
    }

    [Fact]
    public void RejectsBlankAndOversizedFieldsWithoutWriting()
    {
        using var dir = new TempDir();
        var library = new PersonalityLibrary(new(Path.Combine(dir.Path, "profile.db")));
        Assert.Throws<InvalidOperationException>(() => library.Save(new(Guid.Empty, " ", "Character", "")));
        Assert.Throws<InvalidOperationException>(() => library.Save(new(Guid.Empty, "Name", new('x', PersonalityLibrary.MaxInstructions + 1), "")));
        Assert.Empty(library.List());
    }

    [Fact]
    public async Task ADeletedOrEditedTemplateCannotChangeTheActiveSceneAndResetRejectsALateReply()
    {
        using var dir = new TempDir();
        var library = new PersonalityLibrary(new(Path.Combine(dir.Path, "profile.db")));
        var entry = library.Save(new(Guid.Empty, "Mira", "Quiet scholar", "A letter arrives."));
        var started = new TaskCompletionSource<LlmRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RoleplaySession(() => new("creative", 8192, true), Stream);
        session.Start(entry);
        session.Draft = "My first line";
        var pending = session.SendAsync();
        var request = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(request.RequireModel);
        Assert.False(request.CaptureOutput);
        Assert.Empty(request.Tools!);
        Assert.Contains("Quiet scholar", request.System);
        library.Save(entry with { Instructions = "Cartographer" });
        library.Delete(entry.Id);
        Assert.Equal("Quiet scholar", session.Persona);
        session.Start(new(Guid.NewGuid(), "New scene", "Different character", ""));
        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(session.Turns);
        Assert.Equal("Different character", session.Persona);
        Assert.False(session.Busy);

        async IAsyncEnumerable<LlmDelta> Stream(LlmRequest req, [EnumeratorCancellation] CancellationToken ignored)
        {
            started.SetResult(req);
            await release.Task;
            yield return new LlmTextDelta("Old reply");
        }
    }

    [Fact]
    public async Task MissingCreativeOffersRecoveryAndCanRetryTheSameLine()
    {
        bool installed = false;
        var session = new RoleplaySession(() => installed ? new("creative", 8192, true)
            : throw new RoleplayModelUnavailableException("creative"), Reply);
        session.Draft = "Please continue.";
        await session.SendAsync();
        Assert.True(session.ModelMissing);
        Assert.Empty(session.Turns);
        Assert.Equal("Please continue.", session.Draft);
        installed = true;
        await session.SendAsync();
        Assert.Equal("A new paragraph.", Assert.Single(session.Turns).Answer);
        Assert.False(session.ModelMissing);

        static async IAsyncEnumerable<LlmDelta> Reply(LlmRequest _, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new LlmTextDelta("A new paragraph.");
        }
    }

    [Fact]
    public async Task ShutdownCancelsAndJoinsAnUncooperativeModelStream()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var session = new RoleplaySession(() => new(null, 8192, false), Reply) { Draft = "Start a scene" };
        var pending = session.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = session.ShutdownAsync();
        Assert.True(captured.IsCancellationRequested);
        Assert.False(closing.IsCompleted);
        release.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await pending;
        Assert.Empty(session.Turns);

        async IAsyncEnumerable<LlmDelta> Reply(LlmRequest _, [EnumeratorCancellation] CancellationToken token)
        {
            captured = token;
            entered.SetResult();
            await release.Task;
            yield return new LlmTextDelta("Stale output");
        }
    }
}
