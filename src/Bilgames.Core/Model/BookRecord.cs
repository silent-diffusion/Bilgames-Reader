namespace Bilgames.Core.Model;

public enum QualityProfile
{
    Fast,
    Balanced,
    High,
}

public enum AnalysisState
{
    NotStarted,
    Running,
    Done,
}

public sealed class StructureOptions
{
    public bool IncludeFrontMatter { get; set; }
    public bool IncludePreface { get; set; }
    public bool IncludeBody { get; set; } = true;
    public bool IncludeBackMatter { get; set; }
    public bool ReadChapterHeadings { get; set; } = true;
    public FootnoteMode Footnotes { get; set; } = FootnoteMode.EndOfChapter;

    public bool Includes(SectionKind kind) => kind switch
    {
        SectionKind.FrontMatter => IncludeFrontMatter,
        SectionKind.Preface => IncludePreface,
        SectionKind.BackMatter => IncludeBackMatter,
        _ => IncludeBody,
    };
}

public sealed class PlaybackPosition
{
    public int ChapterIndex { get; set; } = -1;
    public double Seconds { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Everything about a book except its text, analysis and audio. Persisted as book.json.</summary>
public sealed class BookRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Language { get; set; } = "en";
    public string? CoverFile { get; set; }
    public string CoverColor { get; set; } = "#2B4A5E";
    public string SourceFileName { get; set; } = "";
    public DateTime ImportedUtc { get; set; }
    public DateTime? LastPlayedUtc { get; set; }

    public List<ChapterInfo> Chapters { get; set; } = new();
    public StructureOptions Options { get; set; } = new();
    public AnalysisState Analysis { get; set; }
    public QualityProfile Quality { get; set; } = QualityProfile.Balanced;
    public PlaybackPosition Position { get; set; } = new();
    public double PlaybackRate { get; set; } = 1.0;
    public GenerationState Generation { get; set; } = new();
    /// <summary>Unsure dialogue lines still waiting for review (cached for the library list).</summary>
    public int UnsureCount { get; set; }
    public int VoiceCount { get; set; }

    public int TotalWords => Chapters.Sum(c => c.WordCount);

    public IEnumerable<ChapterInfo> IncludedChapters => Chapters.Where(c => Options.Includes(c.Kind));

    /// <summary>Audiobook narration averages roughly 155 words per minute.</summary>
    public static TimeSpan EstimateDuration(int words) => TimeSpan.FromMinutes(words / 155.0);
}
