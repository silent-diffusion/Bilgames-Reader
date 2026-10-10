namespace Bilgames.Core.Model;

/// <summary>Where each spoken piece of a chapter sits in its audio file. Drives the live transcript.</summary>
public sealed class SegmentTiming
{
    public double Start { get; set; }
    public double End { get; set; }
    public string BlockId { get; set; } = "";
    public string LineId { get; set; } = "";
    public string SpeakerId { get; set; } = CastMember.NarratorId;
}

public sealed class ChapterAudio
{
    public int ChapterIndex { get; set; }
    /// <summary>Hash of the synthesis plan; when the plan changes, the chapter needs regenerating.</summary>
    public string PlanHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public double DurationSeconds { get; set; }
    public List<SegmentTiming> Timings { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
}

public enum JobState
{
    Idle,
    Queued,
    Running,
    Paused,
    Done,
    Failed,
}

public sealed class GenerationState
{
    public JobState State { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedUtc { get; set; }
}

public sealed class AppSettings
{
    public string DefaultNarratorVoice { get; set; } = Tts.VoiceCatalog.DefaultNarrator;
    /// <summary>Off by default: the app never touches the network unless the user opts in.</summary>
    public bool AutoCheckForUpdates { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public bool KeepSegmentCache { get; set; } = true;
    public Tts.ProcessorPreference Processor { get; set; } = Tts.ProcessorPreference.Gpu;
    public string? LastSection { get; set; }
    public string? LastBookId { get; set; }
}
