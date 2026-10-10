using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Bilgames.Core.Audio;
using Bilgames.Core.Epub;
using Bilgames.Core.Model;
using Bilgames.Core.Planning;

namespace Bilgames.Core.Tests;

public class ArchiveSafetyTests
{
    [Theory]
    [InlineData("OEBPS/../../etc/passwd", null)]
    [InlineData("../evil.xhtml", null)]
    [InlineData("OEBPS/./text/../ch1.xhtml", "OEBPS/ch1.xhtml")]
    [InlineData("OEBPS/my%20chapter.xhtml", "OEBPS/my chapter.xhtml")]
    public void NormalizeRejectsTraversal(string input, string? expected) => Assert.Equal(expected, SafeArchive.Normalize(input));

    [Fact]
    public void RemoteHrefsAreNeverResolved() => Assert.Null(SafeArchive.Resolve("OEBPS/a.xhtml", "https://example.com/x.css"));

    [Fact]
    public void DecompressionBombIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("big.xhtml", CompressionLevel.SmallestSize);
                using var s = e.Open();
                var zeros = new byte[1 << 20];
                for (int i = 0; i < 70; i++) s.Write(zeros); // 70 MB of zeros compresses to ~70 KB
            }
            using var archive = SafeArchive.Open(path);
            Assert.Throws<EpubFormatException>(() => archive.ReadBytes("big.xhtml"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NotAZipGivesAFriendlyError()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "hello");
        try { Assert.Throws<EpubFormatException>(() => SafeArchive.Open(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DrmProtectedBooksAreRefused()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                void Add(string name, string text)
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open());
                    w.Write(text);
                }
                Add("mimetype", "application/epub+zip");
                Add("META-INF/encryption.xml", "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\"><enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/></enc:EncryptedData></encryption>");
            }
            Assert.Throws<DrmProtectedException>(() => EpubImporter.Import(path));
        }
        finally { File.Delete(path); }
    }
}

public class ParagraphRepairTests
{
    [Theory]
    [InlineData("Probably she had crushed her hand", "while swinging round", true)]
    [InlineData("It was a bright cold day in April.", "Winston Smith hurried.", false)]
    [InlineData("“You’re hurt?” he said.", "“It’s nothing.”", false)]
    [InlineData("Well,", "then I happened to think", true)]
    public void JoinsPageBrokenParagraphs(string prev, string next, bool joined) =>
        Assert.Equal(joined, ChapterBuilder.LooksContinued(prev, next));

    [Theory]
    [InlineData("Chapter 3", true)]
    [InlineData("CHAPTER XIV", true)]
    [InlineData("IV", true)]
    [InlineData("Prologue", true)]
    [InlineData("The sun was up so high when I waked.", false)]
    public void RecognizesHeadings(string text, bool heading) => Assert.Equal(heading, ChapterBuilder.IsHeadingLike(text));
}

public class PlannerTests
{
    private static (BookRecord, ChapterText, CastSheet) Sample()
    {
        var book = new BookRecord { Id = "b", Options = new StructureOptions() };
        var ch = new ChapterText
        {
            Index = 0,
            Title = "Chapter IV",
            Blocks =
            {
                new Block { Id = "c0b0", Kind = BlockKind.Heading, Text = "IV" },
                new Block { Id = "c0b1", Kind = BlockKind.Paragraph, Text = "It was a dark night. She said nothing.", NoteRefs = { "c0n0" } },
                new Block { Id = "c0b2", Kind = BlockKind.SceneBreak },
                new Block { Id = "c0b3", Kind = BlockKind.Paragraph, Text = "Morning came." },
            },
            Notes = { new Footnote { Id = "c0n0", Text = "A note." } },
        };
        var cast = new CastSheet { Members = { new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = "af_heart" } } };
        return (book, ch, cast);
    }

    [Fact]
    public void ChapterTitleIsSpokenWithArabicNumerals()
    {
        var (book, ch, cast) = Sample();
        var plan = SynthesisPlanner.Plan(book, ch, null, cast, "m");
        Assert.Equal("Chapter 4.", plan.Segments[0].Text);
    }

    [Fact]
    public void HeadingsCanBeSkipped()
    {
        var (book, ch, cast) = Sample();
        book.Options.ReadChapterHeadings = false;
        var plan = SynthesisPlanner.Plan(book, ch, null, cast, "m");
        Assert.DoesNotContain(plan.Segments, s => s.Text.StartsWith("Chapter"));
    }

    [Theory]
    [InlineData(FootnoteMode.Inline, "Note: A note.")]
    [InlineData(FootnoteMode.EndOfChapter, "Note 1. A note.")]
    [InlineData(FootnoteMode.Skip, null)]
    public void FootnoteModes(FootnoteMode mode, string? expected)
    {
        var (book, ch, cast) = Sample();
        book.Options.Footnotes = mode;
        var plan = SynthesisPlanner.Plan(book, ch, null, cast, "m");
        var note = plan.Segments.FirstOrDefault(s => s.Text.Contains("A note."));
        Assert.Equal(expected, note?.Text);
    }

    [Fact]
    public void ChangingAVoiceOnlyChangesThatSpeakersSegments()
    {
        var (book, ch, cast) = Sample();
        cast.Members.Add(new CastMember { Id = "ch0", Name = "Ann", VoiceId = "af_bella" });
        var analysis = new ChapterAnalysis
        {
            Lines =
            {
                new Line { Id = "a", BlockId = "c0b1", Text = "It was a dark night." },
                new Line { Id = "b", BlockId = "c0b1", Text = "“Hello.”", IsDialogue = true, SpeakerId = "ch0" },
            },
        };
        var before = SynthesisPlanner.Plan(book, ch, analysis, cast, "m");
        cast.Find("ch0")!.VoiceId = "bf_emma";
        var after = SynthesisPlanner.Plan(book, ch, analysis, cast, "m");
        var changed = before.Segments.Zip(after.Segments).Count(p => p.First.Hash != p.Second.Hash);
        Assert.Equal(1, changed);
        Assert.NotEqual(before.Hash, after.Hash);
    }

    [Fact]
    public void LongTextIsChunkedUnderTheLimit()
    {
        var text = string.Join(" ", Enumerable.Repeat("This is a sentence that goes on for a while.", 30));
        var chunks = SynthesisPlanner.Chunk(text, 280);
        Assert.All(chunks, c => Assert.True(c.Length <= 280));
        Assert.Equal(text, string.Join(" ", chunks));
    }
}

