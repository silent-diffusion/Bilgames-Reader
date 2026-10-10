using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bilgames.Core.Model;

namespace Bilgames.Core.Planning;

/// <summary>One unit of speech to synthesize: a single voice, speed and stretch of text.</summary>
public sealed class PlannedSegment
{
    public string Hash { get; init; } = "";
    public string Text { get; init; } = "";
    public string VoiceId { get; init; } = "";
    public float Speed { get; init; } = 1f;
    public string SpeakerId { get; init; } = CastMember.NarratorId;
    public string BlockId { get; init; } = "";
    public string LineId { get; init; } = "";
    /// <summary>Silence after this segment, in milliseconds.</summary>
    public int PauseAfterMs { get; init; }
}

public sealed class ChapterPlan
{
    public int ChapterIndex { get; init; }
    public List<PlannedSegment> Segments { get; init; } = new();
    public string Hash { get; init; } = "";
}

/// <summary>
/// Decides exactly what is spoken, by which voice, in what order. Kept separate from synthesis so
/// a speaker correction only changes the affected segments' hashes; everything else stays cached.
/// </summary>
public static partial class SynthesisPlanner
{
    public const int MaxSegmentChars = 280;
    public const int FastSegmentChars = 420;

    public static ChapterPlan Plan(BookRecord book, ChapterText chapter, ChapterAnalysis? analysis, CastSheet cast, string modelId)
    {
        var opts = book.Options;
        int maxChars = book.Quality == QualityProfile.Fast ? FastSegmentChars : MaxSegmentChars;
        var narrator = cast.Members.FirstOrDefault(m => m.IsNarrator)?.VoiceId ?? Tts.VoiceCatalog.DefaultNarrator;
        string VoiceOf(string speakerId) => cast.Find(speakerId)?.VoiceId is { Length: > 0 } v ? v : narrator;

        var linesByBlock = analysis?.Lines.GroupBy(l => l.BlockId).ToDictionary(g => g.Key, g => g.ToList());
        var notes = chapter.Notes.ToDictionary(n => n.Id);
        var endNotes = new List<Footnote>();
        var segs = new List<PlannedSegment>();

        void Add(string text, string speakerId, string? cue, string blockId, string lineId, int pauseAfter)
        {
            text = ForSpeech(text);
            if (text.Length == 0) return;
            var voice = VoiceOf(speakerId);
            float speed = CueSpeed(cue);
            var chunks = Chunk(text, maxChars);
            for (int i = 0; i < chunks.Count; i++)
            {
                bool last = i == chunks.Count - 1;
                segs.Add(new PlannedSegment
                {
                    Text = chunks[i],
                    VoiceId = voice,
                    Speed = speed,
                    SpeakerId = speakerId,
                    BlockId = blockId,
                    LineId = lineId,
                    PauseAfterMs = last ? pauseAfter : 80,
                    Hash = HashOf(chunks[i], voice, speed, modelId),
                });
            }
        }

        bool leadingHeadings = true, announced = false;
        foreach (var block in chapter.Blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.SceneBreak:
                    if (segs.Count > 0) segs[^1] = WithPause(segs[^1], 1300);
                    continue;
                case BlockKind.Heading:
                    if (leadingHeadings)
                    {
                        // The heading group at the top is announced once, as the chapter's clean title.
                        if (opts.ReadChapterHeadings && !announced)
                            Add(SpokenTitle(chapter.Title), CastMember.NarratorId, null, block.Id, block.Id + "l0", 1000);
                        announced = true;
                        continue;
                    }
                    Add(EndSentence(block.Text), CastMember.NarratorId, null, block.Id, block.Id + "l0", 900);
                    continue;
            }
            leadingHeadings = false;

            if (linesByBlock is not null && linesByBlock.TryGetValue(block.Id, out var lines))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    bool lastInBlock = i == lines.Count - 1;
                    Add(l.Text, l.SpeakerId, l.IsDialogue ? l.Cue : null, block.Id, l.Id, lastInBlock ? 450 : 160);
                }
            }
            else
            {
                Add(block.Text, CastMember.NarratorId, null, block.Id, block.Id + "l0", block.Kind == BlockKind.Verse ? 650 : 450);
            }

            foreach (var nid in block.NoteRefs)
            {
                if (!notes.TryGetValue(nid, out var note)) continue;
                if (opts.Footnotes == FootnoteMode.Inline)
                    Add("Note: " + note.Text, CastMember.NarratorId, null, block.Id, nid, 450);
                else if (opts.Footnotes == FootnoteMode.EndOfChapter && !endNotes.Contains(note))
                    endNotes.Add(note);
            }
        }

        if (endNotes.Count > 0)
        {
            if (segs.Count > 0) segs[^1] = WithPause(segs[^1], 1000);
            Add("Notes.", CastMember.NarratorId, null, "notes", "notes", 600);
            for (int i = 0; i < endNotes.Count; i++)
                Add($"Note {i + 1}. {endNotes[i].Text}", CastMember.NarratorId, null, "notes", endNotes[i].Id, 500);
        }

        var planHash = HashOf(string.Join("|", segs.Select(s => s.Hash + ":" + s.PauseAfterMs)), "", 1, modelId);
        return new ChapterPlan { ChapterIndex = chapter.Index, Segments = segs, Hash = planHash };
    }

    private static PlannedSegment WithPause(PlannedSegment s, int ms) => new()
    {
        Text = s.Text, VoiceId = s.VoiceId, Speed = s.Speed, SpeakerId = s.SpeakerId, BlockId = s.BlockId, LineId = s.LineId,
        Hash = s.Hash, PauseAfterMs = Math.Max(s.PauseAfterMs, ms),
    };

    /// <summary>
    /// Delivery is expressed as small speed changes only, kept deliberately subtle so lines are
    /// not overacted when a cue is wrong.
    /// </summary>
    internal static float CueSpeed(string? cue) => cue switch
    {
        null => 1f,
        "whispered" or "sad" or "hesitant" or "gentle" or "soft" or "quiet" or "slow" or "grave" => 0.94f,
        "shouting" or "excited" or "impatient" or "angry" or "eager" or "sharp" or "hasty" or "quick" => 1.05f,
        _ => 1f,
    };

    /// <summary>"Part II · Chapter IV" → "Part 2. Chapter 4." so the voice doesn't spell out numerals.</summary>
    internal static string SpokenTitle(string title)
    {
        var parts = title.Split(" · ").Select(p => RomanLabel().Replace(p, m => m.Groups[1].Value + RomanToInt(m.Groups[2].Value)));
        return string.Join(" ", parts.Select(EndSentence));
    }

    private static string RomanToInt(string roman)
    {
        var map = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000 };
        int total = 0;
        for (int i = 0; i < roman.Length; i++)
        {
            int v = map[char.ToUpperInvariant(roman[i])];
            total += i + 1 < roman.Length && map[char.ToUpperInvariant(roman[i + 1])] > v ? -v : v;
        }
        return total.ToString();
    }

    [GeneratedRegex(@"^((?:Chapter|Part|Book|Volume|Canto|Stave)\s+)([IVXLCDM]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RomanLabel();

    private static string EndSentence(string s) => s.Length > 0 && !char.IsPunctuation(s[^1]) ? s + "." : s;

    /// <summary>Cleans text for the TTS front end without changing what is said.</summary>
    internal static string ForSpeech(string text)
    {
        var t = text.Replace("--", " — ").Replace("&", " and ").Replace('\n', ' ');
        t = Apostrophes(t);
        t = MultiSpace().Replace(t, " ").Trim();
        return t;
    }

    /// <summary>
    /// Typeset apostrophes (’ and look-alikes) become plain ' so the speech model reads "Jim’s" as one
    /// word ("Jimz") instead of "Jim" + "s". Quotation marks are left alone.
    /// </summary>
    public static string Apostrophes(string t)
    {
        if (t.IndexOfAny(ApostropheLike) < 0) return t;
        t = InnerApostrophe().Replace(t, "'");      // Jim’s, don’t, o’clock
        t = PluralPossessive().Replace(t, "'");     // the boys’ house
        t = LeadingElision().Replace(t, "'").Replace("'n’", "'n'"); // ’em, ’cause, rock ’n’ roll
        return t;
    }

    private static readonly char[] ApostropheLike = { '’', '‘', '`', '´', 'ʼ', '′' };

    [GeneratedRegex(@"(?<=\p{L})[’‘`´ʼ′](?=\p{L})")]
    private static partial Regex InnerApostrophe();

    [GeneratedRegex(@"(?<=\p{L}s)’(?=\s+\p{Ll})")]
    private static partial Regex PluralPossessive();

    [GeneratedRegex(@"(?<=^|[\s(—])’(?=(?:em|n|cause|til|bout|round|tis|twas|twere|twill|ere|neath|gainst)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingElision();

    /// <summary>Splits long text at sentence (then clause) boundaries into chunks the model handles well.</summary>
    internal static List<string> Chunk(string text, int max)
    {
        if (text.Length <= max) return new List<string> { text };
        var sentences = SentenceSplit().Split(text).Where(s => s.Length > 0).ToList();
        var parts = new List<string>();
        foreach (var s in sentences)
        {
            if (s.Length <= max) { parts.Add(s); continue; }
            // A very long sentence: break at clause punctuation, then at spaces.
            var clauses = ClauseSplit().Split(s);
            var sb = new StringBuilder();
            foreach (var c in clauses)
            {
                if (sb.Length + c.Length + 1 > max && sb.Length > 0) { parts.Add(sb.ToString().Trim()); sb.Clear(); }
                if (c.Length > max)
                {
                    foreach (var w in c.Split(' '))
                    {
                        if (sb.Length + w.Length + 1 > max && sb.Length > 0) { parts.Add(sb.ToString().Trim()); sb.Clear(); }
                        sb.Append(w).Append(' ');
                    }
                }
                else sb.Append(c).Append(' ');
            }
            if (sb.Length > 0) parts.Add(sb.ToString().Trim());
        }

        // Re-pack short sentences together so most chunks land in the model's sweet spot.
        var result = new List<string>();
        var cur = new StringBuilder();
        foreach (var p in parts)
        {
            if (cur.Length > 0 && cur.Length + 1 + p.Length > max) { result.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(p);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    public static string HashOf(string text, string voice, float speed, string modelId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{modelId}\u0001{voice}\u0001{speed:F3}\u0001{text}"));
        return Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"(?<=[.!?…][”’""']?)\s+(?=[“""‘'A-Z0-9])")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"(?<=[,;:—])\s+")]
    private static partial Regex ClauseSplit();
}
