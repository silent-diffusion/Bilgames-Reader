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

    public LibraryStore Store => _store;

    private static bool Analyzable(BookRecord b, ChapterInfo c) => c.Kind == SectionKind.Body || b.Options.Includes(c.Kind);

    /// <summary>Finds speakers across the book. Earlier corrections and voice choices are kept.</summary>
    public BookRecord Analyze(string bookId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var book = _store.LoadBook(bookId) ?? throw new InvalidOperationException("Book not found.");
        book.Analysis = AnalysisState.Running;
        _store.SaveBook(book);
        try
        {
            var chapters = book.Chapters.Where(c => Analyzable(book, c)).Select(c => _store.LoadChapterText(bookId, c.Index)).ToList();
            var oldCast = _store.LoadCast(bookId);
            var narratorVoice = oldCast.Members.FirstOrDefault(m => m.IsNarrator)?.VoiceId ?? VoiceCatalog.DefaultNarrator;

            var result = DialogueAnalyzer.Analyze(chapters, narratorVoice, progress, ct);

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
                    foreach (var l in a.Lines)
                    {
                        if (!reviewed.TryGetValue(l.Id, out var r) || r.Text != l.Text) continue;
                        if (r.Source == AttributionSource.User && result.Cast.Find(r.SpeakerId) is null) continue;
                        l.SpeakerId = r.SpeakerId; l.Unsure = r.Unsure; l.Reviewed = true; l.Source = r.Source; l.Confidence = r.Confidence;
                    }
                }
                _store.SaveAnalysis(bookId, a);
            }

            _store.SaveCast(bookId, result.Cast);
            book = _store.LoadBook(bookId) ?? book;
            book.Analysis = AnalysisState.Done;
            Recount(book, result.Cast);
            return book;
        }
        catch
        {
            book = _store.LoadBook(bookId) ?? book;
            book.Analysis = AnalysisState.NotStarted;
            _store.SaveBook(book);
            throw;
        }
    }

    public static string SuggestionKey(MergeSuggestion s) => s.FromId + ">" + s.IntoId;

    public void SetVoice(string bookId, string memberId, string voiceId)
    {
        var cast = _store.LoadCast(bookId);
        var m = cast.Find(memberId);
        if (m is null) return;
        m.VoiceId = voiceId;
        _store.SaveCast(bookId, cast);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, cast);
    }

    public void Merge(string bookId, string fromId, string intoId)
    {
        var cast = _store.LoadCast(bookId);
        var from = cast.Find(fromId);
        var into = cast.Find(intoId);
        if (from is null || into is null || from.IsNarrator) return;

        ForEachAnalysis(bookId, a =>
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
        });

        into.Aliases.Add(from.Name);
        into.Aliases.AddRange(from.Aliases);
        into.Aliases = into.Aliases.Distinct().Where(a => a != into.Name).ToList();
        cast.Members.Remove(from);
        cast.Suggestions.RemoveAll(s => s.FromId == fromId || s.IntoId == fromId);
        _store.SaveCast(bookId, cast);
        var book = _store.LoadBook(bookId);
        if (book is not null) Recount(book, cast);
    }

    public void KeepApart(string bookId, MergeSuggestion s)
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
    public int AssignSpeaker(string bookId, int chapterIndex, string lineId, string speakerId, bool applyToSimilar)
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

    /// <summary>Leaves the line with the narrator and removes it from the queue.</summary>
    public void Skip(string bookId, int chapterIndex, string lineId)
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
