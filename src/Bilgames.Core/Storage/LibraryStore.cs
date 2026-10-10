using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bilgames.Core.Epub;
using Bilgames.Core.Model;

namespace Bilgames.Core.Storage;

public enum DeleteMode
{
    /// <summary>Remove generated audio and caches; keep the book, its analysis and voice choices.</summary>
    AudioOnly,
    /// <summary>Remove the book and everything derived from it.</summary>
    Everything,
}

/// <summary>
/// Everything lives under one local folder (default %LOCALAPPDATA%\BilgamesReader):
/// <code>
/// settings.json
/// models/                      voice model files
/// books/{id}/book.json         BookRecord
/// books/{id}/source.epub       private copy of the imported file
/// books/{id}/cover.*           cover image, if the EPUB has one
/// books/{id}/text/{n}.json     ChapterText, one per chapter
/// books/{id}/analysis/{n}.json ChapterAnalysis
/// books/{id}/cast.json         CastSheet
/// books/{id}/annotations.json  bookmarks and highlights
/// books/{id}/segments/{h}.wav  synthesized segment cache (content-addressed)
/// books/{id}/audio/{n}.m4a     assembled chapter audio + {n}.json timing index
/// </code>
/// JSON writes are atomic (temp file + rename) so a crash never leaves a half-written file.
/// </summary>
public sealed class LibraryStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, object> _locks = new();

    public string Root { get; }
    public string BooksDir => Path.Combine(Root, "books");
    public string ModelsDir => Path.Combine(Root, "models");
    public string TempDir => Path.Combine(Root, "tmp");

    public LibraryStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(BooksDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(TempDir);
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilgamesReader");

    public string BookDir(string id) => Path.Combine(BooksDir, id);
    public string SegmentPath(string id, string hash) => Path.Combine(BookDir(id), "segments", hash + ".wav");
    public string ChapterAudioPath(string id, int index) => Path.Combine(BookDir(id), "audio", $"{index}.m4a");
    public string? CoverPath(BookRecord b) => b.CoverFile is null ? null : Path.Combine(BookDir(b.Id), b.CoverFile);

    private object LockFor(string id) => _locks.GetOrAdd(id, _ => new object());

    // ---------- settings ----------

    public AppSettings LoadSettings() => Read<AppSettings>(Path.Combine(Root, "settings.json")) ?? new AppSettings();
    public void SaveSettings(AppSettings s) => Write(Path.Combine(Root, "settings.json"), s);

    // ---------- books ----------

    public List<BookRecord> LoadAll()
    {
        var list = new List<BookRecord>();
        foreach (var dir in Directory.EnumerateDirectories(BooksDir))
        {
            try
            {
                if (Read<BookRecord>(Path.Combine(dir, "book.json")) is { } b) list.Add(b);
            }
            catch (JsonException) { /* a corrupt record shouldn't hide the rest of the library */ }
        }
        return list.OrderByDescending(b => b.LastPlayedUtc ?? b.ImportedUtc).ToList();
    }

    public BookRecord? LoadBook(string id) => Read<BookRecord>(Path.Combine(BookDir(id), "book.json"));

    public void SaveBook(BookRecord book)
    {
        lock (LockFor(book.Id)) Write(Path.Combine(BookDir(book.Id), "book.json"), book);
    }

    /// <summary>Parses an EPUB and adds it to the library. The original file is copied, never modified.</summary>
    public BookRecord Import(string epubPath, string narratorVoice, CancellationToken ct = default)
    {
        var imported = EpubImporter.Import(epubPath, ct);
        var id = Guid.NewGuid().ToString("N")[..12];
        var dir = BookDir(id);
        Directory.CreateDirectory(Path.Combine(dir, "text"));
        try
        {
            File.Copy(epubPath, Path.Combine(dir, "source.epub"));
            string? coverFile = null;
            if (imported.Cover is { Length: > 0 } cover)
            {
                coverFile = "cover" + imported.CoverExtension;
                File.WriteAllBytes(Path.Combine(dir, coverFile), cover);
            }
            foreach (var ch in imported.Chapters) SaveChapterText(id, ch);

            var book = new BookRecord
            {
                Id = id,
                Title = imported.Title,
                Author = imported.Author,
                Language = imported.Language,
                CoverFile = coverFile,
                CoverColor = CoverPalette.For(imported.Title),
                SourceFileName = Path.GetFileName(epubPath),
                ImportedUtc = DateTime.UtcNow,
                Chapters = imported.Chapters.Select(c => new ChapterInfo
                {
                    Index = c.Index, Title = c.Title, Kind = c.Kind, WordCount = c.WordCount, NoteCount = c.Notes.Count,
                }).ToList(),
            };
            // Front matter, prefaces and back matter start excluded; the user opts in on the Structure tab.
            SaveBook(book);
            var cast = new CastSheet();
            cast.Members.Add(new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = narratorVoice, Color = Tts.VoiceCatalog.NarratorColor });
            SaveCast(id, cast);
            return book;
        }
        catch
        {
            TryDeleteDir(dir);
            throw;
        }
    }

    public void Delete(string id, DeleteMode mode)
    {
        lock (LockFor(id))
        {
            var dir = BookDir(id);
            if (mode == DeleteMode.Everything)
            {
                TryDeleteDir(dir);
                return;
            }
            TryDeleteDir(Path.Combine(dir, "audio"));
            TryDeleteDir(Path.Combine(dir, "segments"));
        }
    }

    /// <summary>Bookmarks and highlights. Kept when only the audio is deleted.</summary>
    public AnnotationSet LoadAnnotations(string id) => Read<AnnotationSet>(Path.Combine(BookDir(id), "annotations.json")) ?? new AnnotationSet();

    public void SaveAnnotations(string id, AnnotationSet set)
    {
        lock (LockFor(id)) Write(Path.Combine(BookDir(id), "annotations.json"), set);
    }

    public void ClearSegmentCache(string id)
    {
        lock (LockFor(id)) TryDeleteDir(Path.Combine(BookDir(id), "segments"));
    }

    // ---------- per-chapter data ----------

    public ChapterText LoadChapterText(string id, int index) =>
        Read<ChapterText>(Path.Combine(BookDir(id), "text", $"{index}.json"))
        ?? throw new FileNotFoundException($"Chapter {index} text is missing.");

    public void SaveChapterText(string id, ChapterText ch) => Write(Path.Combine(BookDir(id), "text", $"{ch.Index}.json"), ch);

    public ChapterAnalysis? LoadAnalysis(string id, int index) => Read<ChapterAnalysis>(Path.Combine(BookDir(id), "analysis", $"{index}.json"));

    public void SaveAnalysis(string id, ChapterAnalysis a) => Write(Path.Combine(BookDir(id), "analysis", $"{a.ChapterIndex}.json"), a);

    public CastSheet LoadCast(string id) => Read<CastSheet>(Path.Combine(BookDir(id), "cast.json")) ?? new CastSheet();

    public void SaveCast(string id, CastSheet cast)
    {
        lock (LockFor(id)) Write(Path.Combine(BookDir(id), "cast.json"), cast);
    }

    public ChapterAudio? LoadChapterAudio(string id, int index)
    {
        var a = Read<ChapterAudio>(Path.Combine(BookDir(id), "audio", $"{index}.json"));
        return a is not null && File.Exists(ChapterAudioPath(id, index)) ? a : null;
    }

    public void SaveChapterAudio(string id, ChapterAudio a) => Write(Path.Combine(BookDir(id), "audio", $"{a.ChapterIndex}.json"), a);

    // ---------- sizes ----------

    public long SizeOf(string id, string? sub = null)
    {
        var dir = sub is null ? BookDir(id) : Path.Combine(BookDir(id), sub);
        return DirSize(dir);
    }

    public long ModelsSize() => DirSize(ModelsDir);

    public long AudioSize() => Directory.Exists(BooksDir)
        ? Directory.EnumerateDirectories(BooksDir).Sum(d => DirSize(Path.Combine(d, "audio")) + DirSize(Path.Combine(d, "segments")))
        : 0;

    /// <summary>
    /// Total size of a folder. Sizes come from the directory listing itself (no per-file stat), so
    /// thousands of cached segments take milliseconds. Still disk I/O: never call it on the UI thread.
    /// </summary>
    public static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options)) total += f.Length;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    // ---------- io helpers ----------

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(s, Json);
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var s = File.Create(tmp)) JsonSerializer.Serialize(s, value, Json);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Typographic cover colors from the design (used when an EPUB has no cover image).</summary>
public static class CoverPalette
{
    private static readonly string[] Colors = { "#2B4A5E", "#3B4C3A", "#5E3B46", "#3A3550", "#4A2626", "#4B4330", "#2F4B4B", "#4A3A5E" };

    public static string For(string title)
    {
        int h = 17;
        foreach (var c in title) h = unchecked(h * 31 + c);
        return Colors[Math.Abs(h % Colors.Length)];
    }
}
