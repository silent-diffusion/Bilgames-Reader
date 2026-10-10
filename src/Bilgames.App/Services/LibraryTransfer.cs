using Bilgames.App.Controls;
using Bilgames.App.ViewModels;
using Bilgames.Core.Storage;

namespace Bilgames.App.Services;

/// <summary>
/// Moving books in and out of the library: one book as a .bilgames package (the unit for taking a
/// book to another PC or device), or the whole library as a backup. Both are the same format.
/// </summary>
public static class LibraryTransfer
{
    /// <summary>Exports one book to a .bilgames file, optionally with its generated audio.</summary>
    public static async Task ExportBookAsync(string bookId)
    {
        var book = AppServices.Store.LoadBook(bookId);
        if (book is null) return;
        var audioBytes = await Task.Run(() => AppServices.Store.SizeOf(bookId, "audio"));
        var choice = await Dialogs.ChooseAsync("Export book file",
            $"Saves “{book.Title}” as one .bilgames file, with its speakers, reviews, voices, bookmarks, highlights, notes and where you were, to open on another PC or device."
            + (audioBytes > 0 ? $"\n\nIncluding the generated audio ({Ui.Size(audioBytes)}) means it plays straight away there." : ""),
            "Export", audioBytes > 0 ? "Export with audio" : "");
        if (choice == 0) return;
        var withAudio = choice == 2;
        var safe = string.Concat(book.Title.Split(Path.GetInvalidFileNameChars()));
        var dest = await Pickers.PickSaveAsync(safe, ("Bilgames Reader book", LibraryBackup.PackageExtension));
        if (dest is null) return;

        BackupResult? result = null;
        try
        {
            var done = await Dialogs.RunWithProgressAsync("Exporting book", $"Saving “{book.Title}”…", (progress, ct) =>
            {
                result = LibraryBackup.Create(AppServices.Store, dest, new BackupOptions(withAudio), AppServices.AppVersion, progress, ct, new[] { bookId });
                return Task.CompletedTask;
            });
            if (done && result is not null)
                await Dialogs.InfoAsync("Book exported", $"“{Path.GetFileName(dest)}” · {Ui.Size(result.Bytes)}\n\n{Path.GetDirectoryName(dest)}");
        }
        catch (Exception ex)
        {
            AppServices.Log("Book export failed: " + ex);
            await Dialogs.InfoAsync("Export failed", ex.Message);
        }
    }

    /// <summary>Opens a .bilgames file (or a backup) from Import or a double-click: adds its books, asking before replacing any.</summary>
    public static async Task ImportPackageAsync(ShellViewModel shell, string path)
    {
        BackupManifest manifest;
        try { manifest = await Task.Run(() => LibraryBackup.Inspect(path)); }
        catch (Exception ex) { await Dialogs.InfoAsync("Can't open this file", ex is BackupFormatException ? ex.Message : "Something went wrong reading it: " + ex.Message); return; }
        if (manifest.Books.Count == 0) { await Dialogs.InfoAsync("Nothing to import", "This file has no books in it."); return; }

        var existing = manifest.Books.Where(b => Directory.Exists(AppServices.Store.BookDir(b.Id))).ToList();
        bool replace = false;
        if (existing.Count > 0)
        {
            var one = manifest.Books.Count == 1;
            var title = one ? $"“{existing[0].Title}” is already in your library" : $"{existing.Count} of these books are already in your library";
            var pick = await Dialogs.ChooseAsync(title,
                (one ? "Replace it with the copy in this file? " : "Replace them with the copies in this file? ")
                + "Your current edits are saved first in the backups folder, and audio that still matches is kept, so only what changed needs generating.",
                "Replace", existing.Count == manifest.Books.Count ? "" : "Add the others only");
            if (pick == 0) return;
            replace = pick == 1;
        }

        RestoreResult? result = null;
        try
        {
            var name = manifest.Books.Count == 1 ? $"“{manifest.Books[0].Title}”" : $"{manifest.Books.Count} books";
            var done = await Dialogs.RunWithProgressAsync("Importing", $"Adding {name}…", async (progress, ct) =>
                result = await RestoreAsync(shell, path, replace, existing.Select(b => b.Id).ToHashSet(), progress, ct));
            if (!done || result is null) return;
            if (manifest.Books.Count == 1 && result.Added + result.Replaced == 1)
                AppServices.RunOnUI(() => shell.OpenBook(manifest.Books[0].Id, BookTab.Structure));
        }
        catch (Exception ex)
        {
            AppServices.Log("Package import failed: " + ex);
            await Dialogs.InfoAsync("Import failed", ex.Message);
        }
    }

    /// <summary>
    /// Restores books from a package or backup. Books about to be replaced are first paused and
    /// closed in the player (their folders are swapped), and a backup of today's edits is kept.
    /// Call from the UI thread; the heavy work runs in the background.
    /// </summary>
    public static async Task<RestoreResult> RestoreAsync(ShellViewModel shell, string path, bool replace, IReadOnlySet<string> existingIds,
        IProgress<double>? progress, CancellationToken ct)
    {
        var replacing = replace ? existingIds : new HashSet<string>();
        string? reopenPlayer = null;
        await OnUI(() =>
        {
            reopenPlayer = shell.Player.BookId is { } p && replacing.Contains(p) ? p : null;
            if (reopenPlayer is not null) shell.Player.Unload();
            foreach (var id in replacing) AppServices.Jobs.Pause(id);
        });

        var result = await Task.Run(() =>
        {
            if (replacing.Count > 0)
            {
                var safety = Path.Combine(AppServices.Store.BackupsDir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                LibraryBackup.Create(AppServices.Store, safety, new BackupOptions(IncludeAudio: false, IncludeSources: false), AppServices.AppVersion, null, ct, replacing);
            }
            return LibraryBackup.Restore(AppServices.Store, path, replace, null, progress, ct);
        }, ct);

        await OnUI(() =>
        {
            shell.Library.Load();
            shell.Notes.Refresh();
            if (shell.Book.BookId is { } open && replacing.Contains(open)) shell.Book.Load(open);
            if (reopenPlayer is not null) shell.Player.Load(reopenPlayer, autoplay: false);
        });
        return result;
    }

    private static Task OnUI(Action a)
    {
        var done = new TaskCompletionSource();
        AppServices.RunOnUI(() => { try { a(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } });
        return done.Task;
    }
}
