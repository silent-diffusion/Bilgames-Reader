using System.Text.Json.Nodes;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Assist;

public sealed record AssistantProgress(AssistantRunKind Kind, int Done, int Total, int Changed, int Suggested, string Status);

/// <summary>
/// The review assistant: a local language model reading the book to decide who speaks.
/// <list type="bullet">
/// <item><see cref="ReviewLineAsync"/> decides one line, asking for more of the surrounding text up to
/// <see cref="MaxContextRequests"/> times when the passage isn't enough;</item>
/// <item><see cref="RunReviewAsync"/> decides every line waiting in Review: a passage at a time, then
/// line by line, with more context, for the lines it still isn't sure about;</item>
/// <item><see cref="RunWholeBookAsync"/> reads the whole book again passage by passage, with the first
/// analysis shown as suggestions, then suggests a voice for each main character.</item>
/// </list>
/// Changes are applied only at or above the confidence threshold, never over the reader's own
/// decisions, and logged so a run can be undone. Runs save their progress and can be resumed.
/// </summary>
public sealed class ReviewAssistant
{
    public const int MaxContextRequests = 3;
    /// <summary>Paragraphs before/after the line at each context level: the first view, then up to three more.</summary>
    private static readonly (int Before, int After)[] ContextLevels = { (3, 2), (8, 6), (16, 12), (30, 24) };
    /// <summary>Roughly how much text one passage holds (characters), plus the paragraphs shown before it for context.</summary>
    private const int PassageChars = 7000, LeadInBlocks = 2;
    private const int VoiceCharacters = 16;

    private readonly LibraryStore _store;
    private readonly BookService _books;
    private readonly IChatModel _model;
    private readonly Func<double> _threshold;
    private readonly Func<IReadOnlyList<VoiceInfo>> _voices;

    public ReviewAssistant(LibraryStore store, BookService books, IChatModel model, Func<double> threshold, Func<IReadOnlyList<VoiceInfo>> voices)
    {
        _store = store;
        _books = books;
        _model = model;
        _threshold = threshold;
        _voices = voices;
    }

    // ---------- one line ----------

    /// <summary>Asks about one line and applies the answer (logged as a run of its own). Returns the opinion and any change made.</summary>
    public async Task<(AssistantOpinion Opinion, AssistantChange? Change)> ReviewLineAsync(string bookId, int chapter, string lineId, CancellationToken ct)
    {
        var book = _store.LoadBook(bookId) ?? throw new InvalidOperationException("Book not found.");
        var analysis = _store.LoadAnalysis(bookId, chapter) ?? throw new InvalidOperationException("This chapter hasn't been analysed.");
        var run = new AssistantRun { Kind = AssistantRunKind.Review, State = AssistantRunState.Done, Total = 1 };
        var opinion = await AskAboutLineAsync(book, analysis, lineId, _store.LoadCast(bookId), firstLevel: 0, ct);
        opinion.RunId = run.Id;
        var result = _books.ApplyAssistant(bookId, chapter, new[] { new LineOpinion(lineId, opinion) }, _threshold());
        lock (_store.EditLock(bookId))
        {
            var log = _store.LoadAssistantLog(bookId);
            run.Done = 1; run.Considered = 1; run.Suggested = result.Suggested; run.Changes = result.Changes; run.FinishedUtc = DateTime.UtcNow;
            log.Runs.Add(run);
            _store.SaveAssistantLog(bookId, log);
        }
        return (opinion, result.Changes.FirstOrDefault());
    }

