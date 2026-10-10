using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bilgames.Core.Model;

namespace Bilgames.Core.Storage;

public sealed record BackupOptions(bool IncludeAudio, bool IncludeSources = true);

public sealed class BackupBook
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
}

/// <summary>backup.json, the first entry of every backup.</summary>
public sealed class BackupManifest
{
    public const string KindName = "bilgames-reader-backup";

    public string Kind { get; set; } = KindName;
    public int FormatVersion { get; set; }
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public bool IncludesAudio { get; set; }
    public bool IncludesSources { get; set; }
    public List<BackupBook> Books { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record BackupResult(int Books, long Bytes);

public sealed record RestoreResult(int Added, int Replaced, int Skipped);

public class BackupFormatException : Exception
{
    public BackupFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// One-file backups of the library: every book's edits (cast, reviews, tones, bookmarks, highlights,
/// notes, listening position), its text and analysis, optionally the original EPUBs and generated
/// audio. The segment cache and voice models are never included; they can be rebuilt or re-downloaded.
///
/// Restoring treats the file as untrusted: only the expected file names are read, sizes are capped
/// while decompressing, each book is unpacked and checked in a scratch folder first, and only then
/// swapped in, so a bad or interrupted restore never leaves a half-written book.
/// </summary>
public static class LibraryBackup
{
    private const string ManifestEntry = "backup.json";
    private const long MaxJsonBytes = 64L * 1024 * 1024;
    private const long MaxMediaBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxEntries = 500_000;

    private static readonly Regex BookId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex BookEntry = new(
        @"^books/(?<id>[A-Za-z0-9_-]{1,64})/(?<rel>(?:(?:text|analysis|audio)/)?[A-Za-z0-9_-][A-Za-z0-9._-]{0,127})$",
        RegexOptions.CultureInvariant);

    // ---------- create ----------

    /// <summary>File extension of a single-book package (the same format, holding one book).</summary>
    public const string PackageExtension = ".bilgames";

    /// <summary>
    /// Writes a backup of the library, or with <paramref name="onlyBookIds"/> a package of some books
    /// (one book is a <see cref="PackageExtension"/> file, the unit for moving a book to another device).
    /// </summary>
    public static BackupResult Create(LibraryStore store, string destination, BackupOptions options, string appVersion,
        IProgress<double>? progress = null, CancellationToken ct = default, IReadOnlyCollection<string>? onlyBookIds = null)
    {
        var books = store.LoadAll().Where(b => onlyBookIds is null || onlyBookIds.Contains(b.Id)).ToList();
        var files = new List<(string Source, string Entry, bool Compress)>();
        foreach (var book in books)
            files.AddRange(FilesFor(store, book, options));

        long total = Math.Max(1, files.Sum(f => SafeLength(f.Source))), done = 0;
        var manifest = new BackupManifest
        {
            FormatVersion = LibraryFormat.CurrentVersion,
            AppVersion = appVersion,
            CreatedUtc = DateTime.UtcNow,
            IncludesAudio = options.IncludeAudio,
            IncludesSources = options.IncludeSources,
            Books = books.Select(b => new BackupBook { Id = b.Id, Title = b.Title, Author = b.Author }).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var tmp = destination + ".partial";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                using (var s = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal).Open())
                    JsonSerializer.Serialize(s, manifest, LibraryStore.Json);

                var buffer = new byte[1 << 16];
                foreach (var (source, entry, compress) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    using var input = OpenShared(source);
                    if (input is null) continue; // removed meanwhile (e.g. an old audio take swept)
                    using var output = zip.CreateEntry(entry, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open();
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n);
                        done += n;
                        progress?.Report(Math.Min(99.0, 100.0 * done / total));
                    }
                }
            }
            File.Move(tmp, destination, overwrite: true);
            progress?.Report(100);
            return new BackupResult(books.Count, new FileInfo(destination).Length);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static IEnumerable<(string, string, bool)> FilesFor(LibraryStore store, BookRecord book, BackupOptions options)
    {
        var dir = store.BookDir(book.Id);
        var prefix = $"books/{book.Id}/";
        foreach (var name in new[] { "book.json", "cast.json", "annotations.json" })
            if (File.Exists(Path.Combine(dir, name))) yield return (Path.Combine(dir, name), prefix + name, true);
        if (book.CoverFile is { } cover && IsPlainName(cover) && File.Exists(Path.Combine(dir, cover)))
            yield return (Path.Combine(dir, cover), prefix + cover, false);
        if (options.IncludeSources && File.Exists(Path.Combine(dir, "source.epub")))
            yield return (Path.Combine(dir, "source.epub"), prefix + "source.epub", false);
        foreach (var sub in new[] { "text", "analysis" })
            foreach (var f in JsonFiles(Path.Combine(dir, sub)))
                yield return (f, $"{prefix}{sub}/{Path.GetFileName(f)}", true);
        if (!options.IncludeAudio) yield break;
        foreach (var f in JsonFiles(Path.Combine(dir, "audio")))
        {
            // Only the current take of each chapter: its timing index and the audio file it names.
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out var index)) continue;
            var audio = store.LoadChapterAudio(book.Id, index);
            if (audio is null) continue;
            var media = store.ChapterAudioPath(book.Id, audio);
            if (!File.Exists(media) || !IsPlainName(Path.GetFileName(media))) continue;
            yield return (f, $"{prefix}audio/{Path.GetFileName(f)}", true);
            yield return (media, $"{prefix}audio/{Path.GetFileName(media)}", false);
        }
    }

