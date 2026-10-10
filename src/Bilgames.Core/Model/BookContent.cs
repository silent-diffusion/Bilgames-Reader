namespace Bilgames.Core.Model;

/// <summary>Where a chapter sits in the book; drives the Structure checklist groups.</summary>
public enum SectionKind
{
    FrontMatter,
    Preface,
    Body,
    BackMatter,
}

public enum BlockKind
{
    Heading,
    Paragraph,
    SceneBreak,
    Verse,
}

public enum FootnoteMode
{
    Inline,
    EndOfChapter,
    Skip,
}

/// <summary>A paragraph-level unit of extracted text. Ids are stable for the life of the import.</summary>
public sealed class Block
{
    public string Id { get; set; } = "";
    public BlockKind Kind { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Ids of footnotes referenced from this block, in order.</summary>
    public List<string> NoteRefs { get; set; } = new();
}

public sealed class Footnote
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Full text of one chapter. Stored one file per chapter so long books never load whole.</summary>
public sealed class ChapterText
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public SectionKind Kind { get; set; }
    public List<Block> Blocks { get; set; } = new();
    public List<Footnote> Notes { get; set; } = new();

    public int WordCount => Blocks.Where(b => b.Kind != BlockKind.SceneBreak).Sum(b => TextUtil.CountWords(b.Text));
}

/// <summary>Lightweight chapter summary kept in book.json.</summary>
public sealed class ChapterInfo
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public SectionKind Kind { get; set; }
    public int WordCount { get; set; }
    public int NoteCount { get; set; }
}

public static class TextUtil
{
    public static int CountWords(string text)
    {
        int count = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool w = !char.IsWhiteSpace(c);
            if (w && !inWord) count++;
            inWord = w;
        }
        return count;
    }
}
