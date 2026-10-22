using System.Text.Json;
using System.Text.Json.Serialization;
using Bilgames.Core.Model;

namespace Bilgames.Core.Assist;

public enum AssistantRunKind
{
    /// <summary>The lines waiting in Review.</summary>
    Review,
    /// <summary>The whole book read again, with the first analysis as suggestions.</summary>
    WholeBook,
}

public enum AssistantRunState
{
    Running,
    Paused,
    Done,
    Undone,
}

/// <summary>One line the assistant changed: enough of the previous state to put it back exactly.</summary>
public sealed class AssistantChange
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Chapter { get; set; }
    public string LineId { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string FromSpeaker { get; set; } = "";
    public string ToSpeaker { get; set; } = "";
    public bool FromUnsure { get; set; }
    public AttributionSource FromSource { get; set; }
    public double FromConfidence { get; set; }
    public string? FromCue { get; set; }
    public CueSource FromCueFrom { get; set; }
    public string? ToCue { get; set; }
    public bool CueChanged { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class VoiceSuggestion
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string MemberId { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Applied { get; set; }
}

public sealed class AssistantRun
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public AssistantRunKind Kind { get; set; }
    public AssistantRunState State { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Work items done and in total (lines for Review, passages for the whole book).</summary>
    public int Done { get; set; }
    public int Total { get; set; }
    /// <summary>Where a paused whole-book run picks up: the next chapter and passage within it.</summary>
    public int NextChapter { get; set; }
    public int NextPassage { get; set; }
    /// <summary>Lines it looked at, lines it changed, and opinions it left as suggestions.</summary>
    public int Considered { get; set; }
    public int Suggested { get; set; }
    public List<AssistantChange> Changes { get; set; } = new();
}

/// <summary>books/{id}/assistant.json: what the review assistant did to a book, so it can be shown and undone.</summary>
public sealed class AssistantLog
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public List<AssistantRun> Runs { get; set; } = new();
    public List<VoiceSuggestion> VoiceSuggestions { get; set; } = new();

    /// <summary>The most recent run that hasn't finished (either kind).</summary>
    [JsonIgnore] public AssistantRun? Unfinished => Runs.LastOrDefault(r => r.State is AssistantRunState.Running or AssistantRunState.Paused);

    /// <summary>The unfinished run of one kind, if any (each kind keeps its own place).</summary>
    public AssistantRun? UnfinishedOf(AssistantRunKind kind) => Runs.LastOrDefault(r => r.Kind == kind && r.State is AssistantRunState.Running or AssistantRunState.Paused);
}
