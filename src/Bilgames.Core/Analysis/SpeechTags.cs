using System.Text.RegularExpressions;

namespace Bilgames.Core.Analysis;

public enum SubjectKind
{
    Name,
    Descriptor,
    FirstPerson,
    Pronoun,
    Unknown,
}

/// <summary>A dialogue tag found in narration: who (as written) and how.</summary>
public sealed record TagHit(SubjectKind Kind, string Subject, string Verb, string? Adverb, bool BeforeQuote);

/// <summary>Finds "said Mrs. Bennet", "the major said", "Jim says:" and similar tags.</summary>
public static partial class SpeechTags
{
    private const string Verbs =
        "said|says|say|asked|asks|replied|replies|answered|answers|cried|cries|shouted|shouts|whispered|whispers|muttered|mutters|" +
        "exclaimed|exclaims|returned|continued|continues|added|adds|observed|observes|remarked|remarks|called|calls|yelled|yells|" +
        "murmured|murmurs|began|begins|repeated|repeats|insisted|insists|protested|protests|explained|explains|demanded|demands|" +
        "inquired|inquires|enquired|enquires|sighed|sighs|laughed|laughs|snapped|snaps|screamed|screams|roared|roars|growled|growls|" +
        "hissed|hisses|groaned|groans|sobbed|sobs|gasped|gasps|stammered|stammers|stuttered|stutters|chuckled|chuckles|interrupted|" +
        "interrupts|agreed|agrees|suggested|suggests|responded|responds|retorted|retorts|declared|declares|announced|announces|" +
        "admitted|admits|begged|begs|pleaded|pleads|warned|warns|wondered|wonders|went on|goes on|rejoined|urged|urges|commanded|" +
        "ordered|sang|sings|grumbled|grumbles|snarled|breathed|drawled|ventured|faltered|bellowed|shrieked|wailed|whimpered|" +
        "mumbled|mumbles|pursued|resumed|told|tells|put in|cut in|corrected|conceded|confessed|persisted|soothed|teased|blurted";

    private const string Titles =
        "Mr|Mrs|Ms|Miss|Dr|Sir|Lady|Lord|Aunt|Uncle|Captain|Colonel|Major|General|Lieutenant|Sergeant|Father|Mother|Brother|Sister|" +
        "Professor|Judge|Doctor|Madame|Madam|Monsieur|Mademoiselle|Signor|Signora|Count|Countess|Duke|Duchess|King|Queen|Prince|Princess|" +
        "Widow|Old|Young|Little|Cousin|Saint|St|Reverend|Rev|M";

    // A proper name: optional title, then 1–3 capitalized words.
    private const string Name = @"(?:(?:" + Titles + @")\.?\s+){0,2}[A-Z][\w’'-]*(?:\s+(?:[A-Z][\w’'-]*|de|van|von|la|le)){0,2}";
    private const string Pronoun = @"(?:he|she|I|we|they|you|He|She|We|They|You)";
    // Determiner + up to three lowercase words; Classify trims it back to the head noun.
    private const string Desc = @"(?:the|his|her|my|our|their|an?|that|this|The|His|Her|My|Our|Their|A|An|That|This)\s+[a-z][a-z-]+(?:\s+[a-z][a-z-]+){0,2}";
    private const string Subject = @"(?<subj>\b(?:" + Pronoun + "|" + Desc + "|" + Name + "))";
    private const string Verb = @"(?<verb>" + Verbs + ")";
    private const string Adverb = @"(?:\s+(?<adv>[a-z]+ly))?";

    // "…,” said the major" / "…,” says I"
    [GeneratedRegex(@"^[\s,;:—–-]*" + Verb + @"\s+" + Subject + Adverb + @"\b", RegexOptions.CultureInvariant)]
    private static partial Regex AfterVerbFirst();

