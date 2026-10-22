using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Bilgames.Core.Analysis;
using Bilgames.Core.Model;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Assist;

/// <summary>A passage shown to the model: its text with numbered dialogue lines, and what each number means.</summary>
public sealed record PromptPassage(string Text, IReadOnlyDictionary<int, Line> Numbered, int TargetNumber);

/// <summary>
/// Everything the assistant says to the model. Kept in one place so prompts can be read and tuned
/// together. The model sees the passage with each dialogue line numbered and the app's current
/// guess beside it (as a suggestion, never as fact), plus the cast list; it answers in JSON whose
/// schema only allows real cast ids, so it can't invent someone without saying so.
/// </summary>
public static class AssistantPrompts
{
    public const string NewSpeaker = "new";
    private const int MaxCast = 60;

    public static readonly string[] ToneIds = new[] { "neutral" }.Concat(Tones.All.Select(t => t.Id)).ToArray();

    public static string SystemPrompt =>
        """
        You help turn novels into full-cast audiobooks by deciding who speaks each line of dialogue.
        Read the passage closely: dialogue tags ("said Alice"), who is being addressed, turn-taking in
        conversations, actions next to the quote, and what each character knows or would say.
        The app's current guess is shown next to each line as a hint; it is often right but can be wrong.
        Rules:
        - Answer with a speaker id from the cast list. A character's thoughts, and what they say to
          themselves, belong to that character ("thought Alice" means Alice). Use "narrator" only when no
          character says it: a sign, a letter or inscription being read, a quoted title or phrase, or the
          narrator speaking in the first person.
        - Use "new" with newName only for a character who speaks but is missing from the cast list.
        - confidence is your honest probability (0 to 1) that the speaker is right. Use high values only
          when the text makes it clear.
        - tone is how the line is delivered. Choose a tone other than "neutral" when the words or the
          narration make the delivery clear: a tag or action ("she whispered", "he snapped", "sobbed"),
          panic or urgency, shouting. Calm thoughts and ordinary speech are "neutral".
          toneConfidence is how sure you are of the tone.
        - Lines shown as [· ...] are context only: don't answer for them.
        - Reply with compact JSON on a single line, no extra spaces.
        """;

    // ---------- the passage ----------

    /// <summary>
    /// Renders blocks as paragraphs, numbering the dialogue lines of <paramref name="numbered"/> (all of
    /// them when null) as [n] and marking the target, if any, as [n ★].
    /// </summary>
    public static PromptPassage Passage(IReadOnlyList<List<Line>> blocks, CastSheet cast, string? targetLineId = null, ISet<string>? numbered = null)
    {
        var sb = new StringBuilder();
        var map = new Dictionary<int, Line>();
        int n = 0, target = -1;
        foreach (var block in blocks)
        {
            foreach (var line in block)
            {
                if (!line.IsDialogue)
                {
                    sb.Append(line.Text);
                    continue;
                }
                if (numbered is not null && !numbered.Contains(line.Id) && line.Id != targetLineId)
                {
                    sb.Append("[· ").Append(Hint(line, cast)).Append("] ").Append(line.Text);
                    continue;
                }
                n++;
                map[n] = line;
                bool isTarget = line.Id == targetLineId;
                if (isTarget) target = n;
                sb.Append('[').Append(n.ToString(CultureInfo.InvariantCulture));
                if (isTarget) sb.Append(" ★");
                sb.Append(" | ").Append(Hint(line, cast)).Append("] ").Append(line.Text);
            }
            sb.Append("\n\n");
        }
        return new PromptPassage(sb.ToString().TrimEnd(), map, target);
    }

    private static string Hint(Line line, CastSheet cast)
    {
        var who = line.SpeakerId == CastMember.NarratorId ? "narrator" : $"{line.SpeakerId} {cast.Find(line.SpeakerId)?.Name}".TrimEnd();
        if (line.Reviewed) return $"{who}, confirmed by the reader";
        return line.Unsure ? $"{who}?" : who;
    }

