using System.Text.RegularExpressions;
using Bilgames.Core.Model;

namespace Bilgames.Core.Analysis;

/// <summary>Book-wide list of speaking characters and the name forms that refer to each.</summary>
internal sealed partial class CharacterRegistry
{
    internal sealed class Character
    {
        public string Id = "";
        public string Name = "";
        public Dictionary<string, int> Forms = new(StringComparer.Ordinal);
        public int TagCount;
        public int MalePronouns, FemalePronouns;
        public bool IsDescriptor;
        public Gender Gender = Gender.Unknown;
    }

    public List<Character> Characters { get; } = new();
    private readonly Dictionary<string, Character> _alias = new(StringComparer.OrdinalIgnoreCase);
    private Regex? _mentionRx;

    /// <summary>Raw tag subjects collected in the first pass: form → count.</summary>
    private readonly Dictionary<string, (int Count, bool Descriptor)> _raw = new(StringComparer.Ordinal);

    public void AddTagSubject(string form, bool descriptor)
    {
        _raw[form] = _raw.TryGetValue(form, out var v) ? (v.Count + 1, descriptor) : (1, descriptor);
    }

    /// <summary>Clusters raw name forms into characters ("Darcy" + "Mr. Darcy", "Jane" + "Jane Bennet").</summary>
    public void Consolidate()
    {
        // Longest names first; titled forms ("Mr. Bingley", "Miss Bingley") before bare ones ("Bingley"), so a
        // bare surname can't glue two differently titled people into one cluster.
        var forms = _raw.OrderByDescending(kv => NameRules.Core(kv.Key).Split(' ').Length)
            .ThenBy(kv => TitleOf(kv.Key) is null ? 1 : 0)
            .ThenByDescending(kv => kv.Value.Count).ToList();
        foreach (var (form, (count, descriptor)) in forms)
        {
            var target = descriptor ? null : FindCluster(form);
            if (target is null)
            {
                target = new Character { Id = $"ch{Characters.Count}", IsDescriptor = descriptor };
                Characters.Add(target);
            }
            target.Forms[form] = target.Forms.GetValueOrDefault(form) + count;
            target.TagCount += count;
        }

        foreach (var c in Characters)
        {
            // Display the fullest form that is used more than once (or the most used one).
            c.Name = c.Forms.Where(f => f.Value >= 2).OrderByDescending(f => NameRules.Core(f.Key).Split(' ').Length).ThenByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault()
                     ?? c.Forms.OrderByDescending(f => f.Value).First().Key;
        }
        BuildAliases();
    }

