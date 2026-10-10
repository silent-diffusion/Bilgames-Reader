namespace Bilgames.Core.Model;

public enum HighlightColor
{
    Yellow,
    Green,
    Blue,
    Pink,
}

/// <summary>A saved place in the book: chapter + time, with the line being read there.</summary>
public sealed class Bookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public int ChapterIndex { get; set; }
    public double Seconds { get; set; }
    public string LineId { get; set; } = "";
    /// <summary>The text being read at the bookmark, kept so exports don't depend on the analysis.</summary>
    public string Excerpt { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A highlighted passage within one paragraph: from <see cref="StartOffset"/> in line
/// <see cref="StartLineId"/> up to (not including) <see cref="EndOffset"/> in <see cref="EndLineId"/>.
/// </summary>
public sealed class Highlight
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public int ChapterIndex { get; set; }
    public string BlockId { get; set; } = "";
    public string StartLineId { get; set; } = "";
    public int StartOffset { get; set; }
    public string EndLineId { get; set; } = "";
    public int EndOffset { get; set; }
    public string Text { get; set; } = "";
    public HighlightColor Color { get; set; }
    public string Note { get; set; } = "";
    /// <summary>Where the passage starts in the chapter audio, or -1 if it wasn't generated yet.</summary>
    public double Seconds { get; set; } = -1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Everything the reader has marked in a book (books/{id}/annotations.json).</summary>
public sealed class AnnotationSet
{
    public List<Bookmark> Bookmarks { get; set; } = new();
    public List<Highlight> Highlights { get; set; } = new();

    public bool IsEmpty => Bookmarks.Count == 0 && Highlights.Count == 0;
}
