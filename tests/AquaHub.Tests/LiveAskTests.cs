using System.Diagnostics;
using System.Text;
using AquaHub.Core;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

/// <summary>
/// Ask against the real local model, real web and a copy of a real profile. Run with AQUAHUB_LIVE=1 and
/// AQUAHUB_ASK_PROFILE=&lt;profile folder&gt; (its settings name the model endpoint). Writes a transcript to
/// %TEMP%\aquahub-live-ask.md for review.
/// </summary>
public class LiveAskTests
{
    private static readonly string Report = Path.Combine(Path.GetTempPath(), "aquahub-live-ask.md");
    private static readonly object Gate = new();

    private sealed class RecordingHost : IAskHost
    {
        public readonly List<string> Steps = new();
        public readonly StringBuilder Answer = new();
        public readonly StringBuilder Reasoning = new();
        public readonly List<string> Approvals = new();
        public void Status(string text) { }
        public int StepStarted(string icon, string text) { lock (Steps) { Steps.Add("… " + text); return Steps.Count; } }
        public void StepFinished(int id, string text, bool ok = true, string? url = null) { lock (Steps) Steps[id - 1] = (ok ? "✓ " : "✗ ") + text; }
        public void Thinking(string delta) => Reasoning.Append(delta);
        public void Text(string delta) => Answer.Append(delta);
        public void ResetText() => Answer.Clear();
        public Task<bool> ApproveAsync(ToolApproval request, CancellationToken ct) { Approvals.Add(request.Title); return Task.FromResult(true); }
    }

    private static (HubCore Core, TempDir Dir) Open()
    {
        var source = Environment.GetEnvironmentVariable("AQUAHUB_ASK_PROFILE") ?? throw new InvalidOperationException("Set AQUAHUB_ASK_PROFILE");
        var dir = new TempDir();
        foreach (var f in new[] { "settings.json", "hub.db" }) File.Copy(Path.Combine(source, f), Path.Combine(dir.Path, f));
        return (new HubCore(HubPaths.Resolve(dir.Path), new InMemorySecretStore(), new NullPlatform()), dir);
    }

    private static async Task<(AskResult Result, RecordingHost Host)> AskAsync(HubCore core, string title, string question, AskOptions options,
        IReadOnlyList<AskAttachment>? attachments = null, IAskPlatform? platform = null, IReadOnlyList<LlmMessage>? history = null)
    {
        var host = new RecordingHost();
        var sw = Stopwatch.StartNew();
        var result = await core.Assistant.RunAsync(question, history ?? Array.Empty<LlmMessage>(), attachments ?? Array.Empty<AskAttachment>(), options, host, platform,
            new HashSet<string>(), CancellationToken.None);
        var sb = new StringBuilder();
        sb.Append("## ").Append(title).Append("\n\n**Q:** ").Append(question).Append("  \n**Mode:** ")
          .Append(options.Research ? "Research " : "").Append(options.Web ? "Web " : "").Append(options.Think ? "Think " : "").Append(options.Computer ? "PC " : "")
          .Append(options.StoryId is not null ? "Story " : "").Append($"· {sw.Elapsed.TotalSeconds:0}s · {result.Model} · {result.ToolCalls} tool calls\n\n");
        sb.Append("**Steps:**\n").Append(string.Join("\n", host.Steps.Select(s => "- " + s))).Append("\n\n");
        if (result.Thinking.Length > 0) sb.Append("**Reasoning (first 600 chars):** ").Append(result.Thinking.Length > 600 ? result.Thinking[..600] + "…" : result.Thinking).Append("\n\n");
        sb.Append("**Answer:**\n\n").Append(result.Text).Append("\n\n**Cited:** ")
          .Append(string.Join("; ", result.Citations.Select(c => $"[{c.Number}] {c.Source} — {c.Title} ({c.Url})"))).Append("\n\n");
        lock (Gate) File.AppendAllText(Report, sb.ToString());
        return (result, host);
    }

    [LiveFact]
    public async Task TellMeMoreAboutAStory()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var story = core.State.Stories.First(c => c.Title.Contains("Starship", StringComparison.OrdinalIgnoreCase));
            var (result, _) = await AskAsync(core, "Tell me more (story)", "Tell me more about: " + (story.Summary?.Headline ?? story.Title),
                new AskOptions { StoryId = story.Id, Web = true });
            Assert.Contains("orbit", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(result.Citations);
            Assert.DoesNotContain("does not contain information", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [LiveFact]
    public async Task LooksThingsUpOnTheWeb()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var (result, host) = await AskAsync(core, "Web lookup", "What does the James Webb Space Telescope's NIRSpec instrument do? Keep it short.",
                new AskOptions { Web = true });
            Assert.True(result.Text.Length > 80);
            Assert.Contains(host.Steps, s => s.Contains("Searched the web", StringComparison.Ordinal) || s.Contains("Read ", StringComparison.Ordinal));
        }
    }

