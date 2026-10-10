using System.Text.RegularExpressions;
using Bilgames.Core.Model;

namespace Bilgames.Core.Analysis;

public sealed record ToneInfo(string Id, string Label, string Description);

/// <summary>
/// How a tone is performed with the controls the speech engine has: speed, loudness (applied when
/// the chapter is assembled) and the pauses around the line. Kept subtle so a wrong guess isn't
/// overacted.
/// </summary>
public sealed record Delivery(float Speed, float GainDb, double PauseScale)
{
    public static readonly Delivery Neutral = new(1f, 0f, 1.0);
}

/// <summary>The fixed set of delivery tones, and how free-form cues ("coldly", "murmured") map onto them.</summary>
public static partial class Tones
{
    public const string Whispered = "whispered", Soft = "soft", Sad = "sad", Hesitant = "hesitant", Amused = "amused",
        Excited = "excited", Urgent = "urgent", Stern = "stern", Angry = "angry", Shouting = "shouting";

    public static IReadOnlyList<ToneInfo> All { get; } = new ToneInfo[]
    {
        new(Whispered, "Whispered", "Much quieter, a little slower"),
        new(Soft, "Soft", "Quieter and gentle"),
        new(Sad, "Sad", "Slower, quieter, longer pauses"),
        new(Hesitant, "Hesitant", "Slower with longer pauses"),
        new(Amused, "Amused", "A touch lighter and quicker"),
        new(Excited, "Excited", "Quicker and a little louder"),
        new(Urgent, "Urgent", "Fast with short pauses"),
        new(Stern, "Stern", "Measured and firm"),
        new(Angry, "Angry", "Louder and clipped"),
        new(Shouting, "Shouting", "Loudest, quick"),
    };

    private static readonly Dictionary<string, Delivery> Deliveries = new()
    {
        [Whispered] = new(0.93f, -8f, 1.15),
        [Soft] = new(0.96f, -3.5f, 1.1),
        [Sad] = new(0.92f, -2.5f, 1.3),
        [Hesitant] = new(0.93f, -1.5f, 1.35),
        [Amused] = new(1.02f, 0f, 1.0),
        [Excited] = new(1.06f, 1.5f, 0.85),
        [Urgent] = new(1.08f, 1f, 0.7),
        [Stern] = new(0.97f, 0.5f, 1.15),
        [Angry] = new(1.04f, 2f, 0.9),
        [Shouting] = new(1.05f, 3.5f, 0.85),
    };

    public static Delivery DeliveryFor(string? cue) =>
        Normalize(cue) is { } id && Deliveries.TryGetValue(id, out var d) ? d : Delivery.Neutral;

    public static string Label(string? cue) => All.FirstOrDefault(t => t.Id == Normalize(cue))?.Label ?? "Neutral";

