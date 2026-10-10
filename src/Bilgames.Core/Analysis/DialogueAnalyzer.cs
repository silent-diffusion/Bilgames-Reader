using Bilgames.Core.Model;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Analysis;

public sealed class AnalysisResult
{
    public List<ChapterAnalysis> Chapters { get; } = new();
    public CastSheet Cast { get; } = new();
}

/// <summary>
/// Rule-based, confidence-aware dialogue attribution. It decides *who* speaks each line; the
/// synthesis planner later decides how it sounds. Anything below <see cref="ConfidentThreshold"/>
/// is read by the narrator and queued for review instead of being guessed.
/// </summary>
public sealed class DialogueAnalyzer
{
    public const double ConfidentThreshold = 0.6;

    private readonly CharacterRegistry _registry = new();
    private List<Para> _run = new();
    private QuoteStyle _style;

    public static AnalysisResult Analyze(IReadOnlyList<ChapterText> chapters, string narratorVoice = VoiceCatalog.DefaultNarrator,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var analyzer = new DialogueAnalyzer();
        return analyzer.Run(chapters, narratorVoice, progress, ct);
    }

    private AnalysisResult Run(IReadOnlyList<ChapterText> chapters, string narratorVoice, IProgress<double>? progress, CancellationToken ct)
    {
        _style = QuoteSplitter.DetectStyle(chapters.SelectMany(c => c.Blocks).Where(b => b.Kind == BlockKind.Paragraph).Select(b => b.Text));

        // Pass 1: who is named in dialogue tags anywhere in the book.
        foreach (var ch in chapters)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var b in ch.Blocks.Where(b => b.Kind == BlockKind.Paragraph))
            {
                var split = QuoteSplitter.Split(b.Text, _style);
                var tags = split.HasQuote
                    ? TagsOf(split).Select(t => t.Tag)
                    : new[] { SpeechTags.FindLeadIn(b.Text) ?? SpeechTags.FindTrailing(b.Text) }.Where(t => t is not null).Select(t => t!);
                foreach (var tag in tags)
                    if (tag.Kind is SubjectKind.Name or SubjectKind.Descriptor)
                        _registry.AddTagSubject(tag.Subject, tag.Kind == SubjectKind.Descriptor);
            }
        }
        _registry.Consolidate();
        progress?.Report(0.2);

        // Pass 2: pronoun statistics for gender.
        foreach (var ch in chapters)
            foreach (var b in ch.Blocks.Where(b => b.Kind == BlockKind.Paragraph))
                foreach (var span in QuoteSplitter.Split(b.Text, _style).Spans.Where(s => !s.IsQuote))
                    _registry.CountPronouns(span.Text);
        _registry.ResolveGenders();
        progress?.Report(0.35);

        // Pass 3: attribution.
        var result = new AnalysisResult();
        int runSeed = 0;
        for (int i = 0; i < chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            result.Chapters.Add(AnalyzeChapter(chapters[i], ref runSeed));
            progress?.Report(0.35 + 0.6 * (i + 1) / chapters.Count);
        }

        BuildCast(result, narratorVoice);
        progress?.Report(1);
        return result;
    }

    // ---------- per chapter ----------

    private sealed class Para
    {
        public Block Block = null!;
        public SplitParagraph Split = null!;
        public List<bool> Speech = new(); // per span: quote that is actual speech
        public List<(int SpanIndex, TagHit Tag, string? SpeakerId, double Conf)> Tags = new();
        public string? Speaker;
        public double Conf;
        public AttributionSource Source = AttributionSource.Fallback;
        public string? Cue;
        public HashSet<string> Addressees = new();
        public int RunId = -1, RunPos = -1;
        public List<SpeakerCandidate> Candidates = new();
        public bool IsDialogue => Speech.Any(s => s);
    }

    private ChapterAnalysis AnalyzeChapter(ChapterText chapter, ref int runSeed)
    {
        var recent = new List<string>(); // character ids, most recent first
        var paras = new List<Para?>(); // null = non-paragraph block (breaks runs)
        Para? prev = null;
        var run = new List<Para>();
        _run = run;
        var runs = new List<List<Para>>();

        void EndRun()
        {
            if (run.Count > 0) runs.Add(run);
            run = new List<Para>();
            _run = run;
        }

        TagHit? leadIn = null;
        Para? lastDialogue = null;
        foreach (var block in chapter.Blocks)
        {
            if (block.Kind != BlockKind.Paragraph)
            {
                paras.Add(null);
                EndRun();
                prev = null;
                leadIn = null;
                continue;
            }

            var p = new Para { Block = block, Split = QuoteSplitter.Split(block.Text, _style) };
            ClassifyQuotes(p);
            paras.Add(p);

            if (!p.IsDialogue)
            {
                Touch(recent, p);
                leadIn = null;
                // "Then he says:" on its own line introduces the next paragraph's speech.
                if (SpeechTags.FindLeadIn(block.Text) is { } li) { leadIn = li; prev = p; continue; }
                // "I says." on its own line attributes the speech just before it.
                if (prev == lastDialogue && lastDialogue is not null && SpeechTags.FindTrailing(block.Text) is { } tr)
                {
                    var (tid, tconf, tcands) = ResolveTag(tr, recent, lastDialogue, null);
                    if (tid is not null && tconf > lastDialogue.Conf)
                    {
                        lastDialogue.Speaker = tid; lastDialogue.Conf = tconf;
                        lastDialogue.Source = tr.Kind == SubjectKind.Pronoun ? AttributionSource.Pronoun : AttributionSource.Tag;
                        lastDialogue.Cue ??= SpeechTags.CueFor(tr.Verb, tr.Adverb);
                        if (tcands.Count > 0) lastDialogue.Candidates = tcands;
                        Bump(recent, tid);
                    }
                    prev = p;
                    continue;
                }
                EndRun();
                prev = p;
                continue;
            }

            CollectAddressees(p);
            ResolveTags(p, recent, prev);
            if (p.Speaker is null && leadIn is not null)
            {
                var (lid, lconf, lcands) = ResolveTag(leadIn, recent, p, lastDialogue);
                if (lid is not null)
                {
                    p.Speaker = lid; p.Conf = lconf * 0.97;
                    p.Source = leadIn.Kind == SubjectKind.Pronoun ? AttributionSource.Pronoun : AttributionSource.Tag;
                    p.Cue ??= SpeechTags.CueFor(leadIn.Verb, leadIn.Adverb);
                }
                if (lcands.Count > 0 && p.Candidates.Count == 0) p.Candidates = lcands;
            }
            leadIn = null;
            lastDialogue = p;
            if (p.Speaker is null && prev is not null && prev.IsDialogue && prev.Split.EndsOpen && p.Split.StartsWithQuote && prev.Speaker is not null)
            {
                p.Speaker = prev.Speaker; p.Conf = Math.Max(0.6, prev.Conf * 0.95); p.Source = AttributionSource.Continuation;
            }
            if (p.Speaker is null) ResolveActionBeat(p, recent, prev);

            p.RunId = runSeed + runs.Count;
            p.RunPos = run.Count;
            run.Add(p);
            Touch(recent, p);
            if (p.Speaker is not null && p.Conf >= ConfidentThreshold) Bump(recent, p.Speaker);
            prev = p;
        }
        EndRun();

        foreach (var r in runs) FillByAlternation(r, recent);
        runSeed += runs.Count + 1;

        // Emit lines.
        var analysis = new ChapterAnalysis { ChapterIndex = chapter.Index };
        for (int bi = 0; bi < chapter.Blocks.Count; bi++)
        {
            var block = chapter.Blocks[bi];
            var p = paras[bi];
            if (p is null)
            {
                if (block.Kind == BlockKind.SceneBreak) continue;
                analysis.Lines.Add(new Line { Id = block.Id + "l0", BlockId = block.Id, Text = block.Text, Source = AttributionSource.Narration });
                continue;
            }
            EmitLines(p, analysis.Lines);
        }
        return analysis;
    }

    /// <summary>Marks which quoted spans are speech; scare quotes and titles become narration.</summary>
    private static void ClassifyQuotes(Para p)
    {
        var spans = p.Split.Spans;
        for (int i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (!s.IsQuote) { p.Speech.Add(false); continue; }
            var before = i > 0 && !spans[i - 1].IsQuote ? spans[i - 1].Text : null;
            var after = i + 1 < spans.Count && !spans[i + 1].IsQuote ? spans[i + 1].Text : null;
            bool tagged = (after is not null && SpeechTags.FindAfter(after) is not null) || (before is not null && SpeechTags.FindBefore(before) is not null);
            int words = TextUtil.CountWords(s.Text);
            char last = s.Text[^1];
            bool endsLikeSpeech = last is '.' or ',' or '!' or '?' or '—' or '–' or '…' or '-' or ';';
            // "a deep sound of “boom!” away up the river": a quotation inside a running sentence.
            bool midSentence = before is not null && after is not null && before.Length > 0 && char.IsLetter(before[^1]) && after.Length > 0 && char.IsLower(after[0]);
            bool speech = tagged || (!midSentence && (i == 0 || words >= 4 || endsLikeSpeech));
            if (!tagged && words <= 3 && !endsLikeSpeech) speech = false;
            p.Speech.Add(speech);
        }
    }

    private static IEnumerable<(int SpanIndex, TagHit Tag)> TagsOf(SplitParagraph split)
    {
        var spans = split.Spans;
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].IsQuote) continue;
            if (i > 0 && spans[i - 1].IsQuote && SpeechTags.FindAfter(spans[i].Text) is { } a) yield return (i, a);
            else if (i + 1 < spans.Count && spans[i + 1].IsQuote && SpeechTags.FindBefore(spans[i].Text) is { } b) yield return (i, b);
        }
    }

    private void CollectAddressees(Para p)
    {
        for (int i = 0; i < p.Split.Spans.Count; i++)
        {
            if (!p.Speech[i]) continue;
            var text = p.Split.Spans[i].Text;
            foreach (var (c, idx, len) in _registry.Mentions(text))
            {
                // Vocative: the name is set off by a comma or ends the sentence ("Lizzy, …", "…, Mr. Bennet?")
                int end = idx + len;
                bool commaBefore = idx >= 2 && text[idx - 2] == ',';
                bool punctAfter = end < text.Length && text[Math.Min(text.Length - 1, end)] is ',' or '!' or '?' or '.';
                bool startsQuote = idx == 0;
                if (commaBefore || startsQuote || punctAfter) p.Addressees.Add(c.Id);
            }
        }
    }

    private void ResolveTags(Para p, List<string> recent, Para? prev)
    {
        foreach (var (si, tag) in TagsOf(p.Split))
        {
            var (id, conf, cands) = ResolveTag(tag, recent, p, prev);
            if (cands.Count > 0 && p.Candidates.Count == 0) p.Candidates = cands;
            p.Tags.Add((si, tag, id, conf));
        }

        // The strongest tag speaks for the paragraph; quotes next to other tags are handled per span.
        var best = p.Tags.Where(t => t.SpeakerId is not null).OrderByDescending(t => t.Conf).FirstOrDefault();
        if (best.SpeakerId is not null)
        {
            p.Speaker = best.SpeakerId;
            p.Conf = best.Conf;
            p.Source = best.Tag.Kind == SubjectKind.Pronoun ? AttributionSource.Pronoun : AttributionSource.Tag;
            p.Cue = SpeechTags.CueFor(best.Tag.Verb, best.Tag.Adverb);
        }
        else if (p.Tags.Count > 0)
        {
            p.Cue = SpeechTags.CueFor(p.Tags[0].Tag.Verb, p.Tags[0].Tag.Adverb);
        }
    }

    private (string? Id, double Conf, List<SpeakerCandidate> Candidates) ResolveTag(TagHit tag, List<string> recent, Para p, Para? prev)
    {
        switch (tag.Kind)
        {
            case SubjectKind.FirstPerson:
                return (CastMember.NarratorId, tag.Subject.Equals("we", StringComparison.OrdinalIgnoreCase) ? 0.6 : 0.95, new());
            case SubjectKind.Name:
            case SubjectKind.Descriptor:
                var c = _registry.Lookup(tag.Subject);
                return c is null ? (null, 0, new()) : (c.Id, tag.Kind == SubjectKind.Name ? 0.95 : 0.88, new());
            case SubjectKind.Pronoun:
                return ResolvePronoun(tag.Subject, recent, p, prev);
            default:
                // "a voice said", "they said": an unnamed speaker. The narrator reads it; nothing to review.
                return (CastMember.NarratorId, 0.85, new());
        }
    }

    private (string? Id, double Conf, List<SpeakerCandidate> Candidates) ResolvePronoun(string pronoun, List<string> recent, Para p, Para? prev)
    {
        var want = pronoun.Equals("she", StringComparison.OrdinalIgnoreCase) ? Gender.Female : Gender.Male;
        var scores = new Dictionary<string, double>();
        bool Fits(CharacterRegistry.Character c) => c.Gender == Gender.Unknown || c.Gender == want;

        double weight = 1.0;
        foreach (var id in recent.Take(8))
        {
            var c = _registry.Characters.FirstOrDefault(x => x.Id == id);
            if (c is null || !Fits(c)) continue;
            scores[id] = scores.GetValueOrDefault(id) + weight * (c.Gender == want ? 1.0 : 0.45);
            weight *= 0.6;
        }
        // Someone already talking in this exchange is a far likelier referent than a name dropped in narration.
        weight = 1.2;
        for (int i = _run.Count - 1; i >= 0 && i >= _run.Count - 6; i--)
        {
            var sp = _run[i].Speaker;
            if (sp is null || _run[i].Conf < ConfidentThreshold) continue;
            var c = _registry.Characters.FirstOrDefault(x => x.Id == sp);
            if (c is null || !Fits(c)) continue;
            scores[sp] = scores.GetValueOrDefault(sp) + weight;
            weight *= 0.7;
        }
        foreach (var id in scores.Keys.ToList())
        {
            if (p.Addressees.Contains(id)) scores[id] *= 0.15;
            if (prev?.IsDialogue == true && prev.Speaker == id) scores[id] *= 0.35; // people rarely answer themselves
            if (prev?.IsDialogue == true && prev.Addressees.Contains(id)) scores[id] += 0.8; // whoever was just addressed tends to reply
        }
        var cands = Normalize(scores);
        if (cands.Count == 0) return (null, 0, cands);
        var top = cands[0];
        // A pronoun is never as certain as a name: the referent may be someone not yet seen.
        double conf = Math.Min(0.85, 0.3 + 0.6 * top.Score);
        return (top.SpeakerId, conf, cands);
    }

    private void ResolveActionBeat(Para p, List<string> recent, Para? prev)
    {
        // "The captain spread his hand open." next to the quote: the subject of the narration speaks.
        for (int i = 0; i < p.Split.Spans.Count; i++)
        {
            var s = p.Split.Spans[i];
            if (s.IsQuote) continue;
            var mention = _registry.Mentions(s.Text).FirstOrDefault();
            if (mention.Character is not null && mention.Index <= 4 && !p.Addressees.Contains(mention.Character.Id))
            {
                p.Speaker = mention.Character.Id; p.Conf = 0.7; p.Source = AttributionSource.ActionBeat;
                return;
            }
            if (s.Text.StartsWith("He ", StringComparison.Ordinal) || s.Text.StartsWith("She ", StringComparison.Ordinal))
            {
                var (id, conf, cands) = ResolvePronoun(s.Text[..s.Text.IndexOf(' ')], recent, p, prev);
                if (id is not null)
                {
                    p.Speaker = id; p.Conf = Math.Min(conf, 0.62) * 0.95; p.Source = AttributionSource.ActionBeat; p.Candidates = cands;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Untagged lines in a back-and-forth exchange alternate between its two speakers:
    /// A, B, ?, B → the gap is A. Confidence decays with each inferred step.
    /// </summary>
    private void FillByAlternation(List<Para> run, List<string> recent)
    {
        bool Known(int i) => i >= 0 && i < run.Count && run[i].Speaker is not null && run[i].Conf >= ConfidentThreshold;

        if (TwoParty(run, out var pa, out var pb)) FillTwoParty(run, pa, pb);

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < run.Count; i++)
            {
                var p = run[i];
                if (p.Speaker is not null && p.Conf >= ConfidentThreshold) continue;
                string? guess = null; double conf = 0;
                if (Known(i - 2) && Known(i - 1) && run[i - 2].Speaker != run[i - 1].Speaker)
                {
                    guess = run[i - 2].Speaker; conf = Math.Min(run[i - 2].Conf, run[i - 1].Conf) * 0.93;
                }
                else if (Known(i + 2) && Known(i + 1) && run[i + 2].Speaker != run[i + 1].Speaker)
                {
                    guess = run[i + 2].Speaker; conf = Math.Min(run[i + 2].Conf, run[i + 1].Conf) * 0.9;
                }
                else if (TwoParty(run, out var a, out var b) && (Known(i - 1) || Known(i + 1)))
                {
                    // A two-person exchange: whoever didn't just speak (or isn't about to) says this.
                    var neighbour = Known(i - 1) ? run[i - 1] : run[i + 1];
                    if (Known(i - 1) && Known(i + 1) && run[i - 1].Speaker != run[i + 1].Speaker) { /* contradictory; leave for review */ }
                    else
                    {
                        guess = neighbour.Speaker == a ? b : a;
                        conf = neighbour.Conf * 0.9;
                    }
                }
                if (guess is not null && conf > p.Conf && !p.Addressees.Contains(guess))
                {
                    p.Speaker = guess; p.Conf = conf; p.Source = AttributionSource.Alternation;
                    changed = changed || conf >= ConfidentThreshold;
                }
            }
        }

        // Whatever is still uncertain gets ranked candidates for the Review screen.
        var participants = run.Where(x => x.Speaker is not null && x.Conf >= ConfidentThreshold).GroupBy(x => x.Speaker!)
            .ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < run.Count; i++)
        {
            var p = run[i];
            if (p.Speaker is not null && p.Conf >= ConfidentThreshold) continue;
            var scores = new Dictionary<string, double>();
            foreach (var c in p.Candidates) scores[c.SpeakerId] = c.Score;
            if (p.Speaker is not null) scores[p.Speaker] = scores.GetValueOrDefault(p.Speaker) + p.Conf;
            foreach (var (id, n) in participants) scores[id] = scores.GetValueOrDefault(id) + 0.3 * n / run.Count + 0.1;
            // The neighbours just spoke; the other participant is more likely.
            if (Known(i - 1)) scores[run[i - 1].Speaker!] = scores.GetValueOrDefault(run[i - 1].Speaker!) * 0.4;
            if (Known(i + 1)) scores[run[i + 1].Speaker!] = scores.GetValueOrDefault(run[i + 1].Speaker!) * 0.4;
            int r = 0;
            foreach (var id in recent.Take(4)) scores[id] = scores.GetValueOrDefault(id) + 0.12 / ++r;
            foreach (var a in p.Addressees) if (scores.ContainsKey(a)) scores[a] *= 0.15;
            scores.Remove(CastMember.NarratorId);
            p.Candidates = Normalize(scores).Take(4).ToList();
            if (p.Speaker is null && p.Candidates.Count > 0) { p.Speaker = p.Candidates[0].SpeakerId; p.Conf = 0.3 + 0.3 * p.Candidates[0].Score; }
        }
    }

    /// <summary>
    /// In a two-person exchange, speakers alternate by paragraph. Each untagged paragraph takes its
    /// speaker from the parity of the nearest strong anchors (explicit tags, or a vocative that rules
    /// one person out). Anchors on both sides that disagree mean the pattern broke somewhere between
    /// them, so those paragraphs are left for review.
    /// </summary>
    private static void FillTwoParty(List<Para> run, string a, string b)
    {
        string Other(string s) => s == a ? b : a;
        var anchors = new (string Speaker, double Conf)?[run.Count];
        for (int i = 0; i < run.Count; i++)
        {
            var p = run[i];
            if (p.Speaker is not null && (p.Speaker == a || p.Speaker == b) && ((p.Conf >= 0.85 && p.Source is AttributionSource.Tag or AttributionSource.Continuation) || (p.Conf >= ConfidentThreshold && p.Source == AttributionSource.Pronoun)))
                anchors[i] = (p.Speaker, p.Conf);
            else if (p.Addressees.Contains(a) ^ p.Addressees.Contains(b))
                anchors[i] = (p.Addressees.Contains(a) ? b : a, 0.8); // "Why, Jim?" isn't said by Jim
        }

        for (int i = 0; i < run.Count; i++)
        {
            var p = run[i];
            if (p.Speaker is not null && p.Conf >= ConfidentThreshold && p.Source != AttributionSource.Alternation) continue;
            if (anchors[i] is { } self) { p.Speaker = self.Speaker; p.Conf = self.Conf; p.Source = AttributionSource.Alternation; continue; }

            int l = i - 1; while (l >= 0 && anchors[l] is null) l--;
            int r = i + 1; while (r < run.Count && anchors[r] is null) r++;
            string? fromLeft = l >= 0 ? ((i - l) % 2 == 0 ? anchors[l]!.Value.Speaker : Other(anchors[l]!.Value.Speaker)) : null;
            string? fromRight = r < run.Count ? ((r - i) % 2 == 0 ? anchors[r]!.Value.Speaker : Other(anchors[r]!.Value.Speaker)) : null;

            string? guess; double conf;
            if (fromLeft is not null && fromRight is not null)
            {
                if (fromLeft != fromRight) continue;
                guess = fromLeft; conf = 0.88 - 0.015 * Math.Min(i - l, r - i);
            }
            else if (fromLeft is not null) { guess = fromLeft; conf = 0.8 - 0.03 * (i - l); }
            else if (fromRight is not null) { guess = fromRight; conf = 0.78 - 0.03 * (r - i); }
            else continue;

            if (p.Addressees.Contains(guess)) continue;
            // Explicit evidence (tags, pronouns, beats) was kept above; parity replaces only weaker guesses.
            p.Speaker = guess; p.Conf = conf; p.Source = AttributionSource.Alternation;
        }
    }

    /// <summary>True when exactly two speakers are confidently known in this run (the narrator counts as "I").</summary>
    private static bool TwoParty(List<Para> run, out string a, out string b)
    {
        // Explicitly tagged speakers only; a stray pronoun guess shouldn't break the pattern. Two speakers
        // that account for nearly all tagged lines make it a two-person exchange.
        var counts = run.Where(x => x.Speaker is not null && x.Conf >= ConfidentThreshold && x.Source is AttributionSource.Tag or AttributionSource.ActionBeat or AttributionSource.Continuation or AttributionSource.Pronoun)
            .GroupBy(x => x.Speaker!).Select(g => (Id: g.Key, N: g.Count())).OrderByDescending(g => g.N).ToList();
        a = counts.Count > 0 ? counts[0].Id : ""; b = counts.Count > 1 ? counts[1].Id : "";
        if (counts.Count < 2) return false;
        int total = counts.Sum(c => c.N);
        return counts.Count == 2 || counts[0].N + counts[1].N >= 0.85 * total && counts[2].N == 1;
    }

    private static List<SpeakerCandidate> Normalize(Dictionary<string, double> scores)
    {
        double total = scores.Values.Where(v => v > 0).Sum();
        if (total <= 0) return new();
        return scores.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
            .Select(kv => new SpeakerCandidate { SpeakerId = kv.Key, Score = Math.Round(kv.Value / total, 3) }).ToList();
    }

    private void Touch(List<string> recent, Para p)
    {
        // Characters named in narration become likely referents for "he"/"she".
        for (int i = 0; i < p.Split.Spans.Count; i++)
        {
            if (p.Split.Spans[i].IsQuote && p.Speech[i]) continue;
            foreach (var (c, _, _) in _registry.Mentions(p.Split.Spans[i].Text)) Bump(recent, c.Id);
        }
    }

    private static void Bump(List<string> recent, string id)
    {
        if (id == CastMember.NarratorId) return;
        recent.Remove(id);
        recent.Insert(0, id);
        if (recent.Count > 12) recent.RemoveAt(recent.Count - 1);
    }


    /// <summary>
    /// The narration that colours quote <paramref name="i"/>: the narration just before it ("She
    /// lowered her voice. “…”"), plus the narration after it when no other quote follows ("“…,” she
    /// said, smiling."). Narration between two quotes belongs to the second.
    /// </summary>
    private static string BeatsFor(IReadOnlyList<Span> spans, List<bool> speech, int i)
    {
        bool IsSpeech(int k) => spans[k].IsQuote && speech[k];
        var parts = new List<string>();
        if (i > 0 && !IsSpeech(i - 1)) parts.Add(spans[i - 1].Text);
        bool laterSpeech = false;
        for (int k = i + 1; k < spans.Count; k++) if (IsSpeech(k)) { laterSpeech = true; break; }
        if (!laterSpeech && i + 1 < spans.Count && !IsSpeech(i + 1)) parts.Add(spans[i + 1].Text);
        return string.Join(" ", parts);
    }
    private static void EmitLines(Para p, List<Line> lines)
    {
        var spans = p.Split.Spans;
        int n = 0;
        var narration = new System.Text.StringBuilder();

        void FlushNarration()
        {
            var t = narration.ToString().Trim().TrimStart(',', ';', ':').Trim();
            narration.Clear();
            if (t.Length == 0 || t.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c))) return;
            lines.Add(new Line { Id = $"{p.Block.Id}l{n++}", BlockId = p.Block.Id, Text = t, Source = AttributionSource.Narration });
        }

        for (int i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (!s.IsQuote || !p.Speech[i])
            {
                if (narration.Length > 0) narration.Append(' ');
                narration.Append(s.IsQuote ? $"“{s.Text}”" : s.Text);
                continue;
            }
            FlushNarration();

            // A quote right next to its own tag belongs to that tag's speaker.
            string? speaker = p.Speaker;
            double conf = p.Conf;
            var src = p.Source;
            var cue = p.Cue;
            foreach (var t in p.Tags)
            {
                bool adjacent = (t.SpanIndex == i + 1 && !t.Tag.BeforeQuote) || (t.SpanIndex == i - 1 && t.Tag.BeforeQuote);
                if (adjacent && t.SpeakerId is not null && t.Conf >= conf - 0.05)
                {
                    speaker = t.SpeakerId; conf = t.Conf;
                    src = t.Tag.Kind == SubjectKind.Pronoun ? AttributionSource.Pronoun : AttributionSource.Tag;
                    cue = SpeechTags.CueFor(t.Tag.Verb, t.Tag.Adverb) ?? cue;
                }
            }

            var cueFrom = cue is not null ? CueSource.Tag : CueSource.None;
            if (cue is null && Tones.FromContext(s.Text, BeatsFor(spans, p.Speech, i)) is { } guessed) { cue = guessed; cueFrom = CueSource.Context; }

            bool unsure = speaker is null || conf < ConfidentThreshold;
            lines.Add(new Line
            {
                Id = $"{p.Block.Id}l{n++}",
                BlockId = p.Block.Id,
                Text = $"“{s.Text}”",
                IsDialogue = true,
                SpeakerId = unsure ? CastMember.NarratorId : speaker!,
                Confidence = Math.Round(conf, 3),
                Source = unsure ? AttributionSource.Fallback : src,
                Candidates = unsure ? p.Candidates.Select(c => new SpeakerCandidate { SpeakerId = c.SpeakerId, Score = c.Score }).ToList() : new(),
                Cue = cue,
                CueFrom = cueFrom,
                RunId = p.RunId,
                RunPosition = p.RunPos,
                Unsure = unsure,
            });
        }
        FlushNarration();
    }

    // ---------- cast ----------

    private void BuildCast(AnalysisResult result, string narratorVoice)
    {
        var lines = result.Chapters.SelectMany(c => c.Lines).Where(l => l.IsDialogue).ToList();
        var counts = lines.Where(l => !l.Unsure).GroupBy(l => l.SpeakerId).ToDictionary(g => g.Key, g => g.ToList());
        var referenced = lines.Where(l => l.Unsure).SelectMany(l => l.Candidates.Select(c => c.SpeakerId)).ToHashSet();

        var cast = result.Cast;
        cast.Members.Add(new CastMember
        {
            Id = CastMember.NarratorId,
            Name = "Narrator",
            VoiceId = narratorVoice,
            Color = VoiceCatalog.NarratorColor,
            LineCount = counts.GetValueOrDefault(CastMember.NarratorId)?.Count ?? 0,
        });

        int colorIndex = 0;
        foreach (var c in _registry.Characters
                     .Where(c => counts.ContainsKey(c.Id) || referenced.Contains(c.Id))
                     .OrderByDescending(c => counts.GetValueOrDefault(c.Id)?.Count ?? 0))
        {
            var mine = counts.GetValueOrDefault(c.Id) ?? new List<Line>();
            cast.Members.Add(new CastMember
            {
                Id = c.Id,
                Name = c.Name,
                Aliases = c.Forms.Keys.Where(f => f != c.Name).ToList(),
                Gender = c.Gender,
                Color = VoiceCatalog.ColorFor(colorIndex++),
                LineCount = mine.Count,
                ConfidentCount = mine.Count(l => l.Confidence >= 0.85),
            });
        }
        VoiceCatalog.AssignVoices(cast.Members, narratorVoice);
        cast.Suggestions.AddRange(SuggestMerges(cast.Members));
    }

    internal static IEnumerable<MergeSuggestion> SuggestMerges(IEnumerable<CastMember> members)
    {
        var list = members.Where(m => !m.IsNarrator).ToList();
        foreach (var a in list)
        {
            foreach (var b in list)
            {
                if (a == b || a.LineCount > b.LineCount) continue;
                if (a.Gender != Gender.Unknown && b.Gender != Gender.Unknown && a.Gender != b.Gender) continue;
                if (NameRules.IsNicknameOf(a.Name, b.Name) || a.Aliases.Any(al => NameRules.IsNicknameOf(al, b.Name)))
                {
                    yield return new MergeSuggestion { FromId = a.Id, IntoId = b.Id, Reason = $"“{a.Name}” looks like {b.Name}." };
                    break;
                }
            }
        }
    }
}
