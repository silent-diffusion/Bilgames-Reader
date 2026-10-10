using Bilgames.App.Services;
using Bilgames.Core.Audio;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public sealed partial class PlayerViewModel
{
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_book is null || IsExporting) return;
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

        IsExporting = true;
        var book = _book;
        var chapters = _ready.ToList();
        try
        {
            await Task.Run(() => Export(book, chapters, dest, p => AppServices.RunOnUI(() => ExportLabel = $"Exporting {p:0}%")));
            ExportLabel = "Export";
            await Dialogs.InfoAsync("Audiobook exported", $"Saved “{Path.GetFileName(dest)}” with {chapters.Count} chapter markers.\n\n{Path.GetDirectoryName(dest)}");
        }
        catch (Exception ex)
        {
            AppServices.Log("Export failed: " + ex);
            await Dialogs.InfoAsync("Export failed", ex.Message);
        }
        finally
        {
            IsExporting = false;
            ExportLabel = "Export";
        }
    }

    private static async Task Export(BookRecord book, List<int> chapters, string dest, Action<double> progress)
    {
        var store = AppServices.Store;
        var tmp = Path.Combine(store.TempDir, $"export-{book.Id}");
        Directory.CreateDirectory(tmp);
        var bigWav = Path.Combine(tmp, "book.wav");
        var m4a = Path.Combine(tmp, "book.m4a");
        var marks = new List<Mp4Chapter>();
        try
        {
            using (var cat = new WavConcatenator(bigWav, AppServices.Engine.SampleRate))
            {
                for (int i = 0; i < chapters.Count; i++)
                {
                    var chWav = Path.Combine(tmp, $"{chapters[i]}.wav");
                    await AppServices.Encoder.DecodeToWavAsync(store.ChapterAudioPath(book.Id, chapters[i]), chWav, AppServices.Engine.SampleRate, CancellationToken.None);
                    marks.Add(new Mp4Chapter(TimeSpan.FromSeconds(cat.PositionSeconds), book.Chapters[chapters[i]].Title));
                    cat.AppendFile(chWav);
                    File.Delete(chWav);
                    progress(70.0 * (i + 1) / chapters.Count);
                }
            }
            await AppServices.Encoder.EncodeAacAsync(bigWav, m4a, CancellationToken.None);
            progress(95);
            Mp4Metadata.Write(m4a, marks, book.Title, book.Author);
            File.Move(m4a, dest, overwrite: true);
            progress(100);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
