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

public class ToneTests
{
    [Theory]
    [InlineData("“GET OUT OF MY HOUSE!”", "", "shouting")]
    [InlineData("“No!”", "", "excited")]
    [InlineData("“You did this.”", "Asha glared at him.", "angry")]
    [InlineData("“That’s wonderful!”", "Wirr grinned.", "excited")]
    [InlineData("“I-I don’t know.”", "", "hesitant")]
    [InlineData("“Well… I suppose… maybe.”", "", "hesitant")]
    [InlineData("“Come here.”", "He lowered his voice.", "whispered")]
    [InlineData("“I miss her.”", "Tears ran down her face.", "sad")]
    [InlineData("“The trucks are leaving.”", "He looked out of the window.", null)]
    public void GuessesToneFromContext(string quote, string narration, string? expected) =>
        Assert.Equal(expected, Bilgames.Core.Analysis.Tones.FromContext(quote, narration));

    [Theory]
    [InlineData("coldly", "stern")]
    [InlineData("angrily", "angry")]
    [InlineData("gently", "soft")]
    [InlineData("impatiently", "urgent")]
    [InlineData("thoughtfully", null)]
    public void TagAdverbsMapOntoTones(string adverb, string? expected) =>
        Assert.Equal(expected, Bilgames.Core.Analysis.SpeechTags.CueFor("said", adverb));

    [Fact]
    public void OldSavedCuesStillMap()
    {
        Assert.Equal("stern", Bilgames.Core.Analysis.Tones.Normalize("sharp"));
        Assert.Equal("urgent", Bilgames.Core.Analysis.Tones.Normalize("impatient"));
        Assert.Null(Bilgames.Core.Analysis.Tones.Normalize("thoughtful"));
    }

    [Fact]
    public void ToneChangesSpeedLoudnessAndPausesInThePlan()
    {
        var book = new BookRecord { Id = "bk" };
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        text.Blocks.Add(new Block { Id = "b0", Kind = BlockKind.Paragraph, Text = "x" });
        var analysis = new ChapterAnalysis { ChapterIndex = 0 };
        analysis.Lines.Add(new Line { Id = "b0l0", BlockId = "b0", Text = "He leaned in.", Source = AttributionSource.Narration });
        analysis.Lines.Add(new Line { Id = "b0l1", BlockId = "b0", Text = "“Quiet now.”", IsDialogue = true, Cue = "whispered" });
        var cast = new CastSheet();
        cast.Members.Add(new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = "af_heart" });
        book.Options.ReadChapterHeadings = false;
        var plan = Bilgames.Core.Planning.SynthesisPlanner.Plan(book, text, analysis, cast, "m");
        var narration = plan.Segments.Single(s => s.LineId == "b0l0");
        var whisper = plan.Segments.Single(s => s.LineId == "b0l1");
        Assert.True(whisper.Speed < 1f);
        Assert.True(whisper.GainDb < -5);
        Assert.True(narration.PauseAfterMs > 160); // a longer breath before the whisper
    }

    [Fact]
    public void BoostsAreSoftLimitedAndCutsAreLinear()
    {
        var loud = Bilgames.Core.Audio.Loudness.Apply(new short[] { 30000, -30000, 1000 }, 6f);
        Assert.InRange(loud[0], 25000, 32767);
        Assert.InRange(loud[1], -32768, -25000);
        Assert.InRange(loud[2], 1900, 2100);
        var quiet = Bilgames.Core.Audio.Loudness.Apply(new short[] { 20000 }, -6f);
        Assert.InRange(quiet[0], 9900, 10100);
    }
}

public class ToneNegationTests
{
    [Theory]
    [InlineData("“Though that may stop now.”", "She gave a small, humorless laugh.", "stern")]
    [InlineData("“It’s very much real,”", "Taeris smiled, though there was little humor in the expression.", "stern")]
    [InlineData("“It’s fine.”", "She gave him a sad smile.", "sad")]
    public void QualifiedSmilesAreNotAmusement(string quote, string narration, string expected) =>
        Assert.Equal(expected, Bilgames.Core.Analysis.Tones.FromContext(quote, narration));
}

public class ToneScopeTests
{
    [Fact]
    public void ABeatColoursTheQuoteAfterItNotBefore()
    {
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        text.Blocks.Add(new Block { Id = "b0", Kind = BlockKind.Paragraph, Text = "“Apparently you did,” said Asha. Then she lowered her voice. “Mistress Alita has been keeping me busy.”" });
        var result = Bilgames.Core.Analysis.DialogueAnalyzer.Analyze(new List<ChapterText> { text }, "af_heart");
        var quotes = result.Chapters[0].Lines.Where(l => l.IsDialogue).ToList();
        Assert.Equal(2, quotes.Count);
        Assert.Null(quotes[0].Cue);
        Assert.Equal("whispered", quotes[1].Cue);
        Assert.Equal(CueSource.Context, quotes[1].CueFrom);
    }
}