    private Character? FindCluster(string form)
    {
        var title = TitleOf(form);
        var tg = title is null ? Gender.Unknown : NameRules.TitleGender(title);
        var core = NameRules.Core(form).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (core.Length == 0) return null;

        var matches = new List<Character>();
        var formGender = NameRules.GuessFromName(form);
        foreach (var c in Characters.Where(c => !c.IsDescriptor))
        {
            // The whole cluster must be compatible, not just the one form that matches: "Mr. Bingley" must
            // not join {Miss Bingley, Bingley}, and "Mrs. Cratchit" must not join {Bob Cratchit}.
            bool conflict = false;
            foreach (var existing in c.Forms.Keys)
            {
                var eg = NameRules.GuessFromName(existing);
                if (formGender != Gender.Unknown && eg != Gender.Unknown && formGender != eg) { conflict = true; break; }
                var et = TitleOf(existing);
                if (title is not null && et is not null && !TitlesCompatible(title, et)) { conflict = true; break; }
            }
            if (conflict) continue;
            foreach (var existing in c.Forms.Keys)
            {
                var eTitle = TitleOf(existing);
                var eg = eTitle is null ? Gender.Unknown : NameRules.TitleGender(eTitle);
                if (tg != Gender.Unknown && eg != Gender.Unknown && tg != eg) continue; // Mr. vs Mrs. Bennet
                if (title is not null && eTitle is not null && !TitlesCompatible(title, eTitle)) continue;
                var eCore = NameRules.Core(existing).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Every word of the shorter core name appears in the longer one.
                if (core.All(w => eCore.Contains(w, StringComparer.OrdinalIgnoreCase)))
                {
                    // "Bennet" alone must not swallow "Jane Bennet" when a title disambiguates nothing — unless only
                    // one character in the book carries that surname ("Holmes" → "Sherlock Holmes").
                    if (title is null && core.Length == 1 && eCore.Length > 1 && !eCore[0].Equals(core[0], StringComparison.OrdinalIgnoreCase)
                        && SurnameOwners(core[0]) != 1) continue;
                    matches.Add(c);
                    break;
                }
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>How many characters (so far) use this word in any of their name forms.</summary>
    private int SurnameOwners(string word) =>
        Characters.Count(c => !c.IsDescriptor && c.Forms.Keys.Any(f =>
            NameRules.Core(f).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(word, StringComparer.OrdinalIgnoreCase)));

    private static bool TitlesCompatible(string a, string b)
    {
        a = a.TrimEnd('.'); b = b.TrimEnd('.');
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var married = new[] { "Mrs", "Lady", "Madame", "Madam" };
        var unmarried = new[] { "Miss", "Mademoiselle" };
        // Miss Bennet and Mrs. Bennet are different people.
        return !(married.Contains(a, StringComparer.OrdinalIgnoreCase) && unmarried.Contains(b, StringComparer.OrdinalIgnoreCase)
                 || unmarried.Contains(a, StringComparer.OrdinalIgnoreCase) && married.Contains(b, StringComparer.OrdinalIgnoreCase));
    }

    private static string? TitleOf(string form)
    {
        var first = form.Split(' ')[0];
        return NameRules.IsTitle(first) ? first : null;
    }

    private void BuildAliases()
    {
        _alias.Clear();
        var tokenOwners = new Dictionary<string, HashSet<Character>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Characters)
        {
            foreach (var f in c.Forms.Keys)
            {
                _alias[f] = c;
                if (c.IsDescriptor) { _alias[f.ToLowerInvariant()] = c; continue; }
                foreach (var tok in NameRules.Core(f).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!tokenOwners.TryGetValue(tok, out var set)) tokenOwners[tok] = set = new();
                    set.Add(c);
                }
            }
        }
        // A single name word ("Elizabeth", "Darcy") refers to a character when only one character uses it.
        foreach (var (tok, owners) in tokenOwners)
            if (owners.Count == 1 && tok.Length > 2) _alias.TryAdd(tok, owners.First());

        // Proper names match case-sensitively (so "Will" never matches "will"); descriptors ("the major") match any case.
        var names = _alias.Where(kv => kv.Key.Length > 1 && !kv.Value.IsDescriptor).Select(kv => kv.Key).Distinct(StringComparer.Ordinal)
            .OrderByDescending(k => k.Length).Select(Regex.Escape).ToList();
        var descs = _alias.Where(kv => kv.Value.IsDescriptor).Select(kv => kv.Key.ToLowerInvariant()).Distinct()
            .OrderByDescending(k => k.Length).Select(Regex.Escape).ToList();
        _mentionRx = names.Count > 0 ? new Regex(@"(?<![\w’'])(" + string.Join("|", names) + @")(?![\w’'])", RegexOptions.CultureInvariant) : null;
        _descRx = descs.Count > 0 ? new Regex(@"(?<![\w’'])(" + string.Join("|", descs) + @")(?![\w’'])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) : null;
    }

    private Regex? _descRx;

    public Character? Lookup(string form)
    {
        if (_alias.TryGetValue(form, out var c)) return c;
        var core = NameRules.Core(form);
        return core.Length > 0 && _alias.TryGetValue(core, out c) ? c : null;
    }

    /// <summary>Characters mentioned in a stretch of narration, in order of appearance.</summary>
    public IEnumerable<(Character Character, int Index, int Length)> Mentions(string text)
    {
        var found = new List<(Character, int, int)>();
        foreach (var rx in new[] { _mentionRx, _descRx })
        {
            if (rx is null) continue;
            foreach (Match m in rx.Matches(text))
                if (_alias.TryGetValue(m.Value, out var c)) found.Add((c, m.Index, m.Length));
        }
        return found.OrderBy(f => f.Item2);
    }

    /// <summary>Adds pronoun evidence: "he"/"she" shortly after a mention of a character.</summary>
    public void CountPronouns(string narration)
    {
        if (_mentionRx is null && _descRx is null) return;
        var mentions = Mentions(narration).ToList();
        for (int i = 0; i < mentions.Count; i++)
        {
            int start = mentions[i].Index;
            int end = i + 1 < mentions.Count ? mentions[i + 1].Index : Math.Min(narration.Length, start + 90);
            if (end <= start) continue;
            var window = narration[start..Math.Min(end, start + 90)];
            foreach (Match w in PronounRx().Matches(window))
            {
                var p = w.Value.ToLowerInvariant();
                if (p is "he" or "him" or "his" or "himself") mentions[i].Character.MalePronouns++;
                else mentions[i].Character.FemalePronouns++;
            }
        }
    }

    public void ResolveGenders()
    {
        foreach (var c in Characters)
        {
            var g = c.IsDescriptor ? SpeechTags.DescriptorGender(c.Name) : NameRules.GuessFromName(c.Name);
            if (g == Gender.Unknown)
            {
                foreach (var f in c.Forms.Keys)
                {
                    g = NameRules.GuessFromName(f);
                    if (g != Gender.Unknown) break;
                }
            }
            if (g == Gender.Unknown && c.MalePronouns + c.FemalePronouns >= 3)
            {
                if (c.MalePronouns >= 2 * c.FemalePronouns) g = Gender.Male;
                else if (c.FemalePronouns >= 2 * c.MalePronouns) g = Gender.Female;
            }
            c.Gender = g;
        }
    }

    [GeneratedRegex(@"\b(he|him|his|himself|she|her|hers|herself)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PronounRx();
}
