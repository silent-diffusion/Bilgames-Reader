using Bilgames.Core.Model;

namespace Bilgames.Core.Tts;

public sealed record VoiceInfo(string Id, string DisplayName, Gender Gender, string Accent, string Grade)
{
    public string Description => $"{(Gender == Gender.Female ? "Female" : "Male")} · {Accent}";
}

/// <summary>The English Kokoro v1.0 voices (Apache-2.0), ordered best-first within each group.</summary>
public static class VoiceCatalog
{
    public static IReadOnlyList<VoiceInfo> All { get; } = new List<VoiceInfo>
    {
        new("af_heart", "Heart", Gender.Female, "American", "A"),
        new("af_bella", "Bella", Gender.Female, "American", "A-"),
        new("bf_emma", "Emma", Gender.Female, "British", "B-"),
        new("af_nicole", "Nicole", Gender.Female, "American", "B-"),
        new("af_aoede", "Aoede", Gender.Female, "American", "C+"),
        new("af_kore", "Kore", Gender.Female, "American", "C+"),
        new("af_sarah", "Sarah", Gender.Female, "American", "C+"),
        new("bf_isabella", "Isabella", Gender.Female, "British", "C"),
        new("af_alloy", "Alloy", Gender.Female, "American", "C"),
        new("af_nova", "Nova", Gender.Female, "American", "C"),
        new("af_sky", "Sky", Gender.Female, "American", "C-"),
        new("bf_alice", "Alice", Gender.Female, "British", "D"),
        new("bf_lily", "Lily", Gender.Female, "British", "D"),
        new("af_jessica", "Jessica", Gender.Female, "American", "D"),
        new("af_river", "River", Gender.Female, "American", "D"),

        new("am_fenrir", "Fenrir", Gender.Male, "American", "C+"),
        new("am_michael", "Michael", Gender.Male, "American", "C+"),
        new("am_puck", "Puck", Gender.Male, "American", "C+"),
        new("bm_george", "George", Gender.Male, "British", "C"),
        new("bm_fable", "Fable", Gender.Male, "British", "C"),
        new("bm_lewis", "Lewis", Gender.Male, "British", "D+"),
        new("bm_daniel", "Daniel", Gender.Male, "British", "D"),
        new("am_echo", "Echo", Gender.Male, "American", "D"),
        new("am_eric", "Eric", Gender.Male, "American", "D"),
        new("am_liam", "Liam", Gender.Male, "American", "D"),
        new("am_onyx", "Onyx", Gender.Male, "American", "D"),
        new("am_adam", "Adam", Gender.Male, "American", "F+"),
    };

    public const string DefaultNarrator = "af_heart";

    public static VoiceInfo Get(string id) => All.FirstOrDefault(v => v.Id == id) ?? All[0];

    public static string Name(string id) => Get(id).DisplayName;

    private static readonly string[] SpeakerColors =
    {
        "#9BD39A", "#7FB2F0", "#F4A3B5", "#7FD6CF", "#C7A6F2",
        "#E8C468", "#F2A07B", "#A3C4F3", "#B5E48C", "#FFB4A2", "#CDB4DB", "#90DBF4",
    };

    public const string NarratorColor = "#C9CED6";

    public static string ColorFor(int characterIndex) => SpeakerColors[characterIndex % SpeakerColors.Length];

    /// <summary>The speaker colour used least among <paramref name="usedColors"/> (for a newly added character).</summary>
    public static string NextColor(IEnumerable<string> usedColors)
    {
        var counts = usedColors.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return SpeakerColors.OrderBy(c => counts.GetValueOrDefault(c)).First();
    }

    /// <summary>
    /// A voice for a newly added character: the best voice of that gender (either, if unknown) that
    /// nobody in the book uses yet; once all are taken, the least-used one.
    /// </summary>
    public static string PickFor(Gender gender, IEnumerable<string> usedVoices)
    {
        var counts = usedVoices.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        return All.Where(v => gender == Gender.Unknown || v.Gender == gender)
            .OrderBy(v => counts.GetValueOrDefault(v.Id)).First().Id; // stable sort: best grade first among equals
    }

    /// <summary>
    /// Gives each character a stable voice that differs from the narrator's. Main characters get
    /// the best voices of their gender; once a gender's voices run out, minor characters share.
    /// </summary>
    public static void AssignVoices(IList<CastMember> members, string narratorVoice)
    {
        var used = new HashSet<string> { narratorVoice };
        var queues = new Dictionary<Gender, Queue<VoiceInfo>>
        {
            [Gender.Female] = new(All.Where(v => v.Gender == Gender.Female && v.Id != narratorVoice)),
            [Gender.Male] = new(All.Where(v => v.Gender == Gender.Male && v.Id != narratorVoice)),
        };
        int unknownToggle = 0;
        foreach (var m in members.Where(m => !m.IsNarrator).OrderByDescending(m => m.LineCount))
        {
            if (!string.IsNullOrEmpty(m.VoiceId)) { used.Add(m.VoiceId); continue; }
            var g = m.Gender != Gender.Unknown ? m.Gender : (unknownToggle++ % 2 == 0 ? Gender.Male : Gender.Female);
            var q = queues[g];
            if (q.Count == 0)
                foreach (var v in All.Where(v => v.Gender == g && v.Id != narratorVoice)) q.Enqueue(v);
            m.VoiceId = q.Dequeue().Id;
        }
    }
}