public class AllNotesExportTests
{
    [Fact]
    public void ManyBooksNestUnderOneTitleAndCsvNamesTheBook()
    {
        var a = (new BookRecord { Title = "Book A", Author = "One", Chapters = { new ChapterInfo { Index = 0, Title = "Ch 1" } } },
                 new AnnotationSet { Bookmarks = { new Bookmark { ChapterIndex = 0, Seconds = 5, Excerpt = "Hi." } } });
        var b = (new BookRecord { Title = "Book, B", Author = "Two", Chapters = { new ChapterInfo { Index = 0, Title = "Start" } } },
                 new AnnotationSet { Highlights = { new Highlight { ChapterIndex = 0, Seconds = 9, Text = "Yes.", Note = "good" } } });
        var md = AnnotationExport.WriteMany(new[] { a, b }, AnnotationFormat.Markdown);
        Assert.StartsWith("# Bookmarks, highlights and notes", md);
        Assert.Contains("## Book A", md);
        Assert.Contains("### Ch 1", md);
        Assert.Contains("## Book, B", md);
        var csv = AnnotationExport.WriteMany(new[] { a, b }, AnnotationFormat.Csv).Split(Environment.NewLine);
        Assert.Equal("Book,Author,Type,Chapter,Time,Seconds,Text,Note,Color,Created", csv[0]);
        Assert.StartsWith("Book A,One,Bookmark,Ch 1,0:05,5,Hi.,", csv[1]);
        Assert.StartsWith("\"Book, B\",Two,Highlight,Start,0:09,9,Yes.,good,yellow,", csv[2]);
    }
}

public sealed class ConcurrentEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-race-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task SimultaneousEditsAreAllKept()
    {
        var store = new LibraryStore(_root);
        var books = new BookService(store);
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        for (int i = 0; i < 60; i++)
            text.Blocks.Add(new Block { Id = $"c0b{i}", Kind = BlockKind.Paragraph, Text = i % 2 == 0 ? $"“Line {i}, is it?”" : $"“Answer {i}.”" });
        var book = new BookRecord { Id = "bk", Title = "Race" };
        book.Chapters.Add(new ChapterInfo { Index = 0, Title = "One", Kind = SectionKind.Body });
        Directory.CreateDirectory(Path.Combine(store.BookDir("bk"), "text"));
        store.SaveChapterText("bk", text);
        store.SaveBook(book);
        books.Analyze("bk");
        var lines = store.LoadAnalysis("bk", 0)!.Lines.Where(l => l.IsDialogue).ToList();

        // Confirms, new speakers, tone changes and recounts all at once, as UI actions on background threads.
        var tasks = new List<Task>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            int n = i;
            tasks.Add(Task.Run(() =>
            {
                var who = books.AddSpeaker("bk", $"Person {n % 7}");
                books.AssignSpeaker("bk", 0, line.Id, who.Id, false);
                if (n % 3 == 0) books.SetCue("bk", 0, line.Id, "sad");
            }));
        }
        await Task.WhenAll(tasks);

        var after = store.LoadAnalysis("bk", 0)!.Lines.Where(l => l.IsDialogue).ToList();
        var cast = store.LoadCast("bk");
        Assert.All(after, l => Assert.True(l.Reviewed, $"{l.Id} lost its review"));
        Assert.All(after, l => Assert.NotNull(cast.Find(l.SpeakerId)));
        Assert.Equal(7, cast.Members.Count(m => m.UserAdded));
        Assert.Equal(lines.Count, cast.Members.Where(m => m.UserAdded).Sum(m => m.LineCount));
        Assert.Equal((lines.Count + 2) / 3, after.Count(l => l.Cue == "sad"));
    }
}

public sealed class RenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-rename-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (LibraryStore, BookService) Book()
    {
        var store = new LibraryStore(_root);
        var books = new BookService(store);
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        string[] p = { "“We should go,” said Henry.", "“Not yet,” said Rinaldi.", "“The trucks are leaving,” said Henry.", "“Let them,” said Rinaldi.", "“Who is that?”" };
        for (int i = 0; i < p.Length; i++) text.Blocks.Add(new Block { Id = $"c0b{i}", Kind = BlockKind.Paragraph, Text = p[i] });
        var book = new BookRecord { Id = "bk", Title = "T" };
        book.Chapters.Add(new ChapterInfo { Index = 0, Title = "One", Kind = SectionKind.Body });
        Directory.CreateDirectory(Path.Combine(store.BookDir("bk"), "text"));
        store.SaveChapterText("bk", text);
        store.SaveBook(book);
        books.Analyze("bk");
        return (store, books);
    }

    [Fact]
    public void RenamesSurviveFindingSpeakersAgain()
    {
        var (store, books) = Book();
        var rinaldi = store.LoadCast("bk").Members.Single(m => m.Name == "Rinaldi");
        Assert.Equal(BookService.RenameResult.Renamed, books.RenameSpeaker("bk", rinaldi.Id, "Lt. Rinaldi", out _));
        Assert.Equal(BookService.RenameResult.NameTaken, books.RenameSpeaker("bk", rinaldi.Id, "henry", out var taken));
        Assert.Equal(store.LoadCast("bk").Members.Single(m => m.Name == "Henry").Id, taken);

        books.Analyze("bk");
        var cast = store.LoadCast("bk");
        Assert.Contains(cast.Members, m => m.Name == "Lt. Rinaldi" && m.LineCount == 2);
        Assert.DoesNotContain(cast.Members, m => m.Name == "Rinaldi");
    }

    [Fact]
    public void ConfirmedLinesKeepTheirSpeakerEvenIfAnalysisDropsThem()
    {
        var (store, books) = Book();
        var line = store.LoadAnalysis("bk", 0)!.Lines.Last(l => l.IsDialogue);
        var stranger = books.AddSpeaker("bk", "The Stranger");
        books.AssignSpeaker("bk", 0, line.Id, stranger.Id, false);
        // Simulate an older cast where this speaker wasn't marked as added (e.g. found by an earlier analysis).
        var cast = store.LoadCast("bk");
        cast.Find(stranger.Id)!.UserAdded = false;
        store.SaveCast("bk", cast);

        books.Analyze("bk");
        var after = store.LoadAnalysis("bk", 0)!.Lines.Single(l => l.Id == line.Id);
        Assert.True(after.Reviewed);
        Assert.Equal("The Stranger", store.LoadCast("bk").Find(after.SpeakerId)!.Name);
    }
}
