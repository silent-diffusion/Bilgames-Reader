using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Bilgames.Assist.Llama;

public readonly record struct DownloadProgress(string Stage, long Done, long Total);

/// <summary>
/// Installs the assistant: llama.cpp's server and the model, under {data}/assistant. Only ever runs
/// after the user clicks Download. Downloads resume after an interruption, every file is checked
/// against its published SHA-256 before use, and the llama.cpp archive is unpacked defensively.
/// </summary>
public sealed class AssistantDownloader
{
    private readonly string _root;

    public AssistantDownloader(string assistantRoot) => _root = assistantRoot;

    public string ModelPath => Path.Combine(_root, "models", AssistantCatalog.Model.FileName);
    public string RuntimeDir => Path.Combine(_root, "runtime", AssistantCatalog.RuntimeBuild);
    public string ServerExe => Path.Combine(RuntimeDir, "llama-server.exe");
    private string ModelVerifiedMark => ModelPath + ".verified";

    public bool RuntimeInstalled => File.Exists(ServerExe);
    /// <summary>Installed and checked: the checksum is verified once after downloading, not at every start.</summary>
    public bool ModelInstalled => File.Exists(ModelPath) && File.Exists(ModelVerifiedMark);
    public bool Installed => RuntimeInstalled && ModelInstalled;

    /// <summary>Bytes of a partly downloaded model (so the UI can offer to resume).</summary>
    public long PartialModelBytes => File.Exists(ModelPath + ".part") ? new FileInfo(ModelPath + ".part").Length : 0;

    public async Task InstallAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BilgamesReader/1.0");

        if (!RuntimeInstalled)
        {
            var zip = Path.Combine(_root, "downloads", AssistantCatalog.Runtime.FileName);
            await DownloadAsync(http, AssistantCatalog.Runtime, zip, exactSize: false, progress, ct);
            Unpack(zip, RuntimeDir);
            File.Delete(zip);
        }
        if (!ModelInstalled)
        {
            var spec = AssistantCatalog.Model;
            EnsureSpace(Path.GetDirectoryName(ModelPath)!, spec.Size - PartialModelBytes);
            await DownloadAsync(http, spec, ModelPath, exactSize: true, progress, ct);
            await File.WriteAllTextAsync(ModelVerifiedMark, spec.Sha256, ct);
        }
    }

    /// <summary>Removes the model (and any partial download); keeps nothing else of it.</summary>
    public void DeleteModel()
    {
        foreach (var f in new[] { ModelPath, ModelPath + ".part", ModelVerifiedMark })
            if (File.Exists(f)) File.Delete(f);
    }

    public void DeleteAll()
    {
        DeleteModel();
        if (Directory.Exists(Path.Combine(_root, "runtime"))) Directory.Delete(Path.Combine(_root, "runtime"), true);
        if (Directory.Exists(Path.Combine(_root, "downloads"))) Directory.Delete(Path.Combine(_root, "downloads"), true);
    }

    private static async Task DownloadAsync(HttpClient http, DownloadSpec spec, string final, bool exactSize, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var part = final + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (exactSize && existing > spec.Size) { File.Delete(part); existing = 0; }

        using (var request = new HttpRequestMessage(HttpMethod.Get, spec.Url))
        {
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                // The partial file is already complete; fall through to the check.
            }
            else
            {
                response.EnsureSuccessStatusCode();
                bool resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resumed) existing = 0;
                long total = exactSize ? spec.Size : (response.Content.Headers.ContentLength ?? 0) + existing;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                long done = existing;
                var last = DateTime.MinValue;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (exactSize && done > spec.Size) throw new InvalidDataException($"{spec.Label}: the download is larger than expected; it was discarded.");
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { progress?.Report(new($"Downloading {spec.Label}", done, total)); last = DateTime.UtcNow; }
                }
            }
        }

        var size = new FileInfo(part).Length;
        if (exactSize && size != spec.Size)
            throw new InvalidDataException($"{spec.Label}: the download ended early ({size / 1048576:N0} of {spec.Size / 1048576:N0} MB). Try again to resume.");
        progress?.Report(new($"Checking {spec.Label}", 0, size));
        if (!await HashMatchesAsync(part, spec.Sha256, size, progress, spec.Label, ct))
        {
            File.Delete(part);
            throw new InvalidDataException($"{spec.Label} didn't match its published checksum, so it was deleted. Try downloading again.");
        }
        File.Move(part, final, overwrite: true);
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, long size, IProgress<DownloadProgress>? progress, string label, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 22];
        long done = 0;
        var last = DateTime.MinValue;
        int n;
        while ((n = await file.ReadAsync(buffer, ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, n, null, 0);
            done += n;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { progress?.Report(new($"Checking {label}", done, size)); last = DateTime.UtcNow; }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unpacks the llama.cpp archive, refusing any entry that would land outside the folder.</summary>
    private static void Unpack(string zip, string target)
    {
        var temp = target + ".unpacking";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        var root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // a folder
                var dest = Path.GetFullPath(Path.Combine(temp, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The llama.cpp archive contains an unsafe path.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        // Some builds put everything in a top-level folder; the server is what we need.
        var exe = Directory.EnumerateFiles(temp, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException("The llama.cpp archive has no llama-server.exe.");
        var source = Path.GetDirectoryName(exe)!;
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }

    private static void EnsureSpace(string dir, long needed)
    {
        Directory.CreateDirectory(dir);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
        if (free < needed + 1L * 1024 * 1024 * 1024)
            throw new IOException($"Not enough disk space: the model needs {needed / 1e9:N1} GB and {free / 1e9:N1} GB is free.");
    }
}
