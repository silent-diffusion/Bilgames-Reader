using Bilgames.Core.Export;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Tests;

public sealed class CastEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-cast-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _store;
    private readonly BookService _books;

    public CastEditTests()
    {
        _store = new LibraryStore(_root);
        _books = new BookService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A one-chapter book where Henry and Rinaldi talk, analyzed.</summary>
    private string AnalyzedBook()
    {
        var paragraphs = new[]
        {
            "The evening was cold.",
            "“We should go,” said Henry.",
            "“Not yet,” said Rinaldi.",
            "“The trucks are leaving,” said Henry.",
            "“Let them leave,” said Rinaldi.",
            "“Someone is at the door,” said Henry.",
        };
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        for (int i = 0; i < paragraphs.Length; i++) text.Blocks.Add(new Block { Id = $"c0b{i}", Kind = BlockKind.Paragraph, Text = paragraphs[i] });
        var book = new BookRecord { Id = "bk", Title = "Test", Author = "A. Writer" };
        book.Chapters.Add(new ChapterInfo { Index = 0, Title = "One", Kind = SectionKind.Body, WordCount = text.WordCount });
        Directory.CreateDirectory(Path.Combine(_store.BookDir("bk"), "text"));
        _store.SaveChapterText("bk", text);
        _store.SaveBook(book);
        _books.Analyze("bk");
        return "bk";
    }

    private CastMember Named(string id, string name) => _store.LoadCast(id).Members.Single(m => m.Name == name);

    [Fact]
    public void MergeMovesLinesAndRemembersTheName()
    {
        var id = AnalyzedBook();
        var henry = Named(id, "Henry");
        var rinaldi = Named(id, "Rinaldi");
        _books.Merge(id, rinaldi.Id, henry.Id);

        var cast = _store.LoadCast(id);
        Assert.Null(cast.Find(rinaldi.Id));
        Assert.Contains("Rinaldi", cast.Find(henry.Id)!.Aliases);
        Assert.Equal(5, cast.Find(henry.Id)!.LineCount);
        Assert.DoesNotContain(_store.LoadAnalysis(id, 0)!.Lines, l => l.SpeakerId == rinaldi.Id);

        // Re-analysis merges them again instead of bringing Rinaldi back.
        _books.Analyze(id);
        cast = _store.LoadCast(id);
        Assert.DoesNotContain(cast.Members, m => m.Name == "Rinaldi");
        Assert.Equal(5, cast.Members.Single(m => m.Name == "Henry").LineCount);
    }

    [Fact]
    public void AddedSpeakersGetAVoiceAndSurviveReanalysis()
    {
        var id = AnalyzedBook();
        var ferguson = _books.AddSpeaker(id, "  Ferguson ", Gender.Female);
        Assert.Equal("Ferguson", ferguson.Name);
        Assert.True(ferguson.UserAdded);
        Assert.Equal(Gender.Female, Bilgames.Core.Tts.VoiceCatalog.Get(ferguson.VoiceId).Gender);
        Assert.DoesNotContain(_store.LoadCast(id).Members, m => m.Id != ferguson.Id && m.VoiceId == ferguson.VoiceId);

        // Adding the same name again returns the existing speaker.
        Assert.Equal(ferguson.Id, _books.AddSpeaker(id, "ferguson").Id);

        // Give her the last line, then re-analyze: the speaker and the correction are both kept.
        var line = _store.LoadAnalysis(id, 0)!.Lines.Last(l => l.IsDialogue);
        _books.AssignSpeaker(id, 0, line.Id, ferguson.Id, false);
        _books.Analyze(id);
        var cast = _store.LoadCast(id);
        Assert.NotNull(cast.Find(ferguson.Id));
        Assert.Equal(1, cast.Find(ferguson.Id)!.LineCount);
        Assert.Equal(ferguson.Id, _store.LoadAnalysis(id, 0)!.Lines.Single(l => l.Id == line.Id).SpeakerId);
    }

    [Fact]
    public void MergeManyFoldsEveryoneIntoOne()
    {
        var id = AnalyzedBook();
        var extra = _books.AddSpeaker(id, "Ettore");
        var henry = Named(id, "Henry");
        _books.MergeMany(id, new[] { Named(id, "Rinaldi").Id, extra.Id }, henry.Id);
        var cast = _store.LoadCast(id);
        Assert.Equal(new[] { "Henry" }, cast.Members.Where(m => !m.IsNarrator).Select(m => m.Name));
        Assert.Contains("Ettore", cast.Find(henry.Id)!.Aliases);
    }
}

