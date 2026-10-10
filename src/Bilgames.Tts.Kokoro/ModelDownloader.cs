using System.Net.Http.Headers;

namespace Bilgames.Tts.Kokoro;

/// <summary>
/// Downloads a voice model file. This is one of the only places the app touches the network, and
/// it is only ever called after the user confirms in the UI. Partial downloads resume.
/// </summary>
public static class ModelDownloader
{
    public static async Task DownloadAsync(ModelInfo model, string modelsDir, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modelsDir);
        var final = Path.Combine(modelsDir, model.FileName);
        var part = final + ".part";

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BilgamesReader/1.0");

        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing > model.Size) { File.Delete(part); existing = 0; }

        using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        bool resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = existing;
            int n;
            var last = DateTime.MinValue;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (done > model.Size) throw new InvalidDataException("The download is larger than expected; it was discarded.");
                if ((DateTime.UtcNow - last).TotalMilliseconds > 150)
                {
                    progress?.Report((done, model.Size));
                    last = DateTime.UtcNow;
                }
            }
            progress?.Report((done, model.Size));
        }

        var size = new FileInfo(part).Length;
        if (size != model.Size)
            throw new InvalidDataException($"The download ended early ({size / 1048576} of {model.Size / 1048576} MB). Try again to resume.");
        File.Move(part, final, overwrite: true);
    }

    public static void Delete(ModelInfo model, string modelsDir)
    {
        foreach (var f in new[] { Path.Combine(modelsDir, model.FileName), Path.Combine(modelsDir, model.FileName + ".part") })
            if (File.Exists(f)) File.Delete(f);
    }
}
