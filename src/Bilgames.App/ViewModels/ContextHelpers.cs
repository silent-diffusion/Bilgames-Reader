using Bilgames.App.Controls;
using Bilgames.Core.Model;
using Bilgames.App.Services;
using Bilgames.Core.Tts;

namespace Bilgames.App.ViewModels;

/// <summary>Turns analysis lines and cast data into what the context views and pickers show.</summary>
public static class ContextHelpers
{
    /// <summary>
    /// Keeps only a few sentences around the target line: up to <paramref name="before"/> characters
    /// before it and <paramref name="after"/> after, cut at sentence boundaries ("…" marks a cut).
    /// </summary>
    public static List<List<Line>> Trim(List<List<Line>> blocks, string targetLineId, int before, int after)
    {
        var flat = blocks.SelectMany((b, bi) => b.Select(l => (Block: bi, Line: l))).ToList();
        int t = flat.FindIndex(x => x.Line.Id == targetLineId);
        if (t < 0) return blocks;
        var keep = new Dictionary<int, Line>(); // index → (possibly shortened) line
        keep[t] = flat[t].Line;
        int budget = before;
        for (int i = t - 1; i >= 0 && budget > 0; i--)
        {
            var l = flat[i].Line;
            if (l.Text.Length <= budget) { keep[i] = l; budget -= l.Text.Length; continue; }
            keep[i] = WithText(l, "… " + TailSentences(l.Text, budget));
            break;
        }
        budget = after;
        for (int i = t + 1; i < flat.Count && budget > 0; i++)
        {
            var l = flat[i].Line;
            if (l.Text.Length <= budget) { keep[i] = l; budget -= l.Text.Length; continue; }
            keep[i] = WithText(l, HeadSentences(l.Text, budget) + " …");
            break;
        }
        return flat.Select((x, i) => (x.Block, Line: keep.GetValueOrDefault(i)))
            .Where(x => x.Line is not null)
            .GroupBy(x => x.Block).Select(g => g.Select(x => x.Line!).ToList()).ToList();
    }

    public const int MaxContextLevel = 3;

    /// <summary>
    /// The passage around a line at an expansion level: 0 is a few sentences either side (cut at
    /// sentence boundaries), then whole paragraphs, growing with each level.
    /// </summary>
    /// <param name="wide">Review's larger card (true) or a speaker's line list (false).</param>
    public static List<List<Line>> Around(ChapterAnalysis analysis, string lineId, int level, bool wide)
    {
        var (before, after) = (level, wide) switch
        {
            (<= 1, true) => (4, 3),
            (2, true) => (8, 6),
            (_, true) => (15, 12),
            (<= 1, false) => (2, 2),
            (2, false) => (5, 4),
            _ => (10, 8),
        };
        var blocks = Bilgames.Core.BookService.ContextBlocks(analysis, lineId, before, after);
        return level == 0 ? Trim(blocks, lineId, wide ? 520 : 260, wide ? 380 : 200) : blocks;
    }

    /// <summary>Whether the next level would actually show more (false at the chapter's edges).</summary>
    public static bool HasMore(ChapterAnalysis analysis, string lineId, int level, bool wide) =>
        level < MaxContextLevel && Chars(Around(analysis, lineId, level + 1, wide)) > Chars(Around(analysis, lineId, level, wide));

    private static int Chars(List<List<Line>> blocks) => blocks.Sum(b => b.Sum(l => l.Text.Length));

    private static Line WithText(Line l, string text) => new()
    {
        Id = l.Id, BlockId = l.BlockId, Text = text, IsDialogue = l.IsDialogue, SpeakerId = l.SpeakerId,
        Unsure = l.Unsure, Reviewed = l.Reviewed, Cue = l.Cue,
    };