    [LiveFact]
    public async Task ResearchWritesACitedReport()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var (result, host) = await AskAsync(core, "Research", "What went right and wrong on SpaceX Starship's first orbital flight, and what happens next?",
                new AskOptions { Research = true, Web = true });
            Assert.True(host.Steps.Count(s => s.StartsWith("✓ Read", StringComparison.Ordinal)) >= 2, "fewer than two pages read:\n" + string.Join("\n", host.Steps));
            Assert.True(result.Citations.Count >= 2);
            Assert.Contains("##", result.Text);
        }
    }

    [LiveFact]
    public async Task SearchesAndReadsYourFiles()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var docs = Directory.CreateDirectory(Path.Combine(dir.Path, "MyDocs", "Home")).FullName;
            File.WriteAllText(Path.Combine(docs, "boiler service notes.txt"),
                "Boiler: Worcester Greenstar 30i. Last serviced 14 October 2025 by Dublin Heating Ltd (Mark). Next service due by 14 October 2026. Cost €120.");
            File.WriteAllText(Path.Combine(docs, "shopping.txt"), "milk, bread, coffee");
            core.Settings.Update(s => s.Ask.Folders = new List<string> { Path.Combine(dir.Path, "MyDocs") });
            var (result, host) = await AskAsync(core, "Use my PC (files)", "When is my boiler service due, and who did the last one?", new AskOptions { Computer = true });
            // The facts from the file: when it's due and who did the last one (the model may leave the year off).
            Assert.Contains("14 October", result.Text.Replace("Oct ", "October ").Replace("14th", "14"), StringComparison.OrdinalIgnoreCase);
            Assert.True(result.Text.Contains("Dublin Heating", StringComparison.OrdinalIgnoreCase) || result.Text.Contains("Mark", StringComparison.Ordinal), "who did the last service is missing");
            Assert.Contains(host.Steps, s => s.Contains("Searched your files", StringComparison.Ordinal) || s.Contains("Read boiler", StringComparison.Ordinal));
        }
    }

    [LiveFact]
    public async Task SeesAnAttachedImage()
    {
        var shot = Environment.GetEnvironmentVariable("AQUAHUB_ASK_IMAGE");
        if (shot is null || !File.Exists(shot)) return;
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var image = new AskAttachment { Name = Path.GetFileName(shot), Kind = AttachmentKind.Image, Image = await File.ReadAllBytesAsync(shot) };
            var (result, _) = await AskAsync(core, "Image attachment (vision)", "What is this screen showing? Two sentences.", new AskOptions(), new[] { image });
            Assert.True(result.Text.Length > 40);
        }
    }

    /// <summary>PNGs drawn in code (no image library): a blue flame, and decoys.</summary>
    private static byte[] Png(int size, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var raw = new byte[size * (size * 3 + 1)];
        for (var y = 0; y < size; y++)
        {
            raw[y * (size * 3 + 1)] = 0;
            for (var x = 0; x < size; x++)
            {
                var (r, g, b) = pixel(x, y);
                var o = y * (size * 3 + 1) + 1 + x * 3;
                raw[o] = r; raw[o + 1] = g; raw[o + 2] = b;
            }
        }
        using var ms = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len);
            ms.Write(len);
            var t = Encoding.ASCII.GetBytes(type);
            ms.Write(t); ms.Write(data);
            var crc = BitConverter.GetBytes(Crc(t.Concat(data).ToArray())); Array.Reverse(crc);
            ms.Write(crc);
        }
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        var w = BitConverter.GetBytes(size); Array.Reverse(w);
        Array.Copy(w, 0, ihdr, 0, 4); Array.Copy(w, 0, ihdr, 4, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static (byte, byte, byte) BlueFlame(int x, int y)
    {
        // A flame: wide at the bottom, tapering to a tip, flickering edges; white-cyan core, blue body, dark background.
        const int size = 384;
        var t = (y - 40.0) / (size - 80.0);
        if (t is < 0 or > 1) return (12, 16, 30);
        var width = 150 * Math.Pow(Math.Sin(Math.PI * Math.Min(t * 1.15, 1) / 2), 1.6) * (t > 0.85 ? (1 - t) / 0.15 : 1) + 10 * Math.Sin(y * 0.09);
        var dx = Math.Abs(x - size / 2.0);
        if (dx > width) return (12, 16, 30);
        var core = dx < width * 0.35 && t > 0.45;
        return core ? ((byte)190, (byte)235, (byte)255) : ((byte)(20 + 40 * t), (byte)(80 + 60 * t), (byte)255);
    }

    [LiveFact]
    public async Task FindsAPictureByWhatItShows()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var pictures = Directory.CreateDirectory(Path.Combine(dir.Path, "MyStuff", "Pictures")).FullName;
            File.WriteAllBytes(Path.Combine(pictures, "my_logo5.png"), Png(384, BlueFlame));
            File.WriteAllBytes(Path.Combine(pictures, "logo_old.png"), Png(384, (x, y) => Math.Abs(x - 192) < 110 && Math.Abs(y - 192) < 110 ? ((byte)210, (byte)30, (byte)40) : ((byte)245, (byte)245, (byte)245)));
            File.WriteAllBytes(Path.Combine(pictures, "logo_green.png"), Png(384, (x, y) => Math.Pow((x - 192) / 150.0, 2) + Math.Pow((y - 192) / 80.0, 2) < 1 ? ((byte)40, (byte)170, (byte)60) : ((byte)255, (byte)255, (byte)255)));
            File.WriteAllBytes(Path.Combine(pictures, "holiday.jpg"), Png(384, (x, y) => y > 250 ? ((byte)60, (byte)150, (byte)50) : Math.Pow(x - 300, 2) + Math.Pow(y - 80, 2) < 1600 ? ((byte)255, (byte)220, (byte)60) : ((byte)120, (byte)180, (byte)240)));
            File.WriteAllText(Path.Combine(dir.Path, "MyStuff", "notes.txt"), "shopping: milk");
            core.Settings.Update(s => s.Ask.Folders = new List<string> { Path.Combine(dir.Path, "MyStuff") });
            var (result, host) = await AskAsync(core, "Use my PC (find a picture by what it shows)", "Find me my logo photo of a blue flame",
                new AskOptions { Computer = true, Think = true }, platform: new LivePlatform());
            Assert.Contains("my_logo5", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(host.Steps, s => s.Contains("Looked at", StringComparison.Ordinal));
        }
    }

    [LiveFact]
    public async Task FindsSomeoneOnTwitterAndLinksThem()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var (result, host) = await AskAsync(core, "Web (find a social account and link it)", "Find the Re:Zero leaker Ice on twitter and link me it",
                new AskOptions { Web = true, Think = true });
            Assert.Contains(host.Steps, s => s.StartsWith("✓ Plan:", StringComparison.Ordinal));
            Assert.DoesNotContain(host.Steps, s => s.Contains("“Find the Re:Zero leaker Ice on twitter and link me it”", StringComparison.Ordinal));
            Assert.True(result.Text.Contains("x.com/", StringComparison.OrdinalIgnoreCase) || result.Text.Contains("twitter.com/", StringComparison.OrdinalIgnoreCase) ||
                        result.Citations.Any(c => c.Url?.Contains("x.com/", StringComparison.OrdinalIgnoreCase) == true || c.Url?.Contains("twitter.com/", StringComparison.OrdinalIgnoreCase) == true),
                "no X/Twitter link in the answer or its sources");
            Assert.DoesNotContain("can't browse", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("I couldn't come up with", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [LiveFact]
    public async Task SearchesThroughASiteItIsGiven()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var (result, host) = await AskAsync(core, "Web (search through a site)", "Look through https://arstechnica.com/space/ and tell me the newest articles about SpaceX, with links",
                new AskOptions { Web = true });
            Assert.Contains(host.Steps, s => s.StartsWith("✓ Read arstechnica.com", StringComparison.Ordinal));
            Assert.Contains(result.Citations, c => c.Url?.Contains("arstechnica.com", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain("cannot perform direct browser", result.Text, StringComparison.OrdinalIgnoreCase);
            // A list of the site's own articles, each with its link (not one item, and not the category page).
            var articles = System.Text.RegularExpressions.Regex.Matches(result.Text + " " + string.Join(' ', result.Citations.Select(c => c.Url)),
                    @"https://arstechnica\.com/[a-z-]+/20\d\d/\d\d/[a-z0-9-]+/?")
                .Select(m => m.Value.TrimEnd('/')).Distinct().Count();
            Assert.True(articles >= 3, $"only {articles} distinct Ars Technica article link(s)");
        }
    }

    [LiveFact]
    public async Task SummarisesAnAttachmentWithoutSearchingThePc()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var mine = Directory.CreateDirectory(Path.Combine(dir.Path, "MyStuff")).FullName;
            File.WriteAllText(Path.Combine(mine, "meeting notes.txt"), "Garden: plant the tulip bulbs in October.");
            core.Settings.Update(s => s.Ask.Folders = new List<string> { mine });
            var notes = new AskAttachment
            {
                Name = "notes.txt", Kind = AttachmentKind.Document,
                Text = "Project Falcon review, 29 September. The budget of €40,000 was approved by finance. The public launch moves from April to May because the app store review takes longer. Aoife owns the release checklist.",
            };
            var (result, host) = await AskAsync(core, "Use my PC + attachment", "Summarise the attached notes in two bullets", new AskOptions { Computer = true },
                attachments: new[] { notes }, platform: new LivePlatform());
            Assert.DoesNotContain(host.Steps, s => s.Contains("your files", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("tulip", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("May", result.Text, StringComparison.Ordinal);
        }
    }

    [LiveFact]
    public async Task SummarisesAWholeLongChapter()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            // ~43,000 characters: read in parts, so the summary covers the end too (it used to stop a sixth of the way in).
            var (result, host) = await AskAsync(core, "Web (summarise a long chapter)",
                "Summarise this chapter for me: https://www.royalroad.com/fiction/21220/mother-of-learning/chapter/301778/1-good-morning-brother",
                new AskOptions { Web = true });
            Assert.Contains(host.Steps, s => s.StartsWith("✓ Read royalroad.com", StringComparison.Ordinal));
            var parts = host.Steps.Where(s => s.StartsWith("✓ Read part ", StringComparison.Ordinal)).ToList();
            Assert.True(parts.Count >= 3, "the chapter wasn't read in parts: " + string.Join(" | ", host.Steps));
            Assert.Contains(parts, s => System.Text.RegularExpressions.Regex.IsMatch(s, @"Read part (\d+) of \1\b")); // the last part too
            Assert.Matches("(?i)Kiri|sister|mother|porridge|woke|waking", result.Text);                            // the start
            Assert.Matches(@"(?i)bike|bicycle|levitat|mud|Ilsa|Cyoria|academy", result.Text);                   // and later on
            Assert.DoesNotContain("cuts off", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [LiveFact]
    public async Task AFollowUpUsesTheChatToSearchAgain()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var pictures = Directory.CreateDirectory(Path.Combine(dir.Path, "MyStuff", "Pictures", "Brand")).FullName;
            File.WriteAllBytes(Path.Combine(pictures, "final_v3.png"), Png(384, BlueFlame));
            core.Settings.Update(s => s.Ask.Folders = new List<string> { Path.Combine(dir.Path, "MyStuff") });
            var history = new[]
            {
                new LlmMessage("user", "Find my logo"),
                new LlmMessage("assistant", "I couldn't find a file with “logo” in its name. Do you remember where it is or what it's called?"),
            };
            var (result, host) = await AskAsync(core, "Use my PC (follow-up)", "It's the blue flame one, somewhere in my Pictures",
                new AskOptions { Computer = true }, platform: new LivePlatform(), history: history);
            Assert.Contains("final_v3", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>What the live tests need from the platform: images passed through as they are, no search index.</summary>
    private sealed class LivePlatform : IAskPlatform
    {
        public IEnumerable<AskTool> ComputerTools() => Array.Empty<AskTool>();
        public Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct) => Task.FromResult(("", 0, 0));
        public Task<string> ReadImageTextAsync(byte[] image, CancellationToken ct) => Task.FromResult("");
        public byte[] PrepareImage(byte[] image, int maxEdge = 1600) => image;
        public string? KnownFolder(string token) => null;
        public Task<IReadOnlyList<IndexedFile>> SearchIndexAsync(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndexedFile>>(Array.Empty<IndexedFile>());
    }

    [LiveFact]
    public async Task ThinksBeforeAnswering()
    {
        var (core, dir) = Open();
        using (dir)
        using (core)
        {
            var (result, _) = await AskAsync(core, "Think", "A train leaves Dublin at 14:10 and takes 2 h 35 min to Cork; a bus leaves at 13:50 and takes 3 h 5 min. Which arrives first and by how much?",
                new AskOptions { Think = true });
            Assert.NotEmpty(result.Thinking);
            // 14:10 + 2 h 35 = 16:45; 13:50 + 3 h 5 = 16:55: the train, by 10 minutes.
            Assert.Contains("train", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("16:45", result.Text, StringComparison.Ordinal);
            Assert.Contains("16:55", result.Text, StringComparison.Ordinal);
            Assert.Matches(@"(?i)\b(10|ten)[\s-]*min", result.Text);
        }
    }
}
