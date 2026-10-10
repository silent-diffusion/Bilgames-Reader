using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bilgames.Core.Storage;

/// <summary>library.json: which version of the on-disk format the library is in.</summary>
public sealed class LibraryManifest
{
    public int FormatVersion { get; set; }
    /// <summary>The app version that last upgraded or created the library (for diagnostics).</summary>
    public string? AppVersion { get; set; }
    public DateTime UpdatedUtc { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A step from one format version to the next, applied to each book folder and/or the library root.</summary>
public sealed record Migration(int ToVersion, string Description, Action<string>? Book = null, Action<string>? Library = null);

public sealed record FormatCheck(int FoundVersion, int CurrentVersion, string? BackupPath)
{
    /// <summary>The library was written by a newer app; editing here is safe (unknown fields are kept) but an update is advised.</summary>
    public bool NewerThanApp => FoundVersion > CurrentVersion;
    public bool Upgraded => FoundVersion < CurrentVersion;
}

/// <summary>
/// Keeps saved data safe across app updates. The library carries a format version; when an update
/// changes the format, every step is applied in order, after first saving a backup of all edits
/// (backups/pre-upgrade-*.zip) so an upgrade can always be undone. Version 1 is the format of
/// 0.4.x, which had no version marker.
/// </summary>
public static class LibraryFormat
{
    public const int CurrentVersion = 1;
    private const string ManifestName = "library.json";
    private const int KeepUpgradeBackups = 3;

    /// <summary>Upgrade steps, oldest first. Each one must be safe to re-run on a book it already upgraded.</summary>
    public static readonly IReadOnlyList<Migration> Migrations = Array.Empty<Migration>();

    public static FormatCheck Prepare(LibraryStore store, string appVersion) =>
        Prepare(store, appVersion, CurrentVersion, Migrations);

    /// <summary>Brings the library up to <paramref name="current"/>. Never touches a library from a newer version.</summary>
    public static FormatCheck Prepare(LibraryStore store, string appVersion, int current, IReadOnlyList<Migration> migrations)
    {
        var path = Path.Combine(store.Root, ManifestName);
        var manifest = LibraryStore.Read<LibraryManifest>(path);
        // No marker: either a brand-new library (nothing to upgrade) or one from before versioning (format 1).
        var found = manifest?.FormatVersion ?? (Directory.EnumerateDirectories(store.BooksDir).Any() ? 1 : current);

        if (found > current) return new FormatCheck(found, current, null);

        string? backup = null;
        if (found < current)
        {
            backup = Path.Combine(store.BackupsDir, $"pre-upgrade-v{found}-to-v{current}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            LibraryBackup.Create(store, backup, new BackupOptions(IncludeAudio: false, IncludeSources: false), appVersion);
            Prune(store.BackupsDir, "pre-upgrade-*.zip", KeepUpgradeBackups);

            foreach (var step in migrations.Where(m => m.ToVersion > found && m.ToVersion <= current).OrderBy(m => m.ToVersion))
            {
                step.Library?.Invoke(store.Root);
                if (step.Book is not null)
                    foreach (var dir in Directory.EnumerateDirectories(store.BooksDir)) step.Book(dir);
            }
        }

        if (manifest is null || found < current || manifest.AppVersion != appVersion)
        {
            manifest ??= new LibraryManifest();
            manifest.FormatVersion = current;
            manifest.AppVersion = appVersion;
            manifest.UpdatedUtc = DateTime.UtcNow;
            LibraryStore.Write(path, manifest);
        }
        return new FormatCheck(found, current, backup);
    }

    /// <summary>Applies the book-level steps between two versions to one book folder (used when restoring an older backup).</summary>
    public static void UpgradeBook(string bookDir, int fromVersion) =>
        UpgradeBook(bookDir, fromVersion, CurrentVersion, Migrations);

    public static void UpgradeBook(string bookDir, int fromVersion, int current, IReadOnlyList<Migration> migrations)
    {
        foreach (var step in migrations.Where(m => m.ToVersion > fromVersion && m.ToVersion <= current).OrderBy(m => m.ToVersion))
            step.Book?.Invoke(bookDir);
    }

    internal static void Prune(string dir, string pattern, int keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(dir).EnumerateFiles(pattern).OrderByDescending(f => f.CreationTimeUtc).Skip(keep))
                old.Delete();
        }
        catch { }
    }
}