public class AnnotationExportTests
{
    private static (BookRecord, AnnotationSet) Sample()
    {
        var book = new BookRecord { Id = "bk", Title = "A Farewell", Author = "E. H." };
        book.Chapters.Add(new ChapterInfo { Index = 0, Title = "Book One" });
        book.Chapters.Add(new ChapterInfo { Index = 1, Title = "Chapter 2" });
        var set = new AnnotationSet();
        set.Highlights.Add(new Highlight { ChapterIndex = 1, Seconds = 75, Text = "The rain fell, \"hard\".", Color = HighlightColor.Green, Note = "=SUM(A1)" });
        set.Bookmarks.Add(new Bookmark { ChapterIndex = 1, Seconds = 3725, Excerpt = "Later on." });
        set.Bookmarks.Add(new Bookmark { ChapterIndex = 0, Seconds = 12, Excerpt = "In the late summer.", Note = "start here" });
        return (book, set);
    }

    [Fact]
    public void MarkdownIsGroupedByChapterInReadingOrder()
    {
        var (book, set) = Sample();
        var md = AnnotationExport.Write(book, set, AnnotationFormat.Markdown, new DateTime(2026, 10, 9));
        Assert.StartsWith("# A Farewell", md);
        Assert.True(md.IndexOf("## Book One") < md.IndexOf("## Chapter 2"));
        Assert.Contains("- **Bookmark** at 0:12 — “In the late summer.”", md);
        Assert.Contains("> The rain fell, \"hard\".", md);
        Assert.Contains("> — 1:15 · green highlight", md);
        Assert.True(md.IndexOf("1:15 · green") < md.IndexOf("at 1:02:05")); // highlight at 1:15 before bookmark at 1:02:05
    }

    [Fact]
    public void CsvQuotesAndDefusesFormulas()
    {
        var (book, set) = Sample();
        var csv = AnnotationExport.Write(book, set, AnnotationFormat.Csv).Split(Environment.NewLine);
        Assert.Equal("Type,Chapter,Time,Seconds,Text,Note,Color,Created", csv[0]);
        Assert.StartsWith("Bookmark,Book One,0:12,12,In the late summer.,start here,,", csv[1]);
        Assert.StartsWith("Highlight,Chapter 2,1:15,75,\"The rain fell, \"\"hard\"\".\",'=SUM(A1),green,", csv[2]);
    }

    [Theory]
    [InlineData("notes.md", AnnotationFormat.Markdown)]
    [InlineData("notes.TXT", AnnotationFormat.PlainText)]
    [InlineData("notes.csv", AnnotationFormat.Csv)]
    public void FormatFollowsTheExtension(string path, AnnotationFormat format) => Assert.Equal(format, AnnotationExport.FormatFor(path));
}

public class ApostropheTests
{
    [Theory]
    [InlineData("Jim’s hat", "Jim's hat")]
    [InlineData("I don’t know, it’s five o’clock.", "I don't know, it's five o'clock.")]
    [InlineData("the boys’ house", "the boys' house")]
    [InlineData("Tell ’em, rock ’n’ roll.", "Tell 'em, rock 'n' roll.")]
    [InlineData("‘Hello,’ he said. ‘It’s late.’", "‘Hello,’ he said. ‘It's late.’")] // quotation marks stay
    [InlineData("Jimʼs and Jim‘s", "Jim's and Jim's")]
    public void TypesetApostrophesBecomePlain(string input, string expected) =>
        Assert.Equal(expected, Bilgames.Core.Planning.SynthesisPlanner.Apostrophes(input));
}
