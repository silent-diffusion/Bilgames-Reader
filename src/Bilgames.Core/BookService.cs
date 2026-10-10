using Bilgames.Core.Analysis;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;
using Bilgames.Core.Tts;

namespace Bilgames.Core;

public sealed class ReviewItem
{
    public int ChapterIndex { get; init; }
    public string ChapterTitle { get; init; } = "";
    public Line Line { get; init; } = null!;
    public List<Line> Context { get; init; } = new();
    public int Position { get; init; }
    public int Total { get; init; }
}

/// <summary>One of a character's lines, for the per-speaker line list.</summary>
public sealed class SpeakerLine
{
    public int ChapterIndex { get; init; }
    public string ChapterTitle { get; init; } = "";
    public Line Line { get; init; } = null!;
}

/// <summary>Speaker analysis and every user correction to it. All edits persist immediately.</summary>
public sealed class BookService
{
    private readonly LibraryStore _store;

    public BookService(LibraryStore store) => _store = store;

    // Every edit to a book's analysis or cast is a read-modify-save of several files. They run on
    // background threads (a review confirm, a voice change, a merge…), so each one holds the book's
    // edit lock: an edit can never save over another with a copy it loaded before that one finished.

    /// <summary>Finds speakers across the book. Earlier corrections and voice choices are kept.</summary>
    /// <param name="firstChapterOnly">
    /// Analyse just the first chapter, so listening can start at once; the book is marked
    /// <see cref="BookRecord.AnalyzingRest"/> and a later full call finishes it.
    /// </param>
    public BookRecord Analyze(string bookId, IProgress<double>? progress = null, CancellationToken ct = default, bool firstChapterOnly = false)
    {
        lock (_store.EditLock(bookId)) return AnalyzeUnlocked(bookId, progress, ct, firstChapterOnly);
    }

    public void SetVoice(string bookId, string memberId, string voiceId)
    {
        lock (_store.EditLock(bookId)) SetVoiceUnlocked(bookId, memberId, voiceId);
    }

    /// <summary>Folds one speaker into another (lines, candidates, name as an alias).</summary>
    public void Merge(string bookId, string fromId, string intoId)
    {
        lock (_store.EditLock(bookId)) MergeUnlocked(bookId, fromId, intoId);
    }

    /// <summary>Merges several speakers into one in a single pass.</summary>
    public void MergeMany(string bookId, IReadOnlyCollection<string> fromIds, string intoId)
    {
        lock (_store.EditLock(bookId)) MergeManyUnlocked(bookId, fromIds, intoId);
    }

    /// <summary>Adds a character the analysis missed (or returns the existing one with that name).</summary>
    public CastMember AddSpeaker(string bookId, string name, Gender gender = Gender.Unknown)
    {
        lock (_store.EditLock(bookId)) return AddSpeakerUnlocked(bookId, name, gender);
    }

    public void KeepApart(string bookId, MergeSuggestion s)
    {
        lock (_store.EditLock(bookId)) KeepApartUnlocked(bookId, s);
    }

    /// <summary>Sets who speaks a line (optionally the rest of that exchange by alternation).</summary>
    public int AssignSpeaker(string bookId, int chapterIndex, string lineId, string speakerId, bool applyToSimilar)
    {
        lock (_store.EditLock(bookId)) return AssignSpeakerUnlocked(bookId, chapterIndex, lineId, speakerId, applyToSimilar);
    }

    /// <summary>Sets a line's tone; kept through re-analysis.</summary>
    public void SetCue(string bookId, int chapterIndex, string lineId, string? cue)
    {
        lock (_store.EditLock(bookId)) SetCueUnlocked(bookId, chapterIndex, lineId, cue);
    }

    /// <summary>Leaves the line with the narrator and removes it from the review queue.</summary>
    public void Skip(string bookId, int chapterIndex, string lineId)
    {
        lock (_store.EditLock(bookId)) SkipUnlocked(bookId, chapterIndex, lineId);
    }

    public LibraryStore Store => _store;

    /// <summary>Whether a chapter gets speaker analysis (the body, plus any other parts chosen to be read).</summary>
    public static bool Analyzable(BookRecord b, ChapterInfo c) => c.Kind == SectionKind.Body || b.Options.Includes(c.Kind);

