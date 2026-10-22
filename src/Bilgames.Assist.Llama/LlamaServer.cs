using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bilgames.Core.Assist;

namespace Bilgames.Assist.Llama;

/// <summary>A graphics device llama.cpp can run on.</summary>
public sealed record LlamaDevice(string Id, string Name, long TotalMiB, long FreeMiB);

/// <summary>
/// Runs llama.cpp's server as a child process for the assistant. It listens only on 127.0.0.1, on a
/// free port, and requires a random key made for this session (so nothing else on the PC, not even a
/// web page, can use it); its built-in web UI is off. Running it out-of-process means a model too
/// big for memory can't take the app down, and llama.cpp can be updated on its own.
/// <c>--fit on</c> lets llama.cpp put as much as fits on the graphics card and keep the rest
/// (mostly the experts of the mixture-of-experts model) in system RAM.
/// </summary>
public sealed partial class LlamaServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly LinkedList<string> _tail = new();

    public Uri Address { get; }
    public string ApiKey { get; }
    public bool HasExited => _process.HasExited;

    private LlamaServer(Process process, Uri address, string apiKey)
    {
        _process = process;
        Address = address;
        ApiKey = apiKey;
    }

    /// <summary>The last lines the server printed (for error messages and the log).</summary>
    public string Tail { get { lock (_tail) return string.Join("\n", _tail); } }

    public static async Task<IReadOnlyList<LlamaDevice>> ListDevicesAsync(string serverExe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(serverExe, "--list-devices")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new AssistantUnavailableException("Couldn't start llama.cpp.");
        var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return DeviceLine().Matches(output).Select(m => new LlamaDevice(m.Groups["id"].Value, m.Groups["name"].Value.Trim(),
            long.Parse(m.Groups["total"].Value), long.Parse(m.Groups["free"].Value))).ToList();
    }

    [GeneratedRegex(@"^\s*(?<id>[A-Za-z]+\d+):\s*(?<name>.+?)\s*\((?<total>\d+) MiB, (?<free>\d+) MiB free\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    /// <summary>
    /// Picks the device to run on: the one named in settings, else the one matching the graphics card
    /// Windows reports with the most dedicated memory (integrated graphics report shared RAM as their
    /// own, so llama.cpp's own figures would favour them). Null means llama.cpp's default.
    /// </summary>
    public static LlamaDevice? ChooseDevice(IReadOnlyList<LlamaDevice> devices, string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) return null;
        return devices.FirstOrDefault(d => d.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(d => d.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase)
                                              || preferredName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<LlamaServer> StartAsync(string serverExe, string modelPath, LlamaDevice? device, int contextSize, string? logFile, CancellationToken ct)
    {
        int port = FreePort();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var args = new List<string>
        {
            "-m", modelPath, "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", key, "--no-webui",
            "--fit", "on", "-c", contextSize.ToString(), "--reasoning", "off", "-np", "1",
        };
        if (device is not null) { args.Add("-dev"); args.Add(device.Id); }
        var psi = new ProcessStartInfo(serverExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi) ?? throw new AssistantUnavailableException("Couldn't start llama.cpp.");
        ChildProcessGuard.Attach(process); // ends with the app, however the app ends
        var server = new LlamaServer(process, new Uri($"http://127.0.0.1:{port}/"), key);
        StreamWriter? log = null;
        if (logFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (server._tail)
            {
                server._tail.AddLast(e.Data);
                while (server._tail.Count > 40) server._tail.RemoveFirst();
                try { log?.WriteLine(e.Data); } catch { }
            }
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Loading 20+ GB from disk takes a while the first time; wait until it answers /health.
        using var http = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new AssistantUnavailableException(Explain(server.Tail));
            try
            {
                using var r = await http.GetAsync("health", ct);
                if (r.StatusCode == HttpStatusCode.OK) return server;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            if (DateTime.UtcNow > deadline)
            {
                await server.DisposeAsync();
                throw new AssistantUnavailableException("The assistant's model took too long to load.");
            }
            await Task.Delay(500, ct);
        }
    }

    /// <summary>A plain-language reason the server stopped, from what it printed last.</summary>
    public static string Explain(string tail)
    {
        if (Regex.IsMatch(tail, "out of memory|failed to allocate|ErrorOutOfDeviceMemory|bad_alloc", RegexOptions.IgnoreCase))
            return "The assistant's model didn't fit in this PC's memory. Close other programs and try again.";
        if (Regex.IsMatch(tail, "unknown model architecture|failed to load model", RegexOptions.IgnoreCase))
            return "llama.cpp couldn't load the assistant's model. It may be damaged; delete it in Settings and download it again.";
        var last = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return "The assistant stopped unexpectedly." + (last.Length > 0 ? $" ({last.Trim()})" : "");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch { }
        _process.Dispose();
    }
}
