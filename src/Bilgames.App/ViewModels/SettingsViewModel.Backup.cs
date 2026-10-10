using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

/// <summary>Settings › Backup: save the whole library to one file, and bring it back.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BackupIdle))] private bool _isBackingUp;
    [ObservableProperty] private double _backupProgress;
    [ObservableProperty] private string _backupStatus = "";
    public bool BackupIdle => !IsBackingUp;

    private CancellationTokenSource? _backupCts;

    [RelayCommand]
    private void CancelBackup() => _backupCts?.Cancel();

    [RelayCommand]
    private async Task BackUpAsync()
    {
        if (IsBackingUp) return;
        var books = AppServices.Store.LoadAll();
        if (books.Count == 0)
        {
            await Dialogs.InfoAsync("Nothing to back up yet", "Import a book first; backups hold your books and everything you've done with them.");
            return;
        }
        var audioBytes = await Task.Run(() => books.Sum(b => AppServices.Store.SizeOf(b.Id, "audio")));
        var choice = await Dialogs.ChooseAsync("Back up your library",
            $"Saves your {books.Count} {(books.Count == 1 ? "book" : "books")} to one file: the original EPUBs, speakers, reviews, voices and tones, bookmarks, highlights, notes and listening positions."
            + (audioBytes > 0 ? $"\n\nYou can also include the generated audio ({Ui.Size(audioBytes)}), so it doesn't need generating again after a restore." : ""),
            "Back up", audioBytes > 0 ? "Back up with audio" : "");
        if (choice == 0) return;
        var withAudio = choice == 2;

        var dest = await Pickers.PickSaveAsync($"Bilgames Reader backup {DateTime.Now:yyyy-MM-dd}", ("Bilgames Reader backup", ".zip"));
        if (dest is null) return;

        await RunBackupTask("Backing up", async (progress, ct) =>
        {
            var result = await Task.Run(() => LibraryBackup.Create(AppServices.Store, dest, new BackupOptions(withAudio), AppServices.AppVersion, progress, ct), ct);
            BackupStatus = $"Backed up {result.Books} {(result.Books == 1 ? "book" : "books")} ({Ui.Size(result.Bytes)}) on {DateTime.Now:d MMM yyyy}.";
            await Dialogs.InfoAsync("Backup saved", $"“{Path.GetFileName(dest)}” · {Ui.Size(result.Bytes)}\n\n{Path.GetDirectoryName(dest)}");
        });
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBackingUp) return;
        var path = await Pickers.PickOpenAsync(".zip");
        if (path is null) return;

        BackupManifest manifest;
        try { manifest = await Task.Run(() => LibraryBackup.Inspect(path)); }
        catch (BackupFormatException ex) { await Dialogs.InfoAsync("Can't restore this file", ex.Message); return; }
        catch (Exception ex) { AppServices.Log("Backup inspect failed: " + ex); await Dialogs.InfoAsync("Can't restore this file", ex.Message); return; }
        if (manifest.Books.Count == 0) { await Dialogs.InfoAsync("Nothing to restore", "This backup has no books in it."); return; }

        var existing = manifest.Books.Where(b => Directory.Exists(AppServices.Store.BookDir(b.Id))).ToList();
        var made = manifest.CreatedUtc.ToLocalTime().ToString("d MMM yyyy");
        var summary = $"The backup from {made} has {manifest.Books.Count} {(manifest.Books.Count == 1 ? "book" : "books")}"
                      + (manifest.IncludesAudio ? ", with audio." : ". Audio isn't included, so it will need generating.");
        bool replace;
        if (existing.Count == 0)
        {
            if (!await Dialogs.ConfirmAsync("Restore backup?", summary + "\n\nThey'll be added to your library.", "Restore")) return;
            replace = false;
        }
        else
        {
            var names = string.Join(", ", existing.Take(3).Select(b => $"“{b.Title}”")) + (existing.Count > 3 ? $" and {existing.Count - 3} more" : "");
            var pick = await Dialogs.ChooseAsync("Restore backup?",
                $"{summary}\n\n{names} {(existing.Count == 1 ? "is" : "are")} already in your library. Replace {(existing.Count == 1 ? "it" : "them")} with the backup's copy (your current edits to {(existing.Count == 1 ? "it" : "them")} are saved first in the backups folder), or keep yours and add only the books you don't have?",
                "Replace with backup", existing.Count == manifest.Books.Count ? "" : "Add missing books only");
            if (pick == 0) return;
            replace = pick == 1;
        }

        var existingIds = existing.Select(b => b.Id).ToHashSet();
        await RunBackupTask("Restoring", async (progress, ct) =>
        {
            var result = await LibraryTransfer.RestoreAsync(_shell, path, replace, existingIds, progress, ct);
            var parts = new List<string>();
            if (result.Added > 0) parts.Add($"added {result.Added}");
            if (result.Replaced > 0) parts.Add($"replaced {result.Replaced}");
            if (result.Skipped > 0) parts.Add($"kept {result.Skipped} you already had");
            BackupStatus = $"Restored on {DateTime.Now:d MMM yyyy}: {string.Join(", ", parts)}.";
            await Dialogs.InfoAsync("Backup restored", char.ToUpper(BackupStatus[0]) + BackupStatus[1..]);
        });
    }

    private async Task RunBackupTask(string verb, Func<IProgress<double>, CancellationToken, Task> work)
    {
        IsBackingUp = true;
        BackupProgress = 0;
        BackupStatus = verb + "…";
        using var cts = _backupCts = new CancellationTokenSource();
        int shown = -1;
        var progress = new Inline(p =>
        {
            var whole = (int)Math.Clamp(p, 0, 100);
            if (Interlocked.Exchange(ref shown, whole) == whole) return;
            AppServices.RunOnUI(() => { if (IsBackingUp) { BackupProgress = whole; BackupStatus = $"{verb}… {whole}%"; } });
        });
        try
        {
            await work(progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            BackupStatus = verb == "Restoring" ? "Restore stopped. Books already restored were kept; nothing was left half-done." : "Backup stopped; no file was saved.";
        }
        catch (Exception ex)
        {
            AppServices.Log($"{verb} failed: {ex}");
            BackupStatus = $"{verb} failed: {ex.Message}";
            await Dialogs.InfoAsync(verb == "Restoring" ? "Restore failed" : "Backup failed", ex.Message);
        }
        finally
        {
            _backupCts = null;
            IsBackingUp = false;
        }
    }

    private sealed class Inline(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
