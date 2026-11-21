using AquaHub.Core;
using AquaHub.Core.Agents;
using AquaHub.Core.Settings;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Search;
using AquaHub.Platform;
using AquaHub.Services;

if (args is not [("quality" or "performance"), var modelPath, var archivePath, var parent])
    throw new ArgumentException("Expected quality|performance, pinned GGUF, pinned CPU x64 ZIP, and a scratch parent on local NTFS.");
string run = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(parent), "Search run " + Guid.NewGuid().ToString("N"))).FullName;
string corpus = Directory.CreateDirectory(Path.Combine(run, "Documents")).FullName;
var paths = HubPaths.Resolve(Path.Combine(run, "profile"));
Directory.CreateDirectory(paths.Llama);
var policy = new WindowsSearchFiles(paths.Root);
policy.Approve(corpus);
using (var archive = File.OpenRead(archivePath))
{
    var pin = LlamaCatalog.Cpu.Archive;
    if (archive.Length != pin.Size || !Convert.ToHexString(SHA256.HashData(archive)).Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("CPU runtime verification failed.");
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    foreach (var entry in zip.Entries.Where(e => e.Name == "llama-server.exe" || e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        entry.ExtractToFile(Path.Combine(paths.Llama, Path.GetFileName(entry.Name)));
}

var recommendation = SearchEmbeddings.Pin;
File.WriteAllText(Path.Combine(paths.Llama, "llama.json"), JsonSerializer.Serialize(new { Build = LlamaCatalog.Build, Backend = "cpu", Server = "llama-server.exe" }));
await using var core = new HubCore(paths, new InMemorySecretStore(), new NullPlatform());
core.Settings.Update(s =>
{
    s.Ai.Enabled = true;
    s.Ai.ManageOllama = s.Ai.StartOllamaOnDemand = s.Ai.Llama.UseOllamaModels = s.Ask.Computer = false;
    s.Ai.Llama.SearchModel = recommendation.Id;
});
Directory.CreateDirectory(paths.Models);
var copied = Path.Combine(paths.Models, recommendation.File.Name);
File.Copy(modelPath, copied);
using (var file = File.OpenRead(copied))
    if (file.Length != recommendation.File.Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(recommendation.File.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Model verification failed.");
core.Models.AddDownload(recommendation.Repo, recommendation.Revision, recommendation.File.Name, copied, recommendation.File.Size, recommendation.File.Sha256, null, DateTimeOffset.UtcNow);
using var host = new LlamaProcessHost();
var session = new SearchSession(core, host);
var service = await session.GetFilesAsync();
try
{
    if (args[0] == "performance")
    {
        await SearchPerformance.Measure(corpus, Path.Combine(paths.Llama, "llama-server.exe"), Path.Combine(paths.Root, "search.sqlite"), service);
        return;
    }
    var labels = JsonSerializer.Deserialize<string[][]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "search-relevance.json")))!;
    foreach (var label in labels) File.WriteAllText(Path.Combine(corpus, label[0] + ".txt"), label[1]);
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    service.Changed += () =>
    {
        if (service.Status.Files == labels.Length && service.Status.Semantic == "Meaning search ready") done.TrySetResult();
    };
    await service.AddAsync(corpus, true);
    await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    int topThree = 0, lexicalTopThree = 0;
    int heldOut = 0, heldOutCount = 0;
    foreach (var label in labels.Where(l => l.Length == 3))
    {
        var hits = await service.FindAsync(label[2], new(), true, default);
        int rank = hits.FindIndex(h => h.Name == label[0] + ".txt") + 1;
        Console.WriteLine($"{label[0]}  rank {rank}");
        if (!label[0].StartsWith('A'))
        {
            heldOutCount++;
            if (rank is >= 1 and <= 3) heldOut++;
            continue;
        }
        var lexical = await service.FindAsync(label[2], new(), false, default);
        if (lexical.Take(3).Any(h => h.Name == label[0] + ".txt")) lexicalTopThree++;
        if (rank is >= 1 and <= 3) topThree++;
    }
    var ambiguous = await service.FindAsync("bicycle club", new(), true, default);
    var drafts = ambiguous.Take(3).Select(h => h.Name).ToHashSet();
    bool bothDrafts = drafts.Contains("B01.txt") && drafts.Contains("B02.txt");
    var specific = await service.FindAsync("March draft", new(), true, default);
    bool marchFirst = specific.FirstOrDefault()?.Name == "B02.txt";
    int unrelated = 0;
    foreach (var query in new[] { "zyxqvplmf742", "Hadronic scattering cross sections inside a particle accelerator", "How to invert a Mandelbrot fractal?" })
        unrelated += (await service.FindAsync(query, new(), true, default)).Count;
    Console.WriteLine($"Lexical top three {lexicalTopThree}/10. Hybrid top three {topThree}/10. Unrelated hits {unrelated}. Scratch output {run}");
    Console.WriteLine($"Held-out queries top three {heldOut}/{heldOutCount}. Both drafts in top three: {bothDrafts}. March draft first: {marchFirst}.");
    if (topThree < 8 || unrelated != 0 || !bothDrafts || !marchFirst) Environment.ExitCode = 1;
}
finally { await session.StopAsync(); }