    private static IEnumerable<string> JsonFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Where(f => IsPlainName(Path.GetFileName(f))) : Enumerable.Empty<string>();

    private static bool IsPlainName(string name) => Regex.IsMatch(name, "^[A-Za-z0-9_-][A-Za-z0-9._-]{0,127}$");

    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    private static FileStream? OpenShared(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // ---------- inspect ----------

    /// <summary>Reads a backup's description without unpacking it. Throws <see cref="BackupFormatException"/> if it isn't one.</summary>
    public static BackupManifest Inspect(string path)
    {
        using var zip = OpenZip(path);
        return ReadManifest(zip);
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > MaxEntries) { zip.Dispose(); throw new BackupFormatException("This file has too many entries to be a Bilgames Reader backup."); }
            return zip;
        }
        catch (InvalidDataException ex) { throw new BackupFormatException("This file isn't a Bilgames Reader backup (it isn't a zip file).", ex); }
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new BackupFormatException("This file isn't a Bilgames Reader backup.");
        BackupManifest? m;
        try
        {
            using var s = LimitedStream(entry, MaxJsonBytes);
            m = JsonSerializer.Deserialize<BackupManifest>(s, LibraryStore.Json);
        }
        catch (JsonException ex) { throw new BackupFormatException("This backup's description is damaged.", ex); }
        if (m is null || m.Kind != BackupManifest.KindName) throw new BackupFormatException("This file isn't a Bilgames Reader backup.");
        if (m.FormatVersion > LibraryFormat.CurrentVersion)
            throw new BackupFormatException($"This backup was made by a newer version of Bilgames Reader ({m.AppVersion}). Update the app, then restore it.");
        m.Books = m.Books.Where(b => BookId.IsMatch(b.Id)).GroupBy(b => b.Id).Select(g => g.First()).ToList();
        return m;
    }

    // ---------- restore ----------

    /// <summary>
    /// Restores books from a backup. Books not in the library are added; books already here are
    /// replaced only when <paramref name="replaceExisting"/> is true, otherwise left alone.
    /// <paramref name="onlyIds"/> limits the restore to some books. The caller must make sure the
    /// books being replaced aren't generating or playing.
    /// </summary>
    public static RestoreResult Restore(LibraryStore store, string path, bool replaceExisting, IReadOnlySet<string>? onlyIds = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var zip = OpenZip(path);
        var manifest = ReadManifest(zip);
        var wanted = manifest.Books.Where(b => onlyIds is null || onlyIds.Contains(b.Id)).Select(b => b.Id).ToHashSet();

        var byBook = new Dictionary<string, List<(ZipArchiveEntry Entry, string Rel)>>();
        foreach (var e in zip.Entries)
        {
            var m = BookEntry.Match(e.FullName.Replace('\\', '/'));
            if (!m.Success || !wanted.Contains(m.Groups["id"].Value)) continue;
            var rel = m.Groups["rel"].Value;
            if (rel.Contains("..")) continue;
            (byBook.TryGetValue(m.Groups["id"].Value, out var list) ? list : byBook[m.Groups["id"].Value] = new()).Add((e, rel));
        }

        long total = Math.Max(1, byBook.Values.SelectMany(l => l).Sum(x => x.Entry.Length)), done = 0;
        int added = 0, replaced = 0, skipped = 0;
        var scratchRoot = Path.Combine(store.TempDir, "restore-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var (id, entries) in byBook)
            {
                ct.ThrowIfCancellationRequested();
                var exists = Directory.Exists(store.BookDir(id));
                if (exists && !replaceExisting) { skipped++; done += entries.Sum(x => x.Entry.Length); continue; }

                var scratch = Path.Combine(scratchRoot, id);
                foreach (var (entry, rel) in entries)
                {
                    var target = Path.GetFullPath(Path.Combine(scratch, rel));
                    if (!target.StartsWith(Path.GetFullPath(scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var cap = rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? MaxJsonBytes : MaxMediaBytes;
                    using (var input = LimitedStream(entry, cap))
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    done += entry.Length;
                    progress?.Report(Math.Min(99.0, 100.0 * done / total));
                }

                // The unpacked book must read back as itself before it goes anywhere near the library.
                var record = LibraryStore.Read<BookRecord>(Path.Combine(scratch, "book.json"));
                if (record is null || record.Id != id) { skipped++; continue; }
                // A book backed up mid-generation isn't generating here: it waits to be resumed.
                if (record.Generation.State is JobState.Running or JobState.Queued
                    || (record.Generation.State == JobState.Done && !manifest.IncludesAudio))
                {
                    record.Generation.State = record.Generation.State == JobState.Done ? JobState.Idle : JobState.Paused;
                    LibraryStore.Write(Path.Combine(scratch, "book.json"), record);
                }
                LibraryFormat.UpgradeBook(scratch, manifest.FormatVersion);

                lock (store.EditLock(id))
                {
                    var live = store.BookDir(id);
                    string? aside = null;
                    if (Directory.Exists(live))
                    {
                        aside = Path.Combine(scratchRoot, "replaced-" + id);
                        Directory.Move(live, aside);
                    }
                    try { Directory.Move(scratch, live); }
                    catch
                    {
                        if (aside is not null && !Directory.Exists(live)) Directory.Move(aside, live);
                        throw;
                    }
                    if (aside is not null) KeepReusableAudio(aside, live, manifest.IncludesAudio);
                }
                if (exists) replaced++; else added++;
            }
            progress?.Report(100);
            return new RestoreResult(added, replaced, skipped);
        }
        finally
        {
            try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The replaced copy's segment cache always carries over (segments are named by the hash of what
    /// they say, so any that match the restored plan are reused and the rest are just unused cache),
    /// and so does its finished audio when the restored copy has none: each chapter records the plan
    /// it was made from, so a chapter whose plan still matches plays at once and the others regenerate.
    /// </summary>
    private static void KeepReusableAudio(string replaced, string live, bool restoredHasAudio)
    {
        foreach (var sub in restoredHasAudio ? new[] { "segments" } : new[] { "segments", "audio" })
        {
            var from = Path.Combine(replaced, sub);
            var to = Path.Combine(live, sub);
            try { if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to); } catch { }
        }
    }

    /// <summary>Decompresses an entry, failing if it turns out bigger than it claims or than the cap.</summary>
    private static Stream LimitedStream(ZipArchiveEntry entry, long cap)
    {
        if (entry.Length > cap) throw new BackupFormatException($"“{entry.Name}” in the backup is unexpectedly large.");
        return new CappedStream(entry.Open(), Math.Min(cap, entry.Length));
    }

    private sealed class CappedStream(Stream inner, long cap) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > cap) throw new BackupFormatException("A file in the backup is larger than it claims; the backup may be damaged.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
