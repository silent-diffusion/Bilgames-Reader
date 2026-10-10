using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>Fields written by a newer version of the app, kept so saving here doesn’t drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

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
    /// <summary>Chapters whose finished audio was removed to save space (Keep finished audio); made again when needed.</summary>
    public List<int> Removed { get; set; } = new();
}

/// <summary>Which finished chapters keep their audio on disk.</summary>
public enum KeepAudio
{
    /// <summary>Every generated chapter (the Windows default).</summary>
    All,
    /// <summary>The chapter being listened to, one before and a few ahead.</summary>
    Recent,
    /// <summary>Everything from the chapter being listened to onwards.</summary>
    UntilListened,
}

public enum AppTheme
{
    System,
    Dark,
    Light,
}

public sealed class AppSettings
{
    /// <summary>Fields written by a newer version of the app, kept so saving here doesn’t drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string DefaultNarratorVoice { get; set; } = Tts.VoiceCatalog.DefaultNarrator;
    /// <summary>Off by default: the app never touches the network unless the user opts in.</summary>
    public bool AutoCheckForUpdates { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public bool KeepSegmentCache { get; set; } = true;
    public KeepAudio KeepFinishedAudio { get; set; } = KeepAudio.All;
    /// <summary>Light, dark, or follow Windows. Windows contrast themes always win.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Size of book text in the player and context views, relative to the default.</summary>
    public double ReadingTextScale { get; set; } = 1.0;
    public Tts.ProcessorPreference Processor { get; set; } = Tts.ProcessorPreference.Gpu;
    public string? LastSection { get; set; }
    public string? LastBookId { get; set; }
}