    /// <summary>Finds speakers across the book. Earlier corrections and voice choices are kept.</summary>
    private BookRecord AnalyzeUnlocked(string bookId, IProgress<double>? progress, CancellationToken ct, bool firstChapterOnly = false)
    {
        var book = _store.LoadBook(bookId) ?? throw new InvalidOperationException("Book not found.");
        // Finishing the rest after the first chapter: the book stays usable (analysed) meanwhile.
        bool finishing = book.AnalyzingRest && !firstChapterOnly;
        if (!finishing) book.Analysis = AnalysisState.Running;
        _store.SaveBook(book);
        try
        {
            var analyzable = book.Chapters.Where(c => Analyzable(book, c)).ToList();
            bool partial = firstChapterOnly && analyzable.Count > 1;
            var chapters = (partial ? analyzable.Take(1) : analyzable).Select(c => _store.LoadChapterText(bookId, c.Index)).ToList();
            var oldCast = _store.LoadCast(bookId);
            var narratorVoice = oldCast.Members.FirstOrDefault(m => m.IsNarrator)?.VoiceId ?? VoiceCatalog.DefaultNarrator;

            var result = DialogueAnalyzer.Analyze(chapters, narratorVoice, progress, ct);
            var idMap = ReapplyCastEdits(oldCast, result);

            // Carry over voice choices (by character name) and reviewed lines (by line id).
            foreach (var m in result.Cast.Members)
            {
                var old = oldCast.Members.FirstOrDefault(o => o.Id == m.Id && o.Name == m.Name) ?? oldCast.Members.FirstOrDefault(o => o.Name == m.Name);
                if (old is not null && !string.IsNullOrEmpty(old.VoiceId)) m.VoiceId = old.VoiceId;
            }
            result.Cast.DismissedSuggestions.AddRange(oldCast.DismissedSuggestions);
            result.Cast.Suggestions.RemoveAll(s => result.Cast.DismissedSuggestions.Contains(SuggestionKey(s)));

            foreach (var a in result.Chapters)
            {
                var previous = _store.LoadAnalysis(bookId, a.ChapterIndex);
                if (previous is not null)
                {
                    var reviewed = previous.Lines.Where(l => l.Reviewed).ToDictionary(l => l.Id);
                    var tones = previous.Lines.Where(l => l.CueFrom == CueSource.User).ToDictionary(l => l.Id);
                    foreach (var l in a.Lines)
                    {
                        if (tones.TryGetValue(l.Id, out var t) && t.Text == l.Text) { l.Cue = t.Cue; l.CueFrom = CueSource.User; }
                        if (!reviewed.TryGetValue(l.Id, out var r) || r.Text != l.Text) continue;
                        var speaker = idMap.GetValueOrDefault(r.SpeakerId, r.SpeakerId);
                        if (result.Cast.Find(speaker) is null)
                        {
                            // Someone you gave this line to whom the fresh analysis didn't find: keep them rather than lose the correction.
                            if (oldCast.Find(r.SpeakerId) is not { IsNarrator: false } old) { if (r.Source == AttributionSource.User) continue; }
                            else speaker = idMap[r.SpeakerId] = KeepMember(result.Cast, old);
                        }
                        l.SpeakerId = speaker; l.Unsure = r.Unsure; l.Reviewed = true; l.Source = r.Source; l.Confidence = r.Confidence;
                    }
                }
                _store.SaveAnalysis(bookId, a);
            }

            _store.SaveCast(bookId, result.Cast);
            book = _store.LoadBook(bookId) ?? book;
            book.Analysis = AnalysisState.Done;
            book.AnalyzingRest = partial;
            Recount(book, result.Cast);
            return book;
        }
        catch
        {
            book = _store.LoadBook(bookId) ?? book;
            if (!finishing) book.Analysis = AnalysisState.NotStarted;
            _store.SaveBook(book);
            throw;
        }
    }