public class Mp4MetadataTests
{
    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    [Fact]
    public void WritesChaptersAndShiftsChunkOffsetsWhenMoovComesFirst()
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A \0\0\0\0M4A mp42"));
        byte[] Stco(uint offset)
        {
            var p = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), offset);
            return Box("stco", p);
        }
        // Lay out once to learn the mdat offset, then rebuild with the real value.
        var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Stco(0))))));
        uint mdatData = (uint)(ftyp.Length + moov.Length + 8);
        moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Stco(mdatData))))));
        var mdat = Box("mdat", Encoding.ASCII.GetBytes("AUDIODATA"));
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, ftyp.Concat(moov).Concat(mdat).ToArray());
        try
        {
            Mp4Metadata.Write(path, new[] { new Mp4Chapter(TimeSpan.Zero, "Chapter 1"), new Mp4Chapter(TimeSpan.FromMinutes(12.5), "Chapter 2 · Café") }, "Book", "Author");
            var chapters = Mp4Metadata.ReadChapters(path);
            Assert.Equal(2, chapters.Count);
            Assert.Equal("Chapter 2 · Café", chapters[1].Title);
            Assert.Equal(TimeSpan.FromMinutes(12.5), chapters[1].Start);

            var bytes = File.ReadAllBytes(path);
            int stco = IndexOf(bytes, "stco"u8.ToArray());
            uint newOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stco + 12));
            Assert.Equal("AUDIODATA", Encoding.ASCII.GetString(bytes, (int)newOffset, 9));
        }
        finally { File.Delete(path); }
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}

public class ChapterStreamTests
{
    [Fact]
    public void ChunksFollowTheFinalChapterTimeline()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilgames-stream-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Bilgames.Core.Storage.LibraryStore(root);
            var segs = new List<PlannedSegment>();
            for (int i = 0; i < 6; i++)
                segs.Add(new PlannedSegment { Hash = "h" + i, Text = "Sentence number " + i + ".", BlockId = "b" + (i / 2), LineId = "l" + i, PauseAfterMs = 200 });
            var plan = new ChapterPlan { ChapterIndex = 0, Segments = segs };
            Directory.CreateDirectory(Path.Combine(store.BookDir("bk"), "segments"));
            void Ready(int i) => Wav.Write(store.SegmentPath("bk", segs[i].Hash), new short[24000], 24000); // 1 s each

            using var stream = new ChapterStream(store, "bk", plan, 24000);
            Assert.Null(stream.TryBuildNext());             // nothing generated yet
            Ready(0);
            var first = stream.TryBuildNext();               // starts with whatever is ready
            Assert.NotNull(first);
            Assert.Equal(0.25, first!.Timings[0].Start, 3);   // same 250 ms lead-in as the saved chapter
            for (int i = 1; i < 6; i++) Ready(i);
            while (stream.TryBuildNext() is not null) { }
            Assert.True(stream.Complete);
            var all = stream.Chunks.SelectMany(c => c.Timings).ToList();
            Assert.Equal(6, all.Count);
            for (int i = 1; i < all.Count; i++)
                Assert.Equal(all[i - 1].End + 0.2, all[i].Start, 3); // contiguous: segment + its pause
            Assert.Equal(stream.Chunks[^1].Start + stream.Chunks[^1].Duration, stream.BufferedSeconds, 3);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