    private static readonly System.Text.RegularExpressions.Regex SentenceEnd = new(@"(?<=[.!?…][”’""']?)\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string TailSentences(string text, int budget)
    {
        var sentences = SentenceEnd.Split(text);
        var picked = new List<string>();
        int used = 0;
        for (int i = sentences.Length - 1; i >= 0; i--)
        {
            if (picked.Count > 0 && used + sentences[i].Length > budget) break;
            picked.Insert(0, sentences[i]);
            used += sentences[i].Length;
        }
        return string.Join(" ", picked);
    }

    private static string HeadSentences(string text, int budget)
    {
        var sentences = SentenceEnd.Split(text);
        var picked = new List<string>();
        int used = 0;
        foreach (var s in sentences)
        {
            if (picked.Count > 0 && used + s.Length > budget) break;
            picked.Add(s);
            used += s.Length;
        }
        return string.Join(" ", picked);
    }

    /// <summary>Paragraphs of lines → coloured runs. Unsure lines (read by the narrator for now) stay neutral.</summary>
    public static IReadOnlyList<IReadOnlyList<ContextRun>> Runs(IEnumerable<IEnumerable<Line>> blocks, CastSheet cast, string? targetLineId)
    {
        var result = new List<IReadOnlyList<ContextRun>>();
        foreach (var block in blocks)
        {
            var runs = new List<ContextRun>();
            foreach (var l in block)
            {
                var member = l.IsDialogue && !(l.Unsure && !l.Reviewed) ? cast.Find(l.SpeakerId) : null;
                bool narrator = member is null || member.IsNarrator;
                runs.Add(new ContextRun
                {
                    LineId = l.Id,
                    Text = l.Text,
                    Color = l.Id == targetLineId ? null : narrator ? (l.IsDialogue ? "#ECEEF1" : null) : member!.Color,
                    Speaker = narrator ? (l.IsDialogue && l.Unsure && !l.Reviewed ? "Unsure" : "Narrator") : member!.Name,
                    IsTarget = l.Id == targetLineId,
                });
            }
            result.Add(runs);
        }
        return result;
    }

    /// <summary>The speakers that appear in a context, for the colour legend.</summary>
    public static List<LegendEntry> Legend(IEnumerable<IEnumerable<Line>> blocks, CastSheet cast)
    {
        var seen = new List<LegendEntry>();
        foreach (var l in blocks.SelectMany(b => b).Where(l => l.IsDialogue && !(l.Unsure && !l.Reviewed)))
        {
            var m = cast.Find(l.SpeakerId);
            if (m is null) continue;
            var name = m.IsNarrator ? "Narrator (speaking)" : m.Name;
            var color = m.IsNarrator ? "#ECEEF1" : m.Color;
            if (seen.Any(s => s.Name == name)) continue;
            seen.Add(new LegendEntry { Name = name, Color = color });
        }
        return seen;
    }

    public static List<PickerItem> SpeakerItems(CastSheet cast, string? currentId) =>
        cast.Members
            .OrderByDescending(m => m.IsNarrator).ThenByDescending(m => m.LineCount)
            .Select(m => new PickerItem
            {
                Id = m.Id,
                Title = m.Name,
                Detail = m.IsNarrator ? "Narration and unknown speakers" : $"{m.LineCount:N0} lines · voice {VoiceResolver.NameOf(m.VoiceId, AppServices.Engine.Voices)}",
                Color = m.Color,
                Initials = m.Initials,
                IsCurrent = m.Id == currentId,
                Keywords = string.Join(" ", m.Aliases),
            }).ToList();

    /// <summary>
    /// Every voice, filed under Gender › Nationality for browsing, with voices already given to
    /// someone else in this book marked "In use" (and who has them).
    /// </summary>
    /// <param name="forMemberId">The speaker being changed: their own voice is "Current", not "In use".</param>
    public static List<PickerItem> VoiceItems(string? currentVoiceId, CastSheet? cast = null, string? forMemberId = null)
    {
        var users = (cast?.Members ?? new List<CastMember>())
            .Where(m => m.Id != forMemberId && (m.IsNarrator || m.LineCount > 0 || m.UserAdded) && !string.IsNullOrEmpty(m.VoiceId))
            .GroupBy(m => m.VoiceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.IsNarrator).ThenByDescending(m => m.LineCount).Select(m => m.IsNarrator ? "the narrator" : m.Name).ToList());
        return AppServices.Engine.Voices.Select(v =>
        {
            users.TryGetValue(v.Id, out var who);
            string? usedBy = who is null ? null
                : (v.Id == currentVoiceId ? "Also used by " : "Used by ") + (who.Count <= 3 ? Join(who) : $"{string.Join(", ", who.Take(2))} and {who.Count - 2} others");
            return new PickerItem
            {
                Id = v.Id,
                Title = v.DisplayName,
                Detail = $"{v.Description} · grade {v.Grade}",
                IsCurrent = v.Id == currentVoiceId,
                Keywords = v.Id,
                Path = new[] { v.Gender == Gender.Female ? "Female" : "Male", v.Accent },
                Badge = who is null ? null : "In use",
                BadgeDetail = usedBy,
            };
        }).ToList();
    }

    private static string Join(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}

public sealed class LegendEntry
{
    public string Name { get; init; } = "";
    public string Color { get; init; } = "";
}

/// <summary>Tone labels and picker rows shared by Review and the speaker line list.</summary>
public static class ToneText
{
    public static string Label(string? cue) => Bilgames.Core.Analysis.Tones.Label(cue);

    public static string Source(CueSource from, string? cue) => from switch
    {
        _ when cue is null && from != CueSource.User => "No tone detected",
        CueSource.Tag => "From the dialogue tag",
        CueSource.Context => "Guessed from context",
        CueSource.User => "Set by you",
        _ => "",
    };

    public static List<PickerItem> Items(string? current)
    {
        var id = Bilgames.Core.Analysis.Tones.Normalize(current);
        var list = new List<PickerItem> { new() { Id = "", Title = "Neutral", Detail = "Normal reading", IsCurrent = id is null } };
        list.AddRange(Bilgames.Core.Analysis.Tones.All.Select(t => new PickerItem { Id = t.Id, Title = t.Label, Detail = t.Description, IsCurrent = t.Id == id }));
        return list;
    }
}
