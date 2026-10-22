using Bilgames.Core.Model;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Assist;

/// <summary>How a character sounds, as the assistant described them.</summary>
public sealed record CharacterSketch(CastMember Member, Gender Gender, string Age, string Accent, string Sound);

/// <summary>
/// Matches voices to described characters, in order of importance (the narrator, then by how much
/// each speaks): same gender, then the character's accent (or the setting's), then the best quality,
/// keeping every voice unique until there are no unused ones left. Deterministic, so it's testable
/// and its choices can be explained.
/// </summary>
public static class VoiceCaster
{
    public static List<VoiceSuggestion> Cast(IReadOnlyList<CharacterSketch> characters, string settingAccent, IReadOnlyList<VoiceInfo> voices)
    {
        var used = new HashSet<string>();
        var result = new List<VoiceSuggestion>();
        var order = characters.OrderByDescending(c => c.Member.IsNarrator).ThenByDescending(c => c.Member.LineCount).ToList();
        foreach (var c in order)
        {
            var gender = c.Gender != Gender.Unknown ? c.Gender
                : c.Member.IsNarrator ? VoiceResolver.Describe(c.Member.VoiceId)?.Gender ?? Gender.Unknown
                : Gender.Unknown;
            var accent = AccentName(c.Accent is "british" or "american" ? c.Accent : settingAccent);
            var pick = voices
                .OrderByDescending(v => gender == Gender.Unknown || v.Gender == gender)
                .ThenByDescending(v => !used.Contains(v.Id))
                .ThenByDescending(v => accent is not null && v.Accent == accent)
                .ThenByDescending(v => Quality(v.Grade))
                .First();
            used.Add(pick.Id);
            result.Add(new VoiceSuggestion { MemberId = c.Member.Id, VoiceId = pick.Id, Reason = Why(c, gender, pick) });
        }
        return result;
    }

    private static string? AccentName(string accent) => accent switch { "british" => "British", "american" => "American", _ => null };

    /// <summary>Kokoro's published quality grades (A best … F worst), as numbers.</summary>
    public static int Quality(string grade) => grade switch
    {
        "A" => 12, "A-" => 11, "B+" => 10, "B" => 9, "B-" => 8, "C+" => 7, "C" => 6, "C-" => 5, "D+" => 4, "D" => 3, "D-" => 2, "F+" => 1, _ => 0,
    };

    private static string Why(CharacterSketch c, Gender gender, VoiceInfo v)
    {
        var who = new List<string>();
        if (c.Age is not ("unknown" or "")) who.Add(c.Age);
        if (gender != Gender.Unknown) who.Add(gender == Gender.Female ? "female" : "male");
        if (c.Accent is "british" or "american") who.Add(c.Accent == "british" ? "British" : "American");
        var described = who.Count > 0 ? string.Join(", ", who) : "";
        var sound = string.IsNullOrWhiteSpace(c.Sound) ? "" : $" ({c.Sound.Trim()})";
        return $"{(described.Length > 0 ? char.ToUpperInvariant(described[0]) + described[1..] : "A voice")}{sound}: {v.DisplayName} is a {v.Accent} {(v.Gender == Gender.Female ? "female" : "male")} voice, quality {v.Grade}.";
    }
}
