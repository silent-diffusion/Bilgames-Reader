using Bilgames.Core.Audio;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Tests;

public sealed class TextAnchorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-anchor-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // Line a: 0–4 s; line b spoken as two segments, 5–7 and 7–9 s; line c: 10–12 s.
    private static List<SegmentTiming> Timings(double scale = 1) => new()
    {
        new() { LineId = "a", Start = 0 * scale, End = 4 * scale },
        new() { LineId = "b", Start = 5 * scale, End = 7 * scale },
        new() { LineId = "b", Start = 7 * scale, End = 9 * scale },
        new() { LineId = "c", Start = 10 * scale, End = 12 * scale },
    };

    [Fact]
    public void MomentBecomesLineAndFraction()
    {
        Assert.Equal(new TextAnchor("a", 0.5), TextAnchors.At(Timings(), 2));
        Assert.Equal(new TextAnchor("b", 0.75), TextAnchors.At(Timings(), 8)); // across both of b's segments
        Assert.Equal("b", TextAnchors.At(Timings(), 9.5)!.Value.LineId);      // a pause belongs to the line before it
    }

    [Fact]
    public void AnchorSurvivesRetimedAudio()
    {
        var anchor = TextAnchors.At(Timings(), 8)!.Value;
        // The same chapter generated slower (e.g. another voice): the anchor lands on the same words.
        Assert.Equal(12, TextAnchors.SecondsOf(Timings(scale: 1.5), anchor.LineId, anchor.Fraction));
        Assert.Null(TextAnchors.SecondsOf(Timings(), "gone", 0.5));
    }

    [Fact]
    public void UpgradeAnchorsExistingPositionAndBookmarks()
    {
        var store = new LibraryStore(_root);
        var book = new BookRecord { Id = "bk", Title = "T", Position = new PlaybackPosition { ChapterIndex = 0, Seconds = 11 } };
        store.SaveBook(book);
        store.SaveChapterAudio("bk", new ChapterAudio { ChapterIndex = 0, Timings = Timings() });
        store.SaveAnnotations("bk", new AnnotationSet
        {
            Bookmarks =
            {
                new Bookmark { ChapterIndex = 0, LineId = "b", Seconds = 6 },  // knew its line
                new Bookmark { ChapterIndex = 0, Seconds = 3 },               // older, seconds only
                new Bookmark { ChapterIndex = 3, Seconds = 3 },               // chapter without audio: left alone
            },
        });
        // A 0.4.x library has no format marker.
        var check = LibraryFormat.Prepare(store, "0.5.0");

        Assert.Equal(1, check.FoundVersion);
        Assert.True(File.Exists(check.BackupPath));
        var pos = store.LoadBook("bk")!.Position;
        Assert.Equal(("c", 0.5, 11.0), (pos.LineId, pos.Fraction, pos.Seconds));
        var marks = store.LoadAnnotations("bk").Bookmarks;
        Assert.Equal(("b", 0.25), (marks[0].LineId, marks[0].Fraction));
        Assert.Equal(("a", 0.75), (marks[1].LineId, marks[1].Fraction));
        Assert.Equal(("", 0.0, 3.0), (marks[2].LineId, marks[2].Fraction, marks[2].Seconds));
    }
}