    // "…,” the major said" / "…” he said" / "…” Mrs. Bennet impatiently cried"
    [GeneratedRegex(@"^[\s,;:—–-]*" + Subject + @"(?:\s+(?<adv>[a-z]+ly))?\s+" + Verb + @"\b" + Adverb, RegexOptions.CultureInvariant)]
    private static partial Regex AfterSubjectFirst();

    // "Then he says, “…" / "Pretty soon Jim says: “…" / "Elizabeth said to her sister, “…"
    [GeneratedRegex(Subject + @"(?:\s+(?<adv>[a-z]+ly))?\s+" + Verb + @"(?:\s+[\w’'-]+){0,5}?\s*[,:—–-]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BeforeSubjectFirst();

    // "…and then says I: “…"
    [GeneratedRegex(Verb + @"\s+" + Subject + @"\s*[,:—–-]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BeforeVerbFirst();

    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "The", "Then", "But", "And", "So", "Well", "Yes", "No", "Oh", "Ah", "Now", "There", "Here", "It", "This", "That", "What", "When",
        "Why", "How", "Who", "Where", "If", "As", "At", "In", "On", "Of", "Or", "For", "To", "With", "After", "Before", "All", "Still",
        "Just", "Only", "Even", "Again", "Once", "Soon", "Presently", "Finally", "Suddenly", "Perhaps", "Maybe", "Indeed", "Instead",
        "Meanwhile", "Somebody", "Someone", "Everybody", "Everyone", "Nobody", "Anyone", "Anybody", "Something", "Nothing", "Everything",
        "Good", "Very", "Pretty", "Sure", "Thank", "Please", "Come", "Go", "Look", "Listen", "Let", "Do", "Don’t", "Don't", "Did",
        "Is", "Are", "Was", "Were", "Will", "Would", "Could", "Should", "Can", "May", "Might", "Must", "Shall", "Not", "Never", "Ever",
        "Always", "Sometimes", "Often", "Then,", "Hello", "Goodbye", "Good-bye", "God", "Heaven", "Christ",
        "Jesus", "Mister", "Missus", "One", "Two", "Three", "Both", "Each", "Every", "Some", "Many", "Most", "Such", "Other", "Another",
        "Chapter", "Part", "Book", "Volume", "Everybody", "Anyhow", "However", "Besides", "Though",
        "Although", "Because", "Since", "Until", "While", "Whilst", "Yet", "Nor", "Like", "Than", "Also", "Too", "Quite", "Rather",
        "Really", "Certainly", "Of course", "Right", "Wait", "Stop", "Hurry", "Quick", "Ha", "Hm", "Hmm", "Hey", "Hi", "Ay", "Aye",
        "Bymeby", "Directly", "Afterwards", "Later", "Next", "First", "Last", "Lastly", "Anyway", "Anyways", "Thereupon", "Thereafter",
        "Upon", "Into", "Onto", "From", "By", "Through", "Without", "Within", "Against", "Among", "Under", "Over", "Behind", "Beyond",
        "About", "Down", "Up", "Out", "Off", "Sudden", "Away", "Along", "Around", "Round", "Across",
    };

    private static readonly HashSet<string> NonCharacterDescriptors = new(StringComparer.OrdinalIgnoreCase)
    {
        "other", "others", "first", "second", "third", "latter", "former", "voice", "voices", "one", "ones", "rest", "same", "two", "three",
        "crowd", "people", "men", "women", "children", "boys", "girls", "telescreen", "radio", "loudspeaker", "sign", "letter", "paper", "note",
        "river", "wind", "sea", "hall", "line", "door", "bell", "clock", "conversation", "words", "word", "book", "story", "song",
        "time", "times", "bills", "day", "night", "morning", "evening", "way",
    };

    /// <summary>"the old" / "the dead": an adjective where the noun should be; the descriptor was cut off.</summary>
    private static readonly HashSet<string> AdjectiveHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "old", "young", "little", "dead", "poor", "dark", "pale", "new", "good", "bad", "great", "big", "small", "whole", "gay", "last",
        "next", "elder", "younger", "eldest", "youngest", "projected", "imperturbable", "frightened", "hollow-cheeked", "same", "very",
    };

    private static readonly HashSet<string> Possessives = new(StringComparer.OrdinalIgnoreCase) { "his", "her", "my", "our", "their", "your" };

    /// <summary>
    /// "cried her mother", "said his wife": a speaker named only relative to someone else. Not the
    /// narrator and not a character of its own; the analyzer resolves it from context or asks for review.
    /// </summary>
    public static bool IsRelative(TagHit tag) =>
        tag.Kind == SubjectKind.Unknown && tag.Subject.Split(' ') is { Length: >= 2 } w && Possessives.Contains(w[0]) && !NonCharacterDescriptors.Contains(w[^1]);

    public static TagHit? FindAfter(string narration) => Match(narration, AfterVerbFirst(), before: false) ?? Match(narration, AfterSubjectFirst(), before: false);

    public static TagHit? FindBefore(string narration) => Match(narration, BeforeSubjectFirst(), before: true) ?? Match(narration, BeforeVerbFirst(), before: true);

    private static TagHit? Match(string text, Regex rx, bool before)
    {
        var m = rx.Match(text);
        if (!m.Success) return null;
        var subj = m.Groups["subj"].Value.Trim();
        var verb = m.Groups["verb"].Value;
        var adv = m.Groups["adv"].Success ? m.Groups["adv"].Value : null;
        // "cried his wife impatiently": the adverb can end up inside a descriptor match.
        adv ??= subj.Split(' ').Skip(1).FirstOrDefault(w => w.Length > 4 && w.EndsWith("ly"));
        if (adv is "only" or "early" or "family" or "silly" or "holy" or "lovely" or "ugly" or "jolly" or "fly" or "reply" or "apply") adv = null;
        // When the subject comes first ("his hands together and says") it must sit right before the verb.
        var kind = Classify(ref subj, requireAdjacent: rx == BeforeSubjectFirst() || rx == AfterSubjectFirst());
        return kind is null ? null : new TagHit(kind.Value, subj, verb.ToLowerInvariant(), adv, before);
    }

    private static SubjectKind? Classify(ref string subj, bool requireAdjacent = false)
    {
        var words = subj.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Drop sentence adverbs in front ("So I", "By-and-by Jim", "Presently Mary").
        int lead = 0;
        while (lead < words.Length - 1 && char.IsUpper(words[lead + 1][0])
               && (NotNames.Contains(words[lead].TrimEnd(',', '.')) || IsHyphenatedPhrase(words[lead]))) lead++;
        words = words[lead..];
        if (words.Length == 1 && IsHyphenatedPhrase(words[0])) return null;
        subj = string.Join(' ', words);

        var lower = subj.ToLowerInvariant();
        if (lower is "i" or "we") return SubjectKind.FirstPerson;
        if (lower is "he" or "she") return SubjectKind.Pronoun;
        if (lower is "they" or "you") return SubjectKind.Unknown;

        var first = words[0];
        if (first.ToLowerInvariant() is "the" or "his" or "her" or "my" or "our" or "their" or "a" or "an" or "that" or "this")
        {
            // "the major looking at the priest" → "the major"; "the other appreciatively" → "the other".
            int cut = 1;
            while (cut < words.Length && cut <= 3 && !EndsNounPhrase(words[cut])) cut++;
            // "the duke he says": a resumptive pronoun between the subject and the verb (dialect).
            bool resumptive = cut < words.Length && words[cut] is "he" or "she";
            if (requireAdjacent && cut < words.Length && !words[cut].EndsWith("ly") && !resumptive) return null;
            words = words[..cut];
            if (words.Length < 2) return null;
            subj = string.Join(' ', words);
            var noun = words[^1];
            if (NonCharacterDescriptors.Contains(noun) || VerbSet.Contains(noun) || AdjectiveHeads.Contains(noun)) return SubjectKind.Unknown;
            if (first.ToLowerInvariant() is "a" or "an") return SubjectKind.Unknown; // "a voice said": an unnamed one-off
            if (first.ToLowerInvariant() is not "the") return SubjectKind.Unknown;  // "his wife": relative to someone; resolved by context
            subj = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(' ', words.Skip(1)));
            subj = "The " + subj;
            return SubjectKind.Descriptor;
        }

        // Proper names: drop trailing words that are actually the start of the next clause.
        var kept = new List<string>();
        foreach (var w in words)
        {
            var bare = w.TrimEnd('.', ',', '’', '\'');
            if (kept.Count > 0 && NotNames.Contains(bare)) break;
            kept.Add(w);
        }
        while (kept.Count > 0 && NotNames.Contains(kept[^1].TrimEnd('.', ','))) kept.RemoveAt(kept.Count - 1);
        if (kept.Count == 0) return null;
        if (kept.Count == 1 && NameRules.IsTitle(kept[0])) return null;
        var core = kept.Where(k => !NameRules.IsTitle(k)).ToList();
        if (core.Count == 0 || NotNames.Contains(core[0].TrimEnd('.', ','))) return null;
        if (core.All(k => k.TrimEnd('.', ',').Length < 2)) return null; // "M", "X": an initial, not a name
        subj = string.Join(' ', kept).TrimEnd(',', '.');
        if (subj.EndsWith("’s", StringComparison.Ordinal) || subj.EndsWith("'s", StringComparison.Ordinal)) return null; // possessive, not a speaker
        return SubjectKind.Name;
    }

    /// <summary>Maps a tag verb or adverb to one of the delivery tones (see <see cref="Tones"/>).</summary>
    public static string? CueFor(string verb, string? adverb)
    {
        if (adverb is { Length: > 4 } && Tones.Normalize(AdverbRoot(adverb)) is { } fromAdverb) return fromAdverb;
        return verb switch
        {
            "whispered" or "whispers" or "murmured" or "murmurs" or "breathed" or "muttered" or "mutters" or "mumbled" or "mumbles" => Tones.Whispered,
            "shouted" or "shouts" or "yelled" or "yells" or "screamed" or "screams" or "roared" or "roars" or "bellowed" or "shrieked" => Tones.Shouting,
            "cried" or "cries" or "exclaimed" or "exclaims" => Tones.Excited,
            "sobbed" or "sobs" or "wailed" or "whimpered" or "groaned" or "groans" or "sighed" or "sighs" => Tones.Sad,
            "laughed" or "laughs" or "chuckled" or "chuckles" or "teased" => Tones.Amused,
            "snarled" or "growled" or "growls" or "hissed" or "hisses" or "spat" => Tones.Angry,
            "snapped" or "snaps" or "retorted" => Tones.Stern,
            "stammered" or "stammers" or "stuttered" or "stutters" or "faltered" or "gasped" or "gasps" => Tones.Hesitant,
            "pleaded" or "begged" or "urged" => Tones.Urgent,
            _ => null,
        };
    }

    private static readonly HashSet<string> VerbSet = new(Verbs.Split('|'), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> PhraseBreakers = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "at", "and", "with", "in", "on", "of", "from", "as", "but", "for", "into", "over", "after", "before", "then", "who",
        "which", "that", "when", "while", "again", "too", "once", "now", "still", "back", "up", "down", "out", "softly", "quietly",
        "he", "she", "it", "they", "i", "is", "was", "were", "are", "had", "has", "have", "did", "does", "do", "would", "could", "should",
        "will", "never", "always", "just", "only", "so", "all", "then", "not", "also",
    };

    private static bool EndsNounPhrase(string w) =>
        PhraseBreakers.Contains(w) || w.EndsWith("ly") || w.EndsWith("ing") || VerbSet.Contains(w);

    /// <summary>"By-and-by", "Well-nigh": hyphenated phrases with lowercase parts aren't names.</summary>
    private static bool IsHyphenatedPhrase(string w)
    {
        var parts = w.Split('-');
        return parts.Length >= 2 && parts.Skip(1).Any(p => p.Length > 0 && char.IsLower(p[0]));
    }

    /// <summary>A whole narration paragraph that introduces the next one's speech: "Then he says:".</summary>
    public static TagHit? FindLeadIn(string paragraph)
    {
        var t = paragraph.TrimEnd();
        if (t.Length == 0 || t[^1] is not (':' or ',' or '—')) return null;
        return FindBefore(t) ?? CompoundSubject(t);
    }

    // "Then he drops down on his knees, and puts his hands together and says:" — the subject of the
    // last sentence carries through a chain of verbs to the speech verb at the end.
    [GeneratedRegex(@"(?:^|[.!?;]\s+)(?:(?:Then|So|And|But|Now|Pretty soon|By-and-by|Presently)\s+)?(?<subj>he|she|I|He|She|[A-Z][a-z’'-]+(?:\s+[A-Z][a-z’'-]+)?)\s+[a-z][^.!?;]*?\b(?<verb>" + Verbs + @")\s*[,:—]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CompoundRx();

    private static TagHit? CompoundSubject(string text)
    {
        var m = CompoundRx().Match(text);
        if (!m.Success) return null;
        var subj = m.Groups["subj"].Value;
        var kind = Classify(ref subj);
        return kind is SubjectKind.Pronoun or SubjectKind.Name or SubjectKind.FirstPerson
            ? new TagHit(kind.Value, subj, m.Groups["verb"].Value.ToLowerInvariant(), null, true)
            : null;
    }

    /// <summary>A short narration paragraph that only attributes the previous speech: "I says." / "said he."</summary>
    public static TagHit? FindTrailing(string paragraph)
    {
        if (TextUtilWords(paragraph) > 8) return null;
        return FindAfter(paragraph);
    }

    private static int TextUtilWords(string s) => Model.TextUtil.CountWords(s);

    private static readonly Dictionary<string, string> IrregularAdverbs = new()
    {
        ["gently"] = "gentle", ["simply"] = "simple", ["subtly"] = "subtle", ["idly"] = "idle", ["truly"] = "true", ["wholly"] = "whole",
        ["dully"] = "dull", ["fully"] = "full", ["shyly"] = "shy", ["slyly"] = "sly", ["dryly"] = "dry", ["drily"] = "dry", ["coyly"] = "coy",
    };

    /// <summary>"impatiently" → "impatient", "angrily" → "angry", "terribly" → "terrible".</summary>
    internal static string AdverbRoot(string adverb)
    {
        if (IrregularAdverbs.TryGetValue(adverb, out var r)) return r;
        if (adverb.EndsWith("ically")) return adverb[..^4];
        if (adverb.EndsWith("ily")) return adverb[..^3] + "y";
        if (adverb.EndsWith("bly")) return adverb[..^1] + "e";
        return adverb[..^2];
    }

    /// <summary>Gender implied by a descriptor noun ("the priest", "the nurse").</summary>
    public static Model.Gender DescriptorGender(string descriptor)
    {
        var noun = descriptor.Split(' ')[^1].ToLowerInvariant();
        return noun switch
        {
            "man" or "boy" or "father" or "priest" or "king" or "prince" or "husband" or "brother" or "son" or "uncle" or "gentleman"
                or "monk" or "duke" or "lord" or "fellow" or "lad" or "waiter" or "landlord" or "barman" or "sir" => Model.Gender.Male,
            "woman" or "girl" or "mother" or "nurse" or "queen" or "princess" or "wife" or "sister" or "daughter" or "aunt" or "lady"
                or "nun" or "duchess" or "widow" or "maid" or "landlady" or "waitress" or "girlfriend" or "matron" => Model.Gender.Female,
            _ => Model.Gender.Unknown,
        };
    }
}
