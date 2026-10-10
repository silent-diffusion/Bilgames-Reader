using System.IO.Compression;
using System.Text.Json;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Tests;

public sealed class StorageSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-store-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _store;

    public StorageSafetyTests() => _store = new LibraryStore(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private BookRecord SaveBook(string id, string title = "Test")
    {
        var book = new BookRecord { Id = id, Title = title, Author = "A. Writer", ImportedUtc = DateTime.UtcNow };
        book.Chapters.Add(new ChapterInfo { Index = 0, Title = "One", Kind = SectionKind.Body });
        _store.SaveBook(book);
        _store.SaveChapterText(id, new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body });
        _store.SaveCast(id, new CastSheet { Members = { new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = "bm_george" } } });
        File.WriteAllText(Path.Combine(_store.BookDir(id), "source.epub"), "epub bytes");
        return book;
    }

    // ---------- format version ----------

    [Fact]
    public void NewLibraryIsMarkedCurrent()
    {
        var check = LibraryFormat.Prepare(_store, "1.0.0");
        Assert.False(check.Upgraded);
        Assert.Equal(LibraryFormat.CurrentVersion, LibraryStore.Read<LibraryManifest>(Path.Combine(_root, "library.json"))!.FormatVersion);
    }

    [Fact]
    public void UnversionedLibraryIsFormatOne()
    {
        SaveBook("bk");
        var check = LibraryFormat.Prepare(_store, "1.0.0", 1, Array.Empty<Migration>());
        Assert.Equal(1, check.FoundVersion);
        Assert.Null(check.BackupPath);
    }

    [Fact]
    public void UpgradeBacksUpFirstThenMigratesEveryBook()
    {
        SaveBook("a"); SaveBook("b");
        LibraryFormat.Prepare(_store, "0.4.5", 1, Array.Empty<Migration>());
        var touched = new List<string>();
        var steps = new[] { new Migration(2, "test", Book: dir => touched.Add(Path.GetFileName(dir))) };

        var check = LibraryFormat.Prepare(_store, "1.0.0", 2, steps);

        Assert.True(check.Upgraded);
        Assert.True(File.Exists(check.BackupPath));
        Assert.Equal(new[] { "a", "b" }, touched.OrderBy(x => x));
        Assert.Equal(2, LibraryStore.Read<LibraryManifest>(Path.Combine(_root, "library.json"))!.FormatVersion);
        // Running again does nothing more.
        touched.Clear();
        Assert.False(LibraryFormat.Prepare(_store, "1.0.0", 2, steps).Upgraded);
        Assert.Empty(touched);
    }

    [Fact]
    public void LibraryFromNewerAppIsLeftAlone()
    {
        SaveBook("bk");
        LibraryStore.Write(Path.Combine(_root, "library.json"), new LibraryManifest { FormatVersion = 9, AppVersion = "3.0.0" });
        var check = LibraryFormat.Prepare(_store, "1.0.0", 1, Array.Empty<Migration>());
        Assert.True(check.NewerThanApp);
        Assert.Equal(9, LibraryStore.Read<LibraryManifest>(Path.Combine(_root, "library.json"))!.FormatVersion);
    }

    // ---------- forward compatibility ----------

    [Fact]
    public void FieldsFromNewerVersionsSurviveASave()
    {
        var path = Path.Combine(_root, "cast.json");
        File.WriteAllText(path, """{"Members":[{"Id":"x","Name":"Ann","FutureThing":{"a":1}}],"SomethingNew":[1,2,3]}""");
        var cast = LibraryStore.Read<CastSheet>(path)!;
        cast.Members[0].Name = "Anne";
        LibraryStore.Write(path, cast);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("[1,2,3]", doc.RootElement.GetProperty("SomethingNew").GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("Members")[0].GetProperty("FutureThing").GetProperty("a").GetInt32());
        Assert.Equal("Anne", doc.RootElement.GetProperty("Members")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void UnknownEnumValueReadsAsDefault()
    {
        var path = Path.Combine(_root, "book.json");
        File.WriteAllText(path, """{"Id":"bk","Title":"T","Quality":"Ultra","Analysis":"Done"}""");
        var book = LibraryStore.Read<BookRecord>(path)!;
        Assert.Equal(default, book.Quality);
        Assert.Equal("T", book.Title);
    }

    [Fact]
    public void DamagedFileIsCopiedAsideNotLost()
    {
        var path = Path.Combine(_root, "annotations.json");
        File.WriteAllText(path, "{ this is not json");
        string? reported = null;
        void OnUnreadable(string p, string _) => reported = p;
        LibraryStore.Unreadable += OnUnreadable;
        try { Assert.Null(LibraryStore.Read<AnnotationSet>(path)); }
        finally { LibraryStore.Unreadable -= OnUnreadable; }

        Assert.Equal(path, reported);
        var copy = Assert.Single(Directory.GetFiles(_root, "annotations.json.unreadable-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(copy));
    }

    // ---------- backup and restore ----------

    [Fact]
    public void BackupRestoresIntoEmptyLibrary()
    {
        SaveBook("bk", "Persuasion");
        _store.SaveAnnotations("bk", new AnnotationSet { Bookmarks = { new Bookmark { Id = "m1", ChapterIndex = 0, Seconds = 12 } } });
        var file = Path.Combine(_root, "out", "backup.zip");
        var result = LibraryBackup.Create(_store, file, new BackupOptions(IncludeAudio: false), "1.0.0");
        Assert.Equal(1, result.Books);

        var other = new LibraryStore(Path.Combine(_root, "other"));
        var info = LibraryBackup.Inspect(file);
        Assert.Equal("Persuasion", Assert.Single(info.Books).Title);
        var restored = LibraryBackup.Restore(other, file, replaceExisting: false);

        Assert.Equal(new RestoreResult(1, 0, 0), restored);
        Assert.Equal("Persuasion", other.LoadBook("bk")!.Title);
        Assert.Equal(12, Assert.Single(other.LoadAnnotations("bk").Bookmarks).Seconds);
        Assert.True(File.Exists(Path.Combine(other.BookDir("bk"), "source.epub")));
        Assert.Empty(Directory.GetDirectories(other.TempDir));
    }

    [Fact]
    public void BookBackedUpWhileGeneratingIsRestoredPaused()
    {
        var book = SaveBook("bk");
        book.Generation.State = JobState.Running;
        _store.SaveBook(book);
        var file = Path.Combine(_root, "out", "running.zip");
        LibraryBackup.Create(_store, file, new BackupOptions(false), "1.0.0");

        var other = new LibraryStore(Path.Combine(_root, "other"));
        LibraryBackup.Restore(other, file, replaceExisting: false);
        Assert.Equal(JobState.Paused, other.LoadBook("bk")!.Generation.State);
    }

    [Fact]
    public void ExistingBooksAreKeptUnlessReplaceIsChosen()
    {
        SaveBook("bk", "Old title");
        var file = Path.Combine(_root, "out", "b.zip");
        LibraryBackup.Create(_store, file, new BackupOptions(false), "1.0.0");
        var book = _store.LoadBook("bk")!;
        book.Title = "Edited since";
        _store.SaveBook(book);

        Assert.Equal(new RestoreResult(0, 0, 1), LibraryBackup.Restore(_store, file, replaceExisting: false));
        Assert.Equal("Edited since", _store.LoadBook("bk")!.Title);

        Assert.Equal(new RestoreResult(0, 1, 0), LibraryBackup.Restore(_store, file, replaceExisting: true));
        Assert.Equal("Old title", _store.LoadBook("bk")!.Title);
    }

    [Fact]
    public void AudioIsIncludedOnlyWhenAsked()
    {
        SaveBook("bk");
        var audio = new ChapterAudio { ChapterIndex = 0, FileName = "0-abcd1234.m4a", DurationSeconds = 3 };
        Directory.CreateDirectory(Path.Combine(_store.BookDir("bk"), "audio"));
        File.WriteAllText(_store.ChapterAudioPath("bk", audio), "aac");
        File.WriteAllText(Path.Combine(_store.BookDir("bk"), "audio", "0-old00000.m4a"), "old take");
        _store.SaveChapterAudio("bk", audio);

        var without = Path.Combine(_root, "out", "no-audio.zip");
        var with = Path.Combine(_root, "out", "audio.zip");
        LibraryBackup.Create(_store, without, new BackupOptions(false), "1.0.0");
        LibraryBackup.Create(_store, with, new BackupOptions(true), "1.0.0");

        using (var z = ZipFile.OpenRead(without)) Assert.DoesNotContain(z.Entries, e => e.FullName.Contains("/audio/"));
        using (var z = ZipFile.OpenRead(with))
        {
            var names = z.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("books/bk/audio/0.json", names);
            Assert.Contains("books/bk/audio/0-abcd1234.m4a", names);
            Assert.DoesNotContain("books/bk/audio/0-old00000.m4a", names); // only the current take
        }
    }

    [Fact]
    public void HostileEntriesAreIgnored()
    {
        SaveBook("bk");
        var file = Path.Combine(_root, "out", "evil.zip");
        LibraryBackup.Create(_store, file, new BackupOptions(false), "1.0.0");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            foreach (var name in new[] { "books/bk/../../escaped.txt", "../outside.txt", "books/bk/text/../../../x.json", "C:/abs.json", "books/bk/sub/deep/x.json" })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write("x");
        }
        var target = new LibraryStore(Path.Combine(_root, "t", "lib"));
        LibraryBackup.Restore(target, file, replaceExisting: false);

        Assert.False(File.Exists(Path.Combine(_root, "t", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "t", "outside.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "t", "lib", "x.json")));
        Assert.False(Directory.Exists(Path.Combine(target.BookDir("bk"), "sub")));
        Assert.NotNull(target.LoadBook("bk"));
    }

    [Fact]
    public void NotABackupIsRejectedClearly()
    {
        var file = Path.Combine(_root, "plain.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("hi");
        Assert.Throws<BackupFormatException>(() => LibraryBackup.Inspect(file));

        File.WriteAllText(file, "not a zip");
        Assert.Throws<BackupFormatException>(() => LibraryBackup.Inspect(file));
    }

    [Fact]
    public void UninstallRemovesOnlyWhatCanBeRebuilt()
    {
        SaveBook("bk");
        _store.SaveAnnotations("bk", new AnnotationSet { Bookmarks = { new Bookmark { ChapterIndex = 0 } } });
        Directory.CreateDirectory(Path.Combine(_store.BookDir("bk"), "segments"));
        File.WriteAllText(Path.Combine(_store.BookDir("bk"), "segments", "x.wav"), "cache");
        File.WriteAllText(Path.Combine(_store.ModelsDir, "kokoro.onnx"), "model");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        _store.SaveSettings(new AppSettings());

        LibraryStore.CleanForUninstall(_root);

        Assert.False(Directory.Exists(_store.ModelsDir));
        Assert.False(Directory.Exists(Path.Combine(_root, "logs")));
        Assert.False(Directory.Exists(Path.Combine(_store.BookDir("bk"), "segments")));
        Assert.NotNull(_store.LoadBook("bk"));
        Assert.Single(_store.LoadAnnotations("bk").Bookmarks);
        Assert.True(File.Exists(Path.Combine(_root, "settings.json")));
    }

    [Fact]
    public void BackupFromNewerAppIsRefused()
    {
        var file = Path.Combine(_root, "future.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("backup.json").Open()))
                w.Write($$"""{"Kind":"bilgames-reader-backup","FormatVersion":{{LibraryFormat.CurrentVersion + 1}},"AppVersion":"9.0"}""");
        var ex = Assert.Throws<BackupFormatException>(() => LibraryBackup.Inspect(file));
        Assert.Contains("newer version", ex.Message);
    }
}