    /// <summary>
    /// One line, starting at <paramref name="firstLevel"/> of context and widening whenever the model
    /// says it needs more, up to <see cref="MaxContextRequests"/> requests for more in all.
    /// </summary>
    private async Task<AssistantOpinion> AskAboutLineAsync(BookRecord book, ChapterAnalysis analysis, string lineId, CastSheet cast, int firstLevel, CancellationToken ct)
    {
        var line = analysis.Lines.First(l => l.Id == lineId);
        var chapterTitle = book.Chapters.FirstOrDefault(c => c.Index == analysis.ChapterIndex)?.Title;
        JsonNode? answer = null;
        int requests = firstLevel;
        for (int level = firstLevel; level < ContextLevels.Length; level++)
        {
            var (before, after) = ContextLevels[level];
            var blocks = BookService.ContextBlocks(analysis, lineId, before, after);
            var shown = blocks.SelectMany(b => b).Where(l => l.IsDialogue).Select(l => l.SpeakerId)
                .Concat(line.Candidates.Select(c => c.SpeakerId)).Distinct().ToList();
            var passage = AssistantPrompts.Passage(blocks, cast, lineId, numbered: new HashSet<string> { lineId });
            // More context only helps if there is more to show.
            bool canAskMore = level < ContextLevels.Length - 1
                && BookService.ContextBlocks(analysis, lineId, ContextLevels[level + 1].Before, ContextLevels[level + 1].After).Count > blocks.Count;
            var messages = new[]
            {
                ChatMessage.System(AssistantPrompts.SystemPrompt),
                ChatMessage.User(AssistantPrompts.ReviewRequest(passage, AssistantPrompts.CastList(cast, shown), $"{book.Title} · {chapterTitle}", requests, canAskMore ? MaxContextRequests : requests)),
            };
            answer = await _model.CompleteJsonAsync(messages, AssistantPrompts.ReviewSchema(AssistantPrompts.SpeakerEnum(cast, shown), canAskMore), 300, ct);
            if (answer?["needMoreContext"]?.GetValue<bool>() != true || !canAskMore) break;
            requests++;
        }
        var opinion = ToOpinion(answer, cast);
        opinion.ContextRequests = requests;
        return opinion;
    }

    // ---------- every line in Review ----------

    public async Task RunReviewAsync(string bookId, IProgress<AssistantProgress>? progress, CancellationToken ct)
    {
        var book = _store.LoadBook(bookId) ?? throw new InvalidOperationException("Book not found.");
        var run = StartRun(bookId, AssistantRunKind.Review);
        // Lines this run already answered (on a resume) are skipped; everything else waiting in Review is asked about.
        var waiting = _books.ReviewQueue(bookId).Where(i => i.Line.Assistant?.RunId != run.Id)
            .GroupBy(i => i.ChapterIndex).ToDictionary(g => g.Key, g => g.Select(i => i.Line.Id).ToHashSet());
        var plan = book.Chapters.Where(c => waiting.ContainsKey(c.Index))
            .Select(c => (Info: c, Passages: Passages(c, _store.LoadAnalysis(bookId, c.Index), waiting[c.Index]))).ToList();
        Record(bookId, run.Id, r => { r.Total = r.Done + plan.Sum(p => p.Passages.Count); r.State = AssistantRunState.Running; });

        await RunPassagesAsync(bookId, book, run.Id, plan, resumeByPosition: false, progress, ct);
        Finish(bookId, run.Id, progress);
    }

    // ---------- the whole book ----------

    public async Task RunWholeBookAsync(string bookId, bool suggestVoices, IProgress<AssistantProgress>? progress, CancellationToken ct)
    {
        var book = _store.LoadBook(bookId) ?? throw new InvalidOperationException("Book not found.");
        var run = StartRun(bookId, AssistantRunKind.WholeBook);
        var plan = book.Chapters.Where(c => BookService.Analyzable(book, c))
            .Select(c => (Info: c, Passages: Passages(c, _store.LoadAnalysis(bookId, c.Index), decide: null))).ToList();
        Record(bookId, run.Id, r => { r.Total = plan.Sum(p => p.Passages.Count) + (suggestVoices ? 1 : 0); r.State = AssistantRunState.Running; });

        await RunPassagesAsync(bookId, book, run.Id, plan, resumeByPosition: true, progress, ct);
        if (suggestVoices)
        {
            await Guard(bookId, run.Id, async () =>
            {
                ct.ThrowIfCancellationRequested();
                Report(bookId, run.Id, progress, "Choosing voices");
                await SuggestVoicesAsync(bookId, ct);
                Record(bookId, run.Id, r => r.Done++);
            });
        }
        Finish(bookId, run.Id, progress);
    }

    /// <summary>One passage: the paragraphs shown, and which dialogue lines in it are being decided (the rest is context).</summary>
    private sealed record Passage(int Chapter, string ChapterTitle, List<List<Line>> Blocks, HashSet<string> Decide);

