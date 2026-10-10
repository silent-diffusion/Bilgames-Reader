using Bilgames.App.Services;
using Bilgames.Core.Audio;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public sealed partial class PlayerViewModel
{
    // Concurrent so the button stays live during an export; selecting it again offers to stop.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExportAsync()
    {
        if (IsExporting)
        {
            if (await Dialogs.ConfirmAsync("Stop exporting?", "The audiobook file won’t be saved. Your generated chapters stay as they are.", "Stop export"))
                _exportCts?.Cancel();
            return;
        }
        if (_book is null) return;
        RefreshReady();
        var included = _book.IncludedChapters.ToList();
        if (_ready.Count == 0)
        {
            await Dialogs.InfoAsync("Nothing to export yet", "Generate audio first; you can export once chapters are ready.");
            return;
        }
        if (_ready.Count < included.Count)
        {
            var go = await Dialogs.ConfirmAsync("Export ready chapters?",
                $"{_ready.Count} of {included.Count} chapters are ready. Export those now as an audiobook, or wait until generation finishes.",
                $"Export {_ready.Count} chapters");
            if (!go) return;
        }
        var safe = string.Concat(_book.Title.Split(Path.GetInvalidFileNameChars()));
        var dest = await Pickers.PickSaveAsync(safe, "Audiobook with chapters", ".m4b");
        if (dest is null) return;

        ExportPercent = 0;
        ExportLabel = "0%";
        IsExporting = true;
        var book = _book;
        var chapters = _ready.ToList();
        using var cts = _exportCts = new CancellationTokenSource();
        int shown = -1;
        void Report(double p)
        {
            // Only hop to the UI thread when the whole-number percentage goes up; Media Foundation
            // reports from its own threads, so a late, lower value is dropped rather than shown.
            var whole = (int)Math.Clamp(p, 0, 100);
            int seen;
            do { seen = Volatile.Read(ref shown); if (whole <= seen) return; }
            while (Interlocked.CompareExchange(ref shown, whole, seen) != seen);
            AppServices.RunOnUI(() => { ExportPercent = whole; ExportLabel = $"{whole}%"; });
        }
        try
        {
            await Task.Run(() => Export(book, chapters, dest, Report, cts.Token));
            ExportPercent = 100;
            await Dialogs.InfoAsync("Audiobook exported", $"Saved “{Path.GetFileName(dest)}” with {chapters.Count} chapter markers.\n\n{Path.GetDirectoryName(dest)}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            RemovePlaceholder(dest);
        }
        catch (Exception ex)
        {
            RemovePlaceholder(dest);
            AppServices.Log("Export failed: " + ex);
            await Dialogs.InfoAsync("Export failed", ex.Message);
        }
        finally
        {
            _exportCts = null;
            IsExporting = false;
            ExportPercent = 0;
            ExportLabel = "Export";
        }
    }

    private static async Task Export(BookRecord book, List<int> chapters, string dest, Action<double> progress, CancellationToken ct)
    {
        var store = AppServices.Store;
        var tmp = Path.Combine(store.TempDir, $"export-{book.Id}");
        Directory.CreateDirectory(tmp);
        var bigWav = Path.Combine(tmp, "book.wav");
        var m4a = Path.Combine(tmp, "book.m4a");
        var marks = new List<Mp4Chapter>();
        // Unpacking the chapters and the final encode take about as long as each other, so each
        // gets half the bar, and each chapter its share by length.
        const double DecodeShare = 50, EncodeShare = 48;
        var audios = chapters.Select(c => store.LoadChapterAudio(book.Id, c)).ToList();
        var total = Math.Max(1, audios.Sum(a => Math.Max(1, a?.DurationSeconds ?? 1)));
        double before = 0;
        try
        {
            using (var cat = new WavConcatenator(bigWav, AppServices.Engine.SampleRate))
            {
                for (int i = 0; i < chapters.Count; i++)
                {
                    var chWav = Path.Combine(tmp, $"{chapters[i]}.wav");
                    ct.ThrowIfCancellationRequested();
                    var audio = audios[i] ?? throw new FileNotFoundException($"Chapter {chapters[i] + 1} audio is missing.");
                    var weight = Math.Max(1, audio.DurationSeconds);
                    var start = before;
                    await AppServices.Encoder.DecodeToWavAsync(store.ChapterAudioPath(book.Id, audio), chWav, AppServices.Engine.SampleRate, ct,
                        new Inline(p => progress(DecodeShare * (start + weight * p / 100) / total)));
                    before += weight;
                    marks.Add(new Mp4Chapter(TimeSpan.FromSeconds(cat.PositionSeconds), book.Chapters[chapters[i]].Title));
                    cat.AppendFile(chWav);
                    File.Delete(chWav);
                    progress(DecodeShare * before / total);
                }
            }
            await AppServices.Encoder.EncodeAacAsync(bigWav, m4a, ct, new Inline(p => progress(DecodeShare + EncodeShare * p / 100)));
            progress(DecodeShare + EncodeShare);
            ct.ThrowIfCancellationRequested();
            Mp4Metadata.Write(m4a, marks, book.Title, book.Author);
            File.Move(m4a, dest, overwrite: true);
            progress(100);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    /// <summary>The save picker creates an empty file; don’t leave it behind when nothing was written.</summary>
    private static void RemovePlaceholder(string dest)
    {
        try { if (new FileInfo(dest) is { Exists: true, Length: 0 }) File.Delete(dest); } catch { }
    }

    /// <summary>Reports straight through on the worker thread (Progress&lt;T&gt; would queue and reorder).</summary>
    private sealed class Inline(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
