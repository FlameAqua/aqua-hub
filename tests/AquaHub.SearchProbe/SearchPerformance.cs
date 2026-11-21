using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AquaHub.Core.Search;
using Microsoft.Data.Sqlite;

internal static class SearchPerformance
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Io { public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32")]
    private static extern bool GetProcessIoCounters(IntPtr process, out Io counters);

    internal static async Task Measure(string folder, string server, string dbPath, FileSearchService service)
    {
        var passage = string.Concat(Enumerable.Repeat("Repot young specimens after the frost and check the greenhouse soil. ", 12));
        for (int i = 0; i < 2000; i++)
        {
            var path = Path.Combine(folder, i < 500 ? $"notebook-{i:D4}.txt" : $"exhibit-{i:D4}.bin");
            File.WriteAllText(path, i < 500 ? (passage + " Lot " + i).PadRight(20971) : "");
        }

        using var process = Process.GetCurrentProcess();
        long baseline = process.PrivateMemorySize64;
        var elapsed = Stopwatch.StartNew();
        long lexicalMs = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            var state = service.Status;
            if (state.Files != 2000) return;
            if (state.Activity == "Index up to date") Interlocked.CompareExchange(ref lexicalMs, elapsed.ElapsedMilliseconds, 0);
            if (state.Semantic == "Meaning search ready") done.TrySetResult();
        };
        using var sampling = new CancellationTokenSource();
        var peakTask = PeakPrivateBytes(server, sampling.Token);
        try
        {
            await service.AddAsync(folder, true);
            await done.Task.WaitAsync(TimeSpan.FromMinutes(20));
            Console.WriteLine($"Scan + extraction {lexicalMs} ms. Including embeddings {elapsed.ElapsedMilliseconds} ms.");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            long Count(string filter)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = filter;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            var vectors = Count("SELECT count(*) FROM chunks WHERE vector IS NOT NULL");
            long bytes = Directory.GetFiles(Path.GetDirectoryName(dbPath)!, Path.GetFileName(dbPath) + "*").Sum(p => new FileInfo(p).Length);
            Console.WriteLine($"Vectors {vectors}. SQLite including WAL/SHM {bytes} bytes.");
            if (vectors != 1000) throw new InvalidDataException("Incomplete benchmark corpus.");
            foreach (bool meaning in new[] { false, true })
            {
                var times = new List<double>();
                for (int i = 0; i < 21; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await service.FindAsync(i % 2 == 0 ? "notebook-0123.txt" : "when should I move the seedlings", new(), meaning, default);
                    if (i > 0) times.Add(watch.Elapsed.TotalMilliseconds);
                }
                Console.WriteLine($"Warm query p95 (meaning={meaning}, n=20) {times.Order().ElementAt(18):F1} ms");
            }
            sampling.Cancel();
            Console.WriteLine($"CPU server sampled peak {await peakTask} private bytes");
            elapsed.Restart();
            for (int i = 500; i < 1500; i++)
                File.Move(Path.Combine(folder, $"exhibit-{i:D4}.bin"), Path.Combine(folder, $"renamed-{i:D4}.bin"));
            while (Count("SELECT count(*) FROM docs WHERE path LIKE '%renamed-%'") != 1000 && elapsed.Elapsed < TimeSpan.FromMinutes(4))
                await Task.Delay(250);
            if (Count("SELECT count(*) FROM docs WHERE path LIKE '%renamed-%'") != 1000 || Count("SELECT count(*) FROM docs") != 2000)
                throw new InvalidDataException("Burst did not reconcile.");
            Console.WriteLine($"1,000 renames reconciled in {elapsed.ElapsedMilliseconds} ms.");
            await Task.Delay(TimeSpan.FromSeconds(125)); // allow the server's idle check to complete
            var dataVersion = Count("PRAGMA data_version");
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            if (!GetProcessIoCounters(process.Handle, out var before)) throw new IOException("Cannot sample process I/O.");
            elapsed.Restart();
            await Task.Delay(TimeSpan.FromMinutes(5));
            process.Refresh();
            if (!GetProcessIoCounters(process.Handle, out var after)) throw new IOException("Cannot sample process I/O.");
            Console.WriteLine($"Idle CPU {(process.TotalProcessorTime - cpu).TotalMilliseconds / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F3}% of machine. " +
                $"Read {after.ReadBytes - before.ReadBytes}, wrote {after.WriteBytes - before.WriteBytes} process bytes. " +
                $"Index commit version {dataVersion} to {Count("PRAGMA data_version")}. Probe private-byte delta {process.PrivateMemorySize64 - baseline}.");
        }
        finally { sampling.Cancel(); await peakTask; }
    }

    private static async Task<long> PeakPrivateBytes(string server, CancellationToken ct)
    {
        long peak = 0;
        while (!ct.IsCancellationRequested)
        {
            foreach (var process in Process.GetProcessesByName("llama-server"))
                using (process)
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, server, StringComparison.OrdinalIgnoreCase))
                            peak = Math.Max(peak, process.PrivateMemorySize64);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.Delay(1000);
        }
        return peak;
    }
}
