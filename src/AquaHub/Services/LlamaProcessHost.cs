using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Util;
using AquaHub.Platform;
using Microsoft.Win32.SafeHandles;

namespace AquaHub.Services;

/// <summary>
/// Starts Aqua's model servers: hidden, inside a Windows job that ends them when Aqua quits or crashes, and with a clean
/// environment — no LLAMA_* or GGML_* settings inherited from the PC (one could switch on something Aqua keeps off, such as
/// file-reading tools), only the key the runtime passes. A llama-server laid out as Ollama ships it (its CUDA libraries in a
/// "cuda_v12" or "cuda_v13" folder beside it) gets that folder on its PATH, so a custom server path to it works too.
/// </summary>
public sealed class LlamaProcessHost : ILlamaProcessHost, IDisposable
{
    private readonly SafeFileHandle? _job = Native.CreateKillOnCloseJob();
    private readonly object _lifetime = new();
    private bool _blocked;
    private Process? _unconfirmed;
    public bool CanReleaseGpu { get { lock (_lifetime) return _unconfirmed is null || _unconfirmed.HasExited; } }

    public LlamaProcessHost()
    {
        if (_job is null) Log.Warn("llama", "Windows couldn't create model-process containment. Model starts are disabled.");
    }

    public ILlamaProcess Start(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string workingDirectory, Action<string> output)
    {
        if (_job is null || _job.IsClosed || _job.IsInvalid)
            throw new LlamaRuntimeException("Windows couldn't create safe process containment. Restart Aqua before loading a model.");
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        // ProcessStartInfo starts with the full parent environment. Keep only the Windows variables the native process
        // loader and temp-file APIs need; model/server configuration must come from this host, not the user's shell.
        var inherited = info.Environment;
        var systemRoot = inherited.TryGetValue("SystemRoot", out var root) ? root : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = inherited.TryGetValue("TEMP", out var inheritedTemp) ? inheritedTemp : Path.GetTempPath();
        info.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            info.Environment["SystemRoot"] = systemRoot;
            info.Environment["WINDIR"] = systemRoot;
        }
        info.Environment["TEMP"] = temp;
        info.Environment["TMP"] = temp;
        if (!string.IsNullOrWhiteSpace(systemRoot))
            info.Environment["PATH"] = Path.Combine(systemRoot, "System32") + Path.PathSeparator + systemRoot;
        foreach (var (key, value) in environment) info.Environment[key] = value;
        var dir = Path.GetDirectoryName(program)!;
        if (!File.Exists(Path.Combine(dir, "ggml-cuda.dll")) && new[] { "cuda_v12", "cuda_v13" }.Select(v => Path.Combine(dir, v)).FirstOrDefault(Directory.Exists) is { } cuda)
        {
            info.Environment["PATH"] = cuda + Path.PathSeparator + dir + Path.PathSeparator + (info.Environment.TryGetValue("PATH", out var path) ? path : "");
            info.WorkingDirectory = cuda;
        }

        Process process;
        lock (_lifetime)
        {
            if (_job.IsClosed || _blocked) throw new LlamaRuntimeException("Aqua's model-process host is closing or Windows refused containment. Restart Aqua before loading a model.");
            process = Process.Start(info) ?? throw new InvalidOperationException("llama-server didn't start.");
            try
            {
                if (!Native.AssignProcessToJobObject(_job, process.Handle))
                    throw new LlamaRuntimeException($"Windows couldn't contain Aqua's model server (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}). Restart Aqua before trying again.");
            }
            catch
            {
                _blocked = true;
                try
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                }
                finally
                {
                    if (!process.HasExited) _unconfirmed = process;
                    else process.Dispose();
                }
                throw;
            }
        }
        try
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Sandbox.Record("llama", "start · " + (arguments.SkipWhile(a => a != "-a").Skip(1).FirstOrDefault() ?? string.Join(' ', arguments)));
            return new Handle(process);
        }
        catch
        {
            lock (_lifetime)
            {
                try { using var failed = new Handle(process); }
                catch { _blocked = true; _unconfirmed = process; throw; }
            }
            throw;
        }
    }

    public int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            _job?.Dispose();
            if (_unconfirmed is not { } process) return;
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
            catch (Exception ex) { Log.Error("llama", "Couldn't stop Aqua's unconfirmed model process on exit", ex); }
            finally { process.Dispose(); _unconfirmed = null; }
        }
    }

    private sealed class Handle(Process process) : ILlamaProcess
    {
        public int Id => process.Id;

        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public void Kill()
        {
            if (HasExited) return;
            try
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new LlamaRuntimeException("Aqua's model process did not stop. GPU handoff is blocked; restart Aqua before retrying.");
            }
            catch (InvalidOperationException) when (HasExited) { }
        }

        public void Dispose() { Kill(); process.Dispose(); }
    }
}