    /// <summary>Synonyms: tag verbs' cues, adverb roots and older saved cues → one of <see cref="All"/>.</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        // whispered
        ["whisper"] = Whispered, ["hushed"] = Whispered, ["murmured"] = Whispered, ["breathless"] = Whispered, ["inaudible"] = Whispered,
        // soft
        ["quiet"] = Soft, ["gentle"] = Soft, ["tender"] = Soft, ["calm"] = Soft, ["kind"] = Soft, ["warm"] = Soft, ["low"] = Soft,
        ["soothing"] = Soft, ["patient"] = Soft, ["fond"] = Soft, ["affectionate"] = Soft, ["reassuring"] = Soft, ["slow"] = Soft,
        // sad
        ["sorrowful"] = Sad, ["mournful"] = Sad, ["miserable"] = Sad, ["wistful"] = Sad, ["sombre"] = Sad, ["somber"] = Sad,
        ["gloomy"] = Sad, ["tearful"] = Sad, ["weary"] = Sad, ["tired"] = Sad, ["sad"] = Sad, ["unhappy"] = Sad, ["glum"] = Sad,
        ["heavy"] = Sad, ["dull"] = Sad, ["hollow"] = Sad, ["grave"] = Sad, ["sorry"] = Sad, ["regretful"] = Sad, ["sobbing"] = Sad,
        // hesitant
        ["nervous"] = Hesitant, ["uncertain"] = Hesitant, ["timid"] = Hesitant, ["shy"] = Hesitant, ["anxious"] = Hesitant,
        ["awkward"] = Hesitant, ["doubtful"] = Hesitant, ["tentative"] = Hesitant, ["uneasy"] = Hesitant, ["fearful"] = Hesitant,
        ["afraid"] = Hesitant, ["hesitant"] = Hesitant, ["shaky"] = Hesitant, ["unsteady"] = Hesitant, ["sheepish"] = Hesitant,
        ["cautious"] = Hesitant, ["careful"] = Hesitant, ["weak"] = Hesitant, ["faint"] = Hesitant, ["stammering"] = Hesitant,
        // amused
        ["cheerful"] = Amused, ["happy"] = Amused, ["playful"] = Amused, ["wry"] = Amused, ["dry"] = Amused, ["sly"] = Amused,
        ["teasing"] = Amused, ["merry"] = Amused, ["light"] = Amused, ["mischievous"] = Amused, ["sardonic"] = Amused,
        ["sarcastic"] = Amused, ["amused"] = Amused, ["lazy"] = Amused, ["casual"] = Amused, ["innocent"] = Amused,
        ["mocking"] = Amused, ["ironic"] = Amused, ["jovial"] = Amused, ["genial"] = Amused, ["smug"] = Amused,
        // excited
        ["eager"] = Excited, ["delighted"] = Excited, ["enthusiastic"] = Excited, ["joyful"] = Excited, ["gleeful"] = Excited,
        ["triumphant"] = Excited, ["excited"] = Excited, ["breathlessly"] = Excited, ["proud"] = Excited, ["bright"] = Excited,
        ["jubilant"] = Excited, ["exultant"] = Excited, ["hopeful"] = Excited,
        // urgent
        ["hasty"] = Urgent, ["quick"] = Urgent, ["impatient"] = Urgent, ["hurried"] = Urgent, ["desperate"] = Urgent,
        ["frantic"] = Urgent, ["urgent"] = Urgent, ["insistent"] = Urgent, ["rapid"] = Urgent, ["breathlessly urgent"] = Urgent,
        // stern
        ["cold"] = Stern, ["stern"] = Stern, ["grim"] = Stern, ["firm"] = Stern, ["flat"] = Stern, ["curt"] = Stern,
        ["sharp"] = Stern, ["serious"] = Stern, ["solemn"] = Stern, ["icy"] = Stern, ["stiff"] = Stern, ["brusque"] = Stern,
        ["terse"] = Stern, ["dark"] = Stern, ["severe"] = Stern, ["deliberate"] = Stern, ["even"] = Stern,
        // angry
        ["furious"] = Angry, ["fierce"] = Angry, ["savage"] = Angry, ["harsh"] = Angry, ["irritable"] = Angry,
        ["irritated"] = Angry, ["indignant"] = Angry, ["bitter"] = Angry, ["hot"] = Angry, ["heated"] = Angry, ["angry"] = Angry,
        ["vicious"] = Angry, ["venomous"] = Angry, ["cross"] = Angry, ["annoyed"] = Angry, ["testy"] = Angry, ["accusing"] = Angry,
        ["menacing"] = Angry, ["threatening"] = Angry, ["contemptuous"] = Angry, ["scornful"] = Angry, ["disgusted"] = Angry,
        ["sneering"] = Angry, ["snide"] = Angry, ["resentful"] = Angry, ["exasperated"] = Angry, ["defiant"] = Angry,
        // shouting
        ["shouting"] = Shouting, ["loud"] = Shouting, ["shrill"] = Shouting, ["booming"] = Shouting,
    };

    /// <summary>Maps any cue onto the fixed set; null for neutral or unknown words.</summary>
    public static string? Normalize(string? cue)
    {
        if (string.IsNullOrWhiteSpace(cue)) return null;
        cue = cue.Trim().ToLowerInvariant();
        if (All.Any(t => t.Id == cue)) return cue;
        return Synonyms.TryGetValue(cue, out var id) ? id : null;
    }

    // ---------- guessing tone from context (no tag) ----------

    [GeneratedRegex(@"\b([A-Za-z])-\1", RegexOptions.IgnoreCase)]
    private static partial Regex Stammer();

    [GeneratedRegex(@"^(?:um+|uh+|er+|erm|well\W+um)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    private static readonly (string Tone, Regex Pattern)[] Beats =
    {
        // Qualified smiles and laughs first, so "a humorless laugh" isn't read as amusement.
        (Stern, new Regex(@"\b(humou?rless|mirthless|without (?:any )?humou?r|little humou?r|no humou?r|(?:tight|thin|grim|forced|cold|icy) (?:smile|laugh|grin)|tone was flat|flat tone|(?:smile|grin) (?:faded|vanished|slipped)|sobered)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Sad, new Regex(@"\b((?:sad|rueful|wistful|weary|tired) (?:smile|laugh)|sadness)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Whispered, new Regex(@"\b(lean(?:ed|ing|s)? (?:in )?close|under (?:his|her|their|my) breath|lower(?:ed|ing|s)? (?:his|her|their|my) voice|barely audible|in a whisper|whisper(?:ed|ing)?|hush(?:ed)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Sad, new Regex(@"\b(tears?|wept|weep(?:ing|s)?|sobb?(?:ed|ing|s)?|crying|choked|grief|mournful(?:ly)?|sorrow(?:ful)?|eyes (?:glistened|glistening|welled)|voice (?:broke|breaking|cracked)|sadly|heartbroken)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Angry, new Regex(@"\b(glar(?:ed|ing|es)|scowl(?:ed|ing|s)?|snarl(?:ed|ing|s)?|furious(?:ly)?|fury|rage|rag(?:ed|ing)|clench(?:ed|ing)|slamm(?:ed|ing)|seeth(?:ed|ing)|livid|gritted teeth|jaw (?:tightened|clenched)|angrily|anger)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Amused, new Regex(@"\b(grinn?(?:ed|ing|s)?|smil(?:ed|ing|es)|smirk(?:ed|ing|s)?|laugh(?:ed|ing|s)?|chuckl(?:ed|ing|es)|amusement|amused|eyes twinkl(?:ed|ing)|wink(?:ed|ing|s)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Hesitant, new Regex(@"\b(hesitat(?:ed|ing|es)|hesitantly|nervously|swallow(?:ed|ing)|trembl(?:ed|ing|es)|stammer(?:ed|ing)?|falter(?:ed|ing)|uncertainly|bit (?:his|her|their|my) lip|fidget(?:ed|ing))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Urgent, new Regex(@"\b(urgently|hurried(?:ly)?|rush(?:ed|ing)|panick?(?:ed|ing)?|frantic(?:ally)?|desperately|grabb(?:ed|ing))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Stern, new Regex(@"\b(coldly|sternly|frown(?:ed|ing|s)?|narrow(?:ed|ing) (?:his|her|their|my) eyes|flatly|icily|grimly)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (Excited, new Regex(@"\b(beam(?:ed|ing)|eagerly|excitedly|bounc(?:ed|ing)|eyes lit|grinning broadly|delighted(?:ly)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    /// <summary>
    /// Guesses a tone for a quote that has no telling tag, from the quote's own punctuation and
    /// capitals and from action beats in the narration of the same paragraph. Null when nothing
    /// clearly points to a tone (most lines).
    /// </summary>
    public static string? FromContext(string quote, string narration)
    {
        var q = quote.Trim().Trim('“', '”', '"', '‘', '’', '\'').Trim();
        if (q.Length == 0) return null;

        var words = Regex.Matches(q, @"[A-Za-z]{2,}").Select(m => m.Value).ToList();
        int caps = words.Count(w => w.All(char.IsUpper) && w != "OK" && w != "TV");
        int bangs = q.Count(c => c == '!');
        bool shout = (words.Count >= 2 && caps * 2 >= words.Count && caps >= 2) || (caps >= 1 && bangs >= 1 && words.Count <= 3) || q.Contains("!!") || q.Contains("?!") || q.Contains("!?");
        if (shout) return Shouting;

        string? beat = null;
        if (narration.Length > 0)
            foreach (var (tone, pattern) in Beats)
                if (pattern.IsMatch(narration)) { beat = tone; break; }

        bool exclaims = q.EndsWith('!') || q.EndsWith("!’") || bangs > 0 && q.TrimEnd('—', '…', '.').EndsWith('!');
        if (beat is not null)
        {
            // "!" sharpens a beat: an angry glare plus "!" is anger; a grin plus "!" is excitement.
            if (exclaims && beat == Amused) return Excited;
            return beat;
        }

        if (Stammer().IsMatch(q) || Filler().IsMatch(q)) return Hesitant;
        int ellipses = Regex.Matches(q, @"…|\.\.\.").Count;
        if (ellipses >= 2) return Hesitant;
        if (exclaims) return Excited;
        return null;
    }
}
