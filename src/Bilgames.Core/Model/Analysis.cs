using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bilgames.Core.Model;

public enum Gender
{
    Unknown,
    Female,
    Male,
}

/// <summary>How a line's speaker was decided. Drives confidence and the Review queue.</summary>
public enum AttributionSource
{
    Narration,
    Tag,
    Pronoun,
    ActionBeat,
    Continuation,
    Alternation,
    Fallback,
    User,
}

public sealed class SpeakerCandidate
{
    public string SpeakerId { get; set; } = "";
    public double Score { get; set; }
}

/// <summary>How a line's tone was decided.</summary>
public enum CueSource
{
    None,
    /// <summary>From the dialogue tag: "she whispered", "he said angrily".</summary>
    Tag,
    /// <summary>Guessed from the quote itself and the narration around it.</summary>
    Context,
    /// <summary>Set by the reader.</summary>
    User,
}

/// <summary>One spoken unit: a stretch of narration or a quoted span, in reading order.</summary>
public sealed class Line
{
    /// <summary>Fields written by a newer version of the app, kept so saving here doesn’t drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string Id { get; set; } = "";
    public string BlockId { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsDialogue { get; set; }
    /// <summary>Effective speaker. Narrator for narration and for unsure dialogue.</summary>
    public string SpeakerId { get; set; } = CastMember.NarratorId;
    public double Confidence { get; set; } = 1;
    public AttributionSource Source { get; set; }
    public List<SpeakerCandidate> Candidates { get; set; } = new();
    /// <summary>Delivery tone (one of Analysis.Tones, e.g. "whispered", "angry"); null for neutral.</summary>
    public string? Cue { get; set; }
    public CueSource CueFrom { get; set; }
    /// <summary>Consecutive dialogue paragraphs share a run id; used for back-and-forth propagation.</summary>
    public int RunId { get; set; } = -1;
    /// <summary>Paragraph position within its dialogue run (0-based).</summary>
    public int RunPosition { get; set; } = -1;
    public bool Unsure { get; set; }
    public bool Reviewed { get; set; }
}

public sealed class ChapterAnalysis
{
    /// <summary>Fields written by a newer version of the app, kept so saving here doesn’t drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public int ChapterIndex { get; set; }
    public List<Line> Lines { get; set; } = new();
}

public sealed class CastMember
{
    /// <summary>Fields written by a newer version of the app, kept so saving here doesn’t drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public const string NarratorId = "narrator";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public Gender Gender { get; set; }
    public string VoiceId { get; set; } = "";
    public string Color { get; set; } = "#C9CED6";
    public int LineCount { get; set; }
    public int ConfidentCount { get; set; }
    /// <summary>Added by the user rather than found by analysis; kept (even with no lines) when the book is re-analyzed.</summary>
    public bool UserAdded { get; set; }

    [JsonIgnore] public bool IsNarrator => Id == NarratorId;

    public string Initials
    {
        get
        {
            if (IsNarrator) return "N";
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.Equals("The", StringComparison.OrdinalIgnoreCase)).ToList();
            var parts = words.Where(p => !Analysis.NameRules.IsTitle(p)).ToList();
            if (parts.Count == 0) parts = words; // "The Lieutenant" → "L"
            if (parts.Count == 0) return Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
            if (parts.Count == 1) return parts[0][..1].ToUpperInvariant();
            return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
        }
    }
}

public sealed class MergeSuggestion
{
    public string FromId { get; set; } = "";
    public string IntoId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class CastSheet
{
    /// <summary>Fields written by a newer version of the app, kept so saving here doesn’t drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public List<CastMember> Members { get; set; } = new();
    public List<MergeSuggestion> Suggestions { get; set; } = new();
    public List<string> DismissedSuggestions { get; set; } = new();

    public CastMember? Find(string id) => Members.FirstOrDefault(m => m.Id == id);
    [JsonIgnore] public CastMember Narrator => Members.First(m => m.IsNarrator);
}