    /// <summary>
    /// Decides each passage's lines in one request, applies the answers, then follows up line by line,
    /// with wider context, on the lines still unsure. Progress is saved after every passage.
    /// </summary>
    private async Task RunPassagesAsync(string bookId, BookRecord book, string runId, List<(ChapterInfo Info, List<Passage> Passages)> plan,
        bool resumeByPosition, IProgress<AssistantProgress>? progress, CancellationToken ct)
    {
        var start = Reload(bookId, runId);
        foreach (var (info, passages) in plan)
        {
            if (resumeByPosition && info.Index < start.NextChapter) continue;
            int first = resumeByPosition && info.Index == start.NextChapter ? start.NextPassage : 0;
            for (int p = first; p < passages.Count; p++)
            {
                int next = p + 1;
                await Guard(bookId, runId, async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    Report(bookId, runId, progress, $"{info.Title} · passage {p + 1} of {passages.Count}");
                    var cast = _store.LoadCast(bookId);
                    var opinions = await AskAboutPassageAsync(book, passages[p], cast, runId, ct);
                    var applied = opinions.Count == 0 ? new AssistantApplyResult(new(), 0) : _books.ApplyAssistant(bookId, info.Index, opinions, _threshold());
                    Record(bookId, runId, r => { r.Changes.AddRange(applied.Changes); r.Suggested += applied.Suggested; r.Considered += opinions.Count; });

                    // The hard ones: still unsure after the passage, so look closer, with more context.
                    var analysis = _store.LoadAnalysis(bookId, info.Index);
                    var hard = analysis?.Lines.Where(l => passages[p].Decide.Contains(l.Id) && l.IsDialogue && l.Unsure && !l.Reviewed).Select(l => l.Id).ToList() ?? new();
                    foreach (var lineId in hard)
                    {
                        ct.ThrowIfCancellationRequested();
                        Report(bookId, runId, progress, $"{info.Title} · looking closer at a line");
                        analysis = _store.LoadAnalysis(bookId, info.Index)!;
                        var opinion = await AskAboutLineAsync(book, analysis, lineId, _store.LoadCast(bookId), firstLevel: 1, ct);
                        opinion.RunId = runId;
                        var result = _books.ApplyAssistant(bookId, info.Index, new[] { new LineOpinion(lineId, opinion) }, _threshold());
                        Record(bookId, runId, r => { r.Changes.AddRange(result.Changes); r.Suggested += result.Suggested; });
                    }
                    Record(bookId, runId, r =>
                    {
                        r.Done++;
                        (r.NextChapter, r.NextPassage) = next < passages.Count ? (info.Index, next) : (info.Index + 1, 0);
                    });
                });
            }
        }
    }

    /// <summary>Splits a chapter into passages of about <see cref="PassageChars"/>, each with a little lead-in. Only passages with lines to decide are kept.</summary>
    private static List<Passage> Passages(ChapterInfo info, ChapterAnalysis? analysis, ISet<string>? decide)
    {
        var list = new List<Passage>();
        if (analysis is null) return list;
        var blocks = new List<List<Line>>();
        foreach (var l in analysis.Lines)
        {
            if (blocks.Count == 0 || blocks[^1][0].BlockId != l.BlockId) blocks.Add(new List<Line>());
            blocks[^1].Add(l);
        }
        int start = 0;
        while (start < blocks.Count)
        {
            int end = start, chars = 0;
            while (end < blocks.Count && (end == start || chars + Chars(blocks[end]) <= PassageChars)) chars += Chars(blocks[end++]);
            var lead = Math.Max(0, start - LeadInBlocks);
            var ids = blocks.GetRange(start, end - start).SelectMany(b => b)
                .Where(l => l.IsDialogue && (decide is null || decide.Contains(l.Id))).Select(l => l.Id).ToHashSet();
            if (ids.Count > 0) list.Add(new Passage(info.Index, info.Title, blocks.GetRange(lead, end - lead), ids));
            start = end;
        }
        return list;
    }

    private static int Chars(List<Line> block) => block.Sum(l => l.Text.Length);

    /// <summary>Asks about a passage's lines; if the answer is too long to finish, asks about each half of them instead.</summary>
    private async Task<List<LineOpinion>> AskAboutPassageAsync(BookRecord book, Passage passage, CastSheet cast, string runId, CancellationToken ct)
    {
        try { return await AskAboutPassageOnceAsync(book, passage, cast, runId, ct); }
        catch (AssistantAnswerTooLongException) when (passage.Decide.Count > 1)
        {
            var order = passage.Blocks.SelectMany(b => b).Where(l => passage.Decide.Contains(l.Id)).Select(l => l.Id).ToList();
            var half = order.Count / 2;
            var first = await AskAboutPassageAsync(book, passage with { Decide = order.Take(half).ToHashSet() }, cast, runId, ct);
            var second = await AskAboutPassageAsync(book, passage with { Decide = order.Skip(half).ToHashSet() }, cast, runId, ct);
            return first.Concat(second).ToList();
        }
    }

    private async Task<List<LineOpinion>> AskAboutPassageOnceAsync(BookRecord book, Passage passage, CastSheet cast, string runId, CancellationToken ct)
    {
        var shown = passage.Blocks.SelectMany(b => b).Where(l => l.IsDialogue)
            .SelectMany(l => l.Candidates.Select(c => c.SpeakerId).Append(l.SpeakerId)).Distinct().ToList();
        var prompt = AssistantPrompts.Passage(passage.Blocks, cast, numbered: passage.Decide);
        var messages = new[]
        {
            ChatMessage.System(AssistantPrompts.SystemPrompt),
            ChatMessage.User(AssistantPrompts.PassageRequest(prompt, AssistantPrompts.CastList(cast, shown), book.Title, passage.ChapterTitle)),
        };
        var answer = await _model.CompleteJsonAsync(messages, AssistantPrompts.PassageSchema(AssistantPrompts.SpeakerEnum(cast, shown), prompt.Numbered.Count),
            Math.Min(8000, 200 + prompt.Numbered.Count * 60), ct);
        var result = new List<LineOpinion>();
        foreach (var item in answer?["lines"]?.AsArray() ?? new JsonArray())
        {
            if (item?["n"]?.GetValue<int>() is not { } n || !prompt.Numbered.TryGetValue(n, out var line)) continue;
            if (result.Any(r => r.LineId == line.Id)) continue; // answered twice: keep the first
            var opinion = ToOpinion(item, cast, "Decided while reading the passage.");
            opinion.RunId = runId;
            result.Add(new LineOpinion(line.Id, opinion));
        }
        return result;
    }

    // ---------- voices ----------

    /// <summary>
    /// Suggests a voice for the narrator and each main character. The model describes the characters
    /// (gender, age, accent, how they sound); <see cref="VoiceCaster"/> then matches voices to those
    /// descriptions. Suggestions are stored for the Cast tab, where the reader applies them.
    /// </summary>
    public async Task<List<VoiceSuggestion>> SuggestVoicesAsync(string bookId, CancellationToken ct)
    {
        var book = _store.LoadBook(bookId) ?? throw new InvalidOperationException("Book not found.");
        var cast = _store.LoadCast(bookId);
        var voices = _voices();
        var main = cast.Members.Where(m => m.IsNarrator || m.LineCount > 0).OrderByDescending(m => m.IsNarrator).ThenByDescending(m => m.LineCount).Take(VoiceCharacters).ToList();
        if (main.Count == 0 || voices.Count == 0) return new();
        var samples = main.Select(m => (m, (IReadOnlyList<string>)Samples(bookId, book, m))).ToList();
        var messages = new[]
        {
            ChatMessage.System("You help cast voices for full-cast audiobooks. Reply with compact JSON on a single line."),
            ChatMessage.User(AssistantPrompts.DescribeRequest(book.Title, samples)),
        };
        var answer = await _model.CompleteJsonAsync(messages, AssistantPrompts.DescribeSchema(main), 150 + main.Count * 70, ct);

        var setting = answer?["setting"]?.GetValue<string>() ?? "other";
        var described = new List<CharacterSketch>();
        foreach (var item in answer?["characters"]?.AsArray() ?? new JsonArray())
        {
            if (item?["id"]?.GetValue<string>() is not { } id || cast.Find(id) is not { } member || described.Any(d => d.Member.Id == id)) continue;
            described.Add(new CharacterSketch(member,
                item["gender"]?.GetValue<string>() switch { "female" => Gender.Female, "male" => Gender.Male, _ => member.Gender },
                item["age"]?.GetValue<string>() ?? "unknown",
                item["accent"]?.GetValue<string>() ?? "unknown",
                item["sound"]?.GetValue<string>() ?? ""));
        }
        var suggestions = VoiceCaster.Cast(described, setting, voices)
            .Where(s => cast.Find(s.MemberId)?.VoiceId != s.VoiceId).ToList();

        lock (_store.EditLock(bookId))
        {
            // What it learned about genders fills gaps in the cast (it never overrides one already set).
            var sheet = _store.LoadCast(bookId);
            foreach (var d in described)
                if (sheet.Find(d.Member.Id) is { Gender: Gender.Unknown, IsNarrator: false } m && d.Gender != Gender.Unknown) m.Gender = d.Gender;
            _store.SaveCast(bookId, sheet);
            var log = _store.LoadAssistantLog(bookId);
            log.VoiceSuggestions = suggestions;
            _store.SaveAssistantLog(bookId, log);
        }
        return suggestions;
    }

    private List<string> Samples(string bookId, BookRecord book, CastMember member)
    {
        if (member.IsNarrator)
        {
            var first = book.Chapters.FirstOrDefault(c => c.Kind == SectionKind.Body);
            var text = first is null ? null : _store.LoadAnalysis(bookId, first.Index);
            return text?.Lines.Where(l => !l.IsDialogue && l.Text.Length > 60).Take(2).Select(l => Short(l.Text, 200)).ToList() ?? new();
        }
        var said = _books.LinesBy(bookId, member.Id).Select(l => l.Line.Text).Where(t => t.Length > 20)
            .OrderByDescending(t => t.Length).Take(2).Select(t => Short(t, 160));
        // Narration that names them usually says "he" or "she", and often their age.
        var names = member.Aliases.Prepend(member.Name).Where(n => n.Length > 2).ToList();
        var mentions = new List<string>();
        foreach (var info in book.Chapters.Where(c => c.Kind == SectionKind.Body))
        {
            var analysis = _store.LoadAnalysis(bookId, info.Index);
            if (analysis is null) continue;
            mentions.AddRange(analysis.Lines.Where(l => !l.IsDialogue && names.Any(n => l.Text.Contains(n, StringComparison.Ordinal)))
                .Select(l => Short(l.Text.Trim(), 220)).Take(1));
            if (mentions.Count >= 3) break;
        }
        return said.Concat(mentions).ToList();
    }

    // ---------- answers ----------

    private static AssistantOpinion ToOpinion(JsonNode? answer, CastSheet cast, string? defaultReason = null)
    {
        var speaker = answer?["speaker"]?.GetValue<string>() ?? "narrator";
        var opinion = new AssistantOpinion
        {
            Confidence = Math.Clamp(answer?["confidence"]?.GetValue<double>() ?? 0, 0, 1),
            Cue = answer?["tone"]?.GetValue<string>() is { } t && t != "neutral" ? t : null,
            CueConfidence = Math.Clamp(answer?["toneConfidence"]?.GetValue<double>() ?? 0, 0, 1),
            Reason = answer?["reason"]?.GetValue<string>() ?? defaultReason ?? "",
        };
        if (speaker == "narrator") opinion.SpeakerId = CastMember.NarratorId;
        else if (speaker == AssistantPrompts.NewSpeaker && answer?["newName"]?.GetValue<string>() is { Length: > 0 } name) opinion.NewName = name.Trim();
        else if (cast.Find(speaker) is not null) opinion.SpeakerId = speaker;
        else opinion.Confidence = 0; // "new" without a name, or an id it shouldn't have used
        return opinion;
    }

    // ---------- the run log ----------

    /// <summary>Resumes the unfinished run of this kind, or starts a new one. A run of the other kind keeps its place.</summary>
    private AssistantRun StartRun(string bookId, AssistantRunKind kind)
    {
        lock (_store.EditLock(bookId))
        {
            var log = _store.LoadAssistantLog(bookId);
            var run = log.UnfinishedOf(kind);
            if (run is null)
            {
                run = new AssistantRun { Kind = kind, State = AssistantRunState.Running };
                log.Runs.Add(run);
            }
            run.State = AssistantRunState.Running;
            _store.SaveAssistantLog(bookId, log);
            return run;
        }
    }

    /// <summary>Runs a step; if it's cancelled, the run is marked paused (so it can be resumed) before the cancellation goes on.</summary>
    private async Task Guard(string bookId, string runId, Func<Task> step)
    {
        try { await step(); }
        catch (OperationCanceledException)
        {
            Record(bookId, runId, r => { if (r.State == AssistantRunState.Running) r.State = AssistantRunState.Paused; });
            throw;
        }
    }

    private void Record(string bookId, string runId, Action<AssistantRun> update)
    {
        lock (_store.EditLock(bookId))
        {
            var log = _store.LoadAssistantLog(bookId);
            if (log.Runs.FirstOrDefault(r => r.Id == runId) is { } run) update(run);
            _store.SaveAssistantLog(bookId, log);
        }
    }

    private AssistantRun Reload(string bookId, string runId) => _store.LoadAssistantLog(bookId).Runs.First(r => r.Id == runId);

    private void Report(string bookId, string runId, IProgress<AssistantProgress>? progress, string status)
    {
        if (progress is null) return;
        var r = Reload(bookId, runId);
        progress.Report(new(r.Kind, r.Done, r.Total, r.Changes.Count, r.Suggested, status));
    }

    private void Finish(string bookId, string runId, IProgress<AssistantProgress>? progress)
    {
        Record(bookId, runId, r => { r.State = AssistantRunState.Done; r.FinishedUtc = DateTime.UtcNow; r.Done = r.Total; });
        Report(bookId, runId, progress, "Done");
    }

    private static string Short(string text, int max = 80) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