    /// <summary>The cast as the model sees it: id, name, other names, gender, how much they speak.</summary>
    public static string CastList(CastSheet cast, IEnumerable<string>? mustInclude = null)
    {
        var include = mustInclude?.ToHashSet() ?? new HashSet<string>();
        var members = cast.Members.Where(m => !m.IsNarrator)
            .OrderByDescending(m => include.Contains(m.Id)).ThenByDescending(m => m.LineCount)
            .Take(MaxCast).OrderByDescending(m => m.LineCount).ThenBy(m => m.Name, StringComparer.Ordinal);
        var sb = new StringBuilder("narrator: the narrator (not a character)\n");
        foreach (var m in members)
        {
            sb.Append(m.Id).Append(": ").Append(m.Name);
            if (m.Aliases.Count > 0) sb.Append(" (also: ").Append(string.Join(", ", m.Aliases.Take(6))).Append(')');
            if (m.Gender != Gender.Unknown) sb.Append(", ").Append(m.Gender == Gender.Female ? "female" : "male");
            sb.Append(", ").Append(m.LineCount.ToString(CultureInfo.InvariantCulture)).Append(" lines\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The ids the model may answer with (cast members shown, narrator, or "new").</summary>
    public static JsonArray SpeakerEnum(CastSheet cast, IEnumerable<string>? mustInclude = null)
    {
        var include = mustInclude?.ToHashSet() ?? new HashSet<string>();
        var ids = cast.Members.Where(m => !m.IsNarrator)
            .OrderByDescending(m => include.Contains(m.Id)).ThenByDescending(m => m.LineCount).Take(MaxCast).Select(m => m.Id);
        return new JsonArray(new[] { "narrator", NewSpeaker }.Concat(ids).Select(id => (JsonNode)id).ToArray());
    }

    // ---------- one line (Review) ----------

    public static string ReviewRequest(PromptPassage passage, string castList, string? title, int contextRequests, int maxContextRequests) =>
        $"""
        Cast:
        {castList}

        Book: {title}

        Passage (dialogue lines are numbered; the line to decide is marked ★):
        {passage.Text}

        Who speaks line {passage.TargetNumber.ToString(CultureInfo.InvariantCulture)} ★, and in what tone?
        {(contextRequests < maxContextRequests
            ? "If the passage doesn't contain enough to decide, set needMoreContext to true and you'll be shown more of the surrounding text."
            : "This is all the context available; decide as well as you can and reflect any doubt in confidence.")}
        """;

    public static JsonObject ReviewSchema(JsonArray speakers, bool allowMoreContext) => Obj(
        ("speaker", Enum(speakers)),
        ("newName", new JsonObject { ["type"] = "string", ["maxLength"] = 60 }),
        ("confidence", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }),
        ("tone", Enum(new JsonArray(ToneIds.Select(t => (JsonNode)t).ToArray()))),
        ("toneConfidence", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }),
        ("needMoreContext", new JsonObject { ["type"] = "boolean", ["enum"] = allowMoreContext ? new JsonArray(true, false) : new JsonArray(false) }),
        ("reason", new JsonObject { ["type"] = "string", ["maxLength"] = 200 }))
        .Required("speaker", "confidence", "tone", "toneConfidence", "needMoreContext", "reason");

    // ---------- a passage (whole book) ----------

    public static string PassageRequest(PromptPassage passage, string castList, string? title, string chapterTitle) =>
        $"""
        Cast:
        {castList}

        Book: {title}
        Chapter: {chapterTitle}

        Passage (dialogue lines to decide are numbered, with the app's current guess):
        {passage.Text}

        Answer for every numbered line whose hint ends in "?" (the app is unsure), and for any other numbered
        line where you disagree with the hint's speaker. Leave out numbered lines where the hint is right.
        """;

    public static JsonObject PassageSchema(JsonArray speakers, int count)
    {
        var item = Obj(
            ("n", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = count }),
            ("speaker", Enum(speakers)),
            ("newName", new JsonObject { ["type"] = "string", ["maxLength"] = 60 }),
            ("confidence", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }),
            ("tone", Enum(new JsonArray(ToneIds.Select(t => (JsonNode)t).ToArray()))),
            ("toneConfidence", new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }))
            .Required("n", "speaker", "confidence", "tone", "toneConfidence");
        return Obj(("lines", new JsonObject { ["type"] = "array", ["items"] = item, ["minItems"] = 0, ["maxItems"] = count }))
            .Required("lines");
    }

    // ---------- characters (for choosing voices) ----------

    public static string DescribeRequest(string? title, IReadOnlyList<(CastMember Member, IReadOnlyList<string> Samples)> characters)
    {
        var sb = new StringBuilder();
        sb.Append("Book: ").Append(title).Append("\n\nCharacters, with some of their lines and narration that mentions them:\n");
        foreach (var (m, samples) in characters)
        {
            sb.Append("\n").Append(m.Id).Append(": ").Append(m.IsNarrator ? "the narrator" : m.Name)
              .Append(", ").Append(m.LineCount.ToString(CultureInfo.InvariantCulture)).Append(" lines\n");
            foreach (var s in samples) sb.Append(s.StartsWith('“') || s.StartsWith('"') ? "  - says: " : "  - narration: ").Append(s).Append('\n');
        }
        sb.Append("""

            For the book, give the accent of its setting (british, american or other). For each character
            (and the narrator), give their gender (from pronouns and descriptions in the narration; "unknown"
            if the text doesn't say), their age group, the accent they'd most likely have, and a few words
            describing how they sound (for example "nervous, hurried", "slow and pompous").
            """);
        return sb.ToString();
    }

    public static JsonObject DescribeSchema(IReadOnlyList<CastMember> characters)
    {
        var item = Obj(
            ("id", Enum(new JsonArray(characters.Select(c => (JsonNode)c.Id).ToArray()))),
            ("gender", Enum(new JsonArray("female", "male", "unknown"))),
            ("age", Enum(new JsonArray("child", "young", "adult", "old", "unknown"))),
            ("accent", Enum(new JsonArray("british", "american", "other", "unknown"))),
            ("sound", new JsonObject { ["type"] = "string", ["maxLength"] = 60 }))
            .Required("id", "gender", "age", "accent", "sound");
        return Obj(
                ("setting", Enum(new JsonArray("british", "american", "other"))),
                ("characters", new JsonObject { ["type"] = "array", ["items"] = item, ["minItems"] = characters.Count, ["maxItems"] = characters.Count }))
            .Required("setting", "characters");
    }

    // ---------- JSON schema helpers ----------

    private static JsonObject Enum(JsonArray values) => new() { ["type"] = "string", ["enum"] = values };

    private static JsonObject Obj(params (string Name, JsonNode Schema)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in props) properties[name] = schema;
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
    }

    private static JsonObject Required(this JsonObject schema, params string[] names)
    {
        schema["required"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        return schema;
    }
}
