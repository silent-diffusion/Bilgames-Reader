using Bilgames.Core.Model;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Tests;

public sealed class FirstChapterFirstTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-first-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (LibraryStore, BookService) Library(string sub)
    {
        var store = new LibraryStore(Path.Combine(_root, sub));
        var book = new BookRecord { Id = "bk", Title = "Two talkers" };
        for (int c = 0; c < 3; c++)
        {
            var text = new ChapterText { Index = c, Title = $"Chapter {c + 1}", Kind = SectionKind.Body };
            text.Blocks.Add(new Block { Id = $"c{c}b0", Kind = BlockKind.Paragraph, Text = "“We should go,” said Henry." });
            text.Blocks.Add(new Block { Id = $"c{c}b1", Kind = BlockKind.Paragraph, Text = c == 2 ? "“Not yet,” said Catherine." : "“Not yet,” said Rinaldi." });
            book.Chapters.Add(new ChapterInfo { Index = c, Title = text.Title, Kind = SectionKind.Body, WordCount = text.WordCount });
            Directory.CreateDirectory(Path.Combine(store.BookDir("bk"), "text"));
            store.SaveChapterText("bk", text);
        }
        store.SaveBook(book);
        return (store, new BookService(store));
    }

    [Fact]
    public void FirstChapterThenRestMatchesOneFullAnalysis()
    {
        var (store, books) = Library("staged");
        var first = books.Analyze("bk", firstChapterOnly: true);
        Assert.True(first.AnalyzingRest);
        Assert.Equal(AnalysisState.Done, first.Analysis); // usable (Cast, Review, listening) straight away
        Assert.NotNull(store.LoadAnalysis("bk", 0));
        Assert.Null(store.LoadAnalysis("bk", 2));
        Assert.DoesNotContain(store.LoadCast("bk").Members, m => m.Name == "Catherine");

        var rest = books.Analyze("bk");
        Assert.False(rest.AnalyzingRest);

        var (direct, directBooks) = Library("direct");
        directBooks.Analyze("bk");
        for (int c = 0; c < 3; c++)
            Assert.Equal(Speakers(direct, c), Speakers(store, c));
        Assert.Equal(direct.LoadCast("bk").Members.Select(m => m.Name).OrderBy(n => n), store.LoadCast("bk").Members.Select(m => m.Name).OrderBy(n => n));
    }

    private static IEnumerable<string> Speakers(LibraryStore store, int chapter)
    {
        var cast = store.LoadCast("bk");
        return store.LoadAnalysis("bk", chapter)!.Lines.Select(l => $"{l.Id}:{cast.Find(l.SpeakerId)?.Name}");
    }
}
