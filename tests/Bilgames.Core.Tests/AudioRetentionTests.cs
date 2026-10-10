using Bilgames.Core.Jobs;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Tests;

public sealed class AudioRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-keep-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _store;

    public AudioRetentionTests() => _store = new LibraryStore(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private BookRecord Book(int chapters, int at)
    {
        var book = new BookRecord { Id = "bk", Title = "T", Position = new PlaybackPosition { ChapterIndex = at } };
        for (int i = 0; i < chapters; i++) book.Chapters.Add(new ChapterInfo { Index = i, Title = $"{i}", Kind = SectionKind.Body });
        _store.SaveBook(book);
        Directory.CreateDirectory(Path.Combine(_store.BookDir("bk"), "audio"));
        for (int i = 0; i < chapters; i++)
        {
            var audio = new ChapterAudio { ChapterIndex = i, FileName = $"{i}-take.m4a" };
            File.WriteAllText(_store.ChapterAudioPath("bk", audio), "aac");
            _store.SaveChapterAudio("bk", audio);
        }
        return book;
    }

    [Fact]
    public void WindowFollowsTheListener()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, AudioRetention.Wanted(Book(10, 5), KeepAudio.All));
        Assert.Equal(new[] { 4, 5, 6, 7, 8 }, AudioRetention.Wanted(Book(10, 5), KeepAudio.Recent));
        Assert.Equal(new[] { 0, 1, 2, 3 }, AudioRetention.Wanted(Book(10, 0), KeepAudio.Recent));
        Assert.Equal(new[] { 8, 9 }, AudioRetention.Wanted(Book(10, 9), KeepAudio.Recent));
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, AudioRetention.Wanted(Book(10, 5), KeepAudio.UntilListened));
    }

    [Fact]
    public void EvictionRemovesOnlyAudioAndRemembersIt()
    {
        Book(10, 5);
        _store.SaveAnnotations("bk", new AnnotationSet { Bookmarks = { new Bookmark { ChapterIndex = 0 } } });

        Assert.Equal(0, AudioRetention.Evict(_store, "bk", KeepAudio.All));
        Assert.Equal(4, AudioRetention.Evict(_store, "bk", KeepAudio.Recent, playing: 9)); // 0–3 go; 9 is playing

        var book = _store.LoadBook("bk")!;
        Assert.Equal(new[] { 0, 1, 2, 3 }, book.Generation.Removed.OrderBy(i => i));
        Assert.Null(_store.LoadChapterAudio("bk", 0));
        Assert.False(File.Exists(Path.Combine(_store.BookDir("bk"), "audio", "0-take.m4a")));
        Assert.NotNull(_store.LoadChapterAudio("bk", 9));
        Assert.Single(_store.LoadAnnotations("bk").Bookmarks);
    }
}