    /// <summary>
    /// Keeps the user's cast edits through a re-analysis: speakers they added come back, and names
    /// they merged (stored as aliases) are merged again. Returns old id → new id for added speakers
    /// the analysis has now found by itself.
    /// </summary>
    private static Dictionary<string, string> ReapplyCastEdits(CastSheet oldCast, AnalysisResult result)
    {
        var cast = result.Cast;
        var idMap = new Dictionary<string, string>();
        foreach (var added in oldCast.Members.Where(m => m.UserAdded))
        {
            var found = cast.Members.FirstOrDefault(m => !m.IsNarrator && m.Name.Equals(added.Name, StringComparison.OrdinalIgnoreCase));
            if (found is not null) { idMap[added.Id] = found.Id; continue; }
            if (cast.Find(added.Id) is not null) continue;
            cast.Members.Add(new CastMember
            {
                Id = added.Id, Name = added.Name, Aliases = added.Aliases.ToList(), Gender = added.Gender,
                VoiceId = added.VoiceId, Color = added.Color, UserAdded = true,
            });
        }
        foreach (var old in oldCast.Members.Where(m => !m.IsNarrator && m.Aliases.Count > 0))
        {
            var into = cast.Members.FirstOrDefault(m => !m.IsNarrator && m.Name == old.Name);
            if (into is null)
            {
                // Renamed by the reader ("Tenente" → "Rinaldi"): the analysis found the old name again; restore the new one.
                into = cast.Members.FirstOrDefault(m => !m.IsNarrator && !m.UserAdded && old.Aliases.Contains(m.Name)
                                                        && oldCast.Members.All(o => o.Name != m.Name));
                if (into is null) continue;
                into.Aliases.Add(into.Name);
                into.Name = old.Name;
                into.Aliases = into.Aliases.Distinct().Where(a => a != into.Name).ToList();
            }
            foreach (var from in cast.Members.Where(m => m != into && !m.IsNarrator && old.Aliases.Contains(m.Name)).ToList())
            {
                foreach (var a in result.Chapters) Repoint(a, from.Id, into.Id);
                Absorb(cast, from, into);
            }
        }
        // Speaker ids are only labels: carry corrections by who the speaker *is* (their name), so a
        // fresh analysis that numbers characters differently can't hand a line to someone else.
        foreach (var old in oldCast.Members.Where(m => !m.IsNarrator && !idMap.ContainsKey(m.Id)))
        {
            var now = cast.Members.FirstOrDefault(m => !m.IsNarrator && m.Name == old.Name);
            if (now is not null && now.Id != old.Id) idMap[old.Id] = now.Id;
            else if (now is null && cast.Find(old.Id) is { } clash && clash.Name != old.Name) idMap[old.Id] = "\u0001missing:" + old.Id;
        }
        return idMap;
    }

    /// <summary>Copies a speaker from the previous cast into the new one (with a free id). Returns the id used.</summary>
    private static string KeepMember(CastSheet cast, CastMember old)
    {
        var existing = cast.Members.FirstOrDefault(m => !m.IsNarrator && m.Name == old.Name);
        if (existing is not null) return existing.Id;
        var id = old.Id;
        for (int n = 1; cast.Find(id) is not null; n++) id = $"k{n}";
        cast.Members.Add(new CastMember
        {
            Id = id, Name = old.Name, Aliases = old.Aliases.ToList(), Gender = old.Gender, VoiceId = old.VoiceId,
            Color = old.Color, UserAdded = old.UserAdded,
        });
        return id;
    }

    public enum RenameResult { Renamed, NameTaken, Invalid }

    /// <summary>
    /// Renames a speaker. The old name is kept as an alias, so finding speakers again recognises
    /// them and restores this name. Returns <see cref="RenameResult.NameTaken"/> if someone else
    /// already has that name (merge them instead).
    /// </summary>
    public RenameResult RenameSpeaker(string bookId, string memberId, string newName, out string? takenById)
    {
        takenById = null;
        newName = string.Join(' ', newName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        lock (_store.EditLock(bookId))
        {
            var cast = _store.LoadCast(bookId);
            var member = cast.Find(memberId);
            if (member is null || member.IsNarrator || newName.Length == 0) return RenameResult.Invalid;
            if (member.Name == newName) return RenameResult.Renamed;
            var other = cast.Members.FirstOrDefault(m => m != member && !m.IsNarrator && m.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
            if (other is not null) { takenById = other.Id; return RenameResult.NameTaken; }
            if (!member.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)) member.Aliases.Add(member.Name);
            member.Name = newName;
            member.Aliases = member.Aliases.Distinct().Where(a => !a.Equals(newName, StringComparison.OrdinalIgnoreCase)).ToList();
            _store.SaveCast(bookId, cast);
            return RenameResult.Renamed;
        }
    }

    public static string SuggestionKey(MergeSuggestion s) => s.FromId + ">" + s.IntoId;

    private void SetVoiceUnlocked(string bookId, string memberId, string voiceId)
    {
        var cast = _store.LoadCast(bookId);
        var m = cast.Find(memberId);
        if (m is null) return;
        m.VoiceId = voiceId;
        _store.SaveCast(bookId, cast);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, cast);
    }

    /// <summary>
    /// Folds one speaker into another: every line (and review candidate) moves over, the name becomes
    /// an alias (so a later re-analysis merges them again), and the merged-away entry is removed.
    /// </summary>
    private void MergeUnlocked(string bookId, string fromId, string intoId)
    {
        var cast = _store.LoadCast(bookId);
        var from = cast.Find(fromId);
        var into = cast.Find(intoId);
        if (from is null || into is null || from == into || from.IsNarrator) return;

        ForEachAnalysis(bookId, a => Repoint(a, fromId, intoId));
        Absorb(cast, from, into);
        _store.SaveCast(bookId, cast);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, cast);
    }

    /// <summary>Merges several speakers into one in a single pass.</summary>
    private void MergeManyUnlocked(string bookId, IReadOnlyCollection<string> fromIds, string intoId)
    {
        var cast = _store.LoadCast(bookId);
        var into = cast.Find(intoId);
        if (into is null) return;
        var from = fromIds.Select(cast.Find).Where(m => m is not null && m != into && !m.IsNarrator).Select(m => m!).ToList();
        if (from.Count == 0) return;

        ForEachAnalysis(bookId, a =>
        {
            bool changed = false;
            foreach (var f in from) changed |= Repoint(a, f.Id, intoId);
            return changed;
        });
        foreach (var f in from) Absorb(cast, f, into);
        _store.SaveCast(bookId, cast);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, cast);
    }

    /// <summary>Moves a chapter's lines and candidates from one speaker to another. Returns whether anything changed.</summary>
    internal static bool Repoint(ChapterAnalysis a, string fromId, string intoId)
    {
        bool changed = false;
        foreach (var l in a.Lines)
        {
            if (l.SpeakerId == fromId) { l.SpeakerId = intoId; changed = true; }
            foreach (var c in l.Candidates.Where(c => c.SpeakerId == fromId)) { c.SpeakerId = intoId; changed = true; }
            // Collapse duplicate candidates created by the merge.
            if (l.Candidates.GroupBy(c => c.SpeakerId).Any(g => g.Count() > 1))
            {
                l.Candidates = l.Candidates.GroupBy(c => c.SpeakerId)
                    .Select(g => new SpeakerCandidate { SpeakerId = g.Key, Score = g.Sum(x => x.Score) })
                    .OrderByDescending(c => c.Score).ToList();
            }
        }
        return changed;
    }

    private static void Absorb(CastSheet cast, CastMember from, CastMember into)
    {
        if (!into.IsNarrator)
        {
            into.Aliases.Add(from.Name);
            into.Aliases.AddRange(from.Aliases);
            into.Aliases = into.Aliases.Distinct().Where(a => a != into.Name).ToList();
            if (into.Gender == Gender.Unknown) into.Gender = from.Gender;
        }
        cast.Members.Remove(from);
        cast.Suggestions.RemoveAll(s => s.FromId == from.Id || s.IntoId == from.Id);
    }

    /// <summary>
    /// Adds a character the analysis missed. Returns the existing member if the name (or one of its
    /// aliases) is already in the cast. New speakers get their own colour and an unused voice.
    /// </summary>
    private CastMember AddSpeakerUnlocked(string bookId, string name, Gender gender)
    {
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new ArgumentException("A speaker needs a name.", nameof(name));
        var cast = _store.LoadCast(bookId);
        if (cast.Members.Count == 0)
        {
            // Not analyzed yet: start the sheet with the narrator so the new speaker has company.
            cast.Members.Add(new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = VoiceCatalog.DefaultNarrator, Color = VoiceCatalog.NarratorColor });
        }
        var existing = cast.Members.FirstOrDefault(m => !m.IsNarrator &&
            (m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || m.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))));
        if (existing is not null) return existing;

        int n = 1;
        while (cast.Find("u" + n) is not null) n++;
        var member = new CastMember
        {
            Id = "u" + n,
            Name = name,
            Gender = gender,
            UserAdded = true,
            Color = VoiceCatalog.NextColor(cast.Members.Where(m => !m.IsNarrator && (m.LineCount > 0 || m.UserAdded)).Select(m => m.Color)),
            VoiceId = VoiceCatalog.PickFor(gender, cast.Members.Select(m => m.VoiceId)),
        };
        cast.Members.Add(member);
        _store.SaveCast(bookId, cast);
        return member;
    }

    private void KeepApartUnlocked(string bookId, MergeSuggestion s)
    {
        var cast = _store.LoadCast(bookId);
        cast.DismissedSuggestions.Add(SuggestionKey(s));
        cast.Suggestions.RemoveAll(x => SuggestionKey(x) == SuggestionKey(s));
        _store.SaveCast(bookId, cast);
    }

    /// <summary>The paragraphs surrounding a line: <paramref name="before"/> paragraphs before, <paramref name="after"/> after.</summary>
    public static List<List<Line>> ContextBlocks(ChapterAnalysis analysis, string lineId, int before = 3, int after = 2)
    {
        var blocks = new List<List<Line>>();
        foreach (var l in analysis.Lines)
        {
            if (blocks.Count == 0 || blocks[^1][0].BlockId != l.BlockId) blocks.Add(new List<Line>());
            blocks[^1].Add(l);
        }
        int at = blocks.FindIndex(b => b.Any(l => l.Id == lineId));
        if (at < 0) return new();
        int from = Math.Max(0, at - before), to = Math.Min(blocks.Count - 1, at + after);
        return blocks.GetRange(from, to - from + 1);
    }

    public List<List<Line>> ContextFor(string bookId, int chapterIndex, string lineId, int before = 3, int after = 2)
    {
        var a = _store.LoadAnalysis(bookId, chapterIndex);
        return a is null ? new() : ContextBlocks(a, lineId, before, after);
    }

    /// <summary>Every dialogue line currently assigned to a speaker, in reading order.</summary>
    public List<SpeakerLine> LinesBy(string bookId, string speakerId)
    {
        var book = _store.LoadBook(bookId);
        var list = new List<SpeakerLine>();
        if (book is null) return list;
        foreach (var info in book.Chapters)
        {
            var a = _store.LoadAnalysis(bookId, info.Index);
            if (a is null) continue;
            foreach (var l in a.Lines.Where(l => l.IsDialogue && l.SpeakerId == speakerId && !(l.Unsure && !l.Reviewed)))
                list.Add(new SpeakerLine { ChapterIndex = info.Index, ChapterTitle = info.Title, Line = l });
        }
        return list;
    }

    /// <summary>Every unsure, unreviewed dialogue line in reading order, with a little context.</summary>
    public List<ReviewItem> ReviewQueue(string bookId)
    {
        var book = _store.LoadBook(bookId);
        if (book is null) return new();
        var items = new List<(int Ch, string Title, Line Line, List<Line> Ctx)>();
        foreach (var info in book.Chapters)
        {
            var a = _store.LoadAnalysis(bookId, info.Index);
            if (a is null) continue;
            for (int i = 0; i < a.Lines.Count; i++)
            {
                var l = a.Lines[i];
                if (!l.IsDialogue || !l.Unsure || l.Reviewed) continue;
                items.Add((info.Index, info.Title, l, a.Lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(3, i)).ToList()));
            }
        }
        return items.Select((x, i) => new ReviewItem
        {
            ChapterIndex = x.Ch, ChapterTitle = x.Title, Line = x.Line, Context = x.Ctx, Position = i + 1, Total = items.Count,
        }).ToList();
    }

    /// <summary>A specific line (for "Fix" from the player), with every cast member as a choice.</summary>
    public ReviewItem? LineForFix(string bookId, int chapterIndex, string lineId)
    {
        var book = _store.LoadBook(bookId);
        var a = _store.LoadAnalysis(bookId, chapterIndex);
        if (book is null || a is null) return null;
        int i = a.Lines.FindIndex(l => l.Id == lineId);
        if (i < 0) return null;
        return new ReviewItem
        {
            ChapterIndex = chapterIndex,
            ChapterTitle = book.Chapters[chapterIndex].Title,
            Line = a.Lines[i],
            Context = a.Lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(3, i)).ToList(),
            Position = 1,
            Total = 1,
        };
    }

    /// <summary>
    /// Sets who speaks a line. With <paramref name="applyToSimilar"/>, the rest of the same
    /// back-and-forth exchange is re-assigned by alternation: same parity → this speaker, other
    /// parity → the exchange's other main speaker.
    /// </summary>
    private int AssignSpeakerUnlocked(string bookId, int chapterIndex, string lineId, string speakerId, bool applyToSimilar)
    {
        var a = _store.LoadAnalysis(bookId, chapterIndex);
        if (a is null) return 0;
        var line = a.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return 0;

        Set(line, speakerId);
        int changed = 1;

        if (applyToSimilar && line.RunId >= 0)
        {
            var run = a.Lines.Where(l => l.RunId == line.RunId && l.IsDialogue).ToList();
            // The other participant: whoever else confidently speaks in this exchange (may be the first-person narrator).
            var other = run.Where(l => (!l.Unsure || l.Reviewed) && l.SpeakerId != speakerId)
                .GroupBy(l => l.SpeakerId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            foreach (var l in run)
            {
                if (l == line || l.Reviewed || !l.Unsure) continue;
                bool same = (l.RunPosition - line.RunPosition) % 2 == 0;
                if (same) { Set(l, speakerId); changed++; }
                else if (other is not null) { Set(l, other); changed++; }
            }
        }

        _store.SaveAnalysis(bookId, a);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, _store.LoadCast(bookId));
        return changed;
    }

    /// <summary>Sets how a line is delivered (one of <see cref="Tones.All"/>, or null for neutral). Kept through re-analysis.</summary>
    private void SetCueUnlocked(string bookId, int chapterIndex, string lineId, string? cue)
    {
        var a = _store.LoadAnalysis(bookId, chapterIndex);
        var line = a?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (a is null || line is null) return;
        line.Cue = Tones.Normalize(cue);
        line.CueFrom = CueSource.User;
        _store.SaveAnalysis(bookId, a);
    }

    /// <summary>Leaves the line with the narrator and removes it from the queue.</summary>
    private void SkipUnlocked(string bookId, int chapterIndex, string lineId)
    {
        var a = _store.LoadAnalysis(bookId, chapterIndex);
        var line = a?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (a is null || line is null) return;
        line.Reviewed = true;
        _store.SaveAnalysis(bookId, a);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, _store.LoadCast(bookId));
    }

    private static void Set(Line l, string speakerId)
    {
        l.SpeakerId = speakerId;
        l.Unsure = false;
        l.Reviewed = true;
        l.Source = AttributionSource.User;
        l.Confidence = 1;
    }

    /// <summary>Refreshes line counts on the cast sheet and the cached numbers on the book record.</summary>
    public void Recount(BookRecord book, CastSheet cast)
    {
        lock (_store.EditLock(book.Id)) RecountUnlocked(book, cast);
    }

    private void RecountUnlocked(BookRecord book, CastSheet cast)
    {
        var counts = new Dictionary<string, (int Lines, int Confident)>();
        int unsure = 0;
        foreach (var info in book.Chapters)
        {
            var a = _store.LoadAnalysis(book.Id, info.Index);
            if (a is null) continue;
            foreach (var l in a.Lines.Where(l => l.IsDialogue))
            {
                if (l.Unsure && !l.Reviewed) { unsure++; continue; }
                var c = counts.GetValueOrDefault(l.SpeakerId);
                counts[l.SpeakerId] = (c.Lines + 1, c.Confident + (l.Confidence >= 0.85 ? 1 : 0));
            }
        }
        foreach (var m in cast.Members)
        {
            var c = counts.GetValueOrDefault(m.Id);
            m.LineCount = c.Lines;
            m.ConfidentCount = c.Confident;
        }
        _store.SaveCast(book.Id, cast);
        book.UnsureCount = unsure;
        book.VoiceCount = cast.Members.Where(m => m.IsNarrator || m.LineCount > 0).Select(m => m.VoiceId).Distinct().Count();
        _store.SaveBook(book);
    }

    private void ForEachAnalysis(string bookId, Func<ChapterAnalysis, bool> edit)
    {
        var book = _store.LoadBook(bookId);
        if (book is null) return;
        foreach (var info in book.Chapters)
        {
            var a = _store.LoadAnalysis(bookId, info.Index);
            if (a is not null && edit(a)) _store.SaveAnalysis(bookId, a);
        }
    }
}
