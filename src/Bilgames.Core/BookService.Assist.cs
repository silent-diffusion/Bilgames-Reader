using Bilgames.Core.Analysis;
using Bilgames.Core.Assist;
using Bilgames.Core.Model;

namespace Bilgames.Core;

/// <summary>An opinion the assistant formed about one line.</summary>
public sealed record LineOpinion(string LineId, AssistantOpinion Opinion);

/// <summary>What applying a batch of opinions did.</summary>
public sealed record AssistantApplyResult(List<AssistantChange> Changes, int Suggested);

public sealed partial class BookService
{
    // ---------- applying the assistant's opinions ----------

    /// <summary>
    /// Applies the assistant's opinions about lines of one chapter. At or above <paramref name="threshold"/>
    /// it changes the speaker (adding a new character if it named one) and the tone; below, it leaves the
    /// opinion beside the line and ranks its pick among the line's candidates for Review. Lines the reader
    /// confirmed, and tones the reader chose, are never changed.
    /// </summary>
    public AssistantApplyResult ApplyAssistant(string bookId, int chapterIndex, IReadOnlyList<LineOpinion> opinions, double threshold)
    {
        lock (_store.EditLock(bookId))
        {
            var analysis = _store.LoadAnalysis(bookId, chapterIndex);
            var changes = new List<AssistantChange>();
            int suggested = 0;
            if (analysis is null) return new(changes, 0);
            var cast = _store.LoadCast(bookId);
            bool castChanged = false;

            foreach (var (lineId, opinion) in opinions)
            {
                var line = analysis.Lines.FirstOrDefault(l => l.Id == lineId);
                if (line is null || !line.IsDialogue) continue;
                line.Assistant = opinion;
                if (line.Reviewed || line.Source == AttributionSource.User) continue; // the reader's decision stands
                bool sure = opinion.Confidence >= threshold;

                // Who the assistant means: a cast member, the narrator, or someone new.
                string? speaker = opinion.SpeakerId;
                if (string.IsNullOrEmpty(speaker) && opinion.NewName is { Length: > 0 } name)
                {
                    var known = FindByName(cast, name);
                    if (known is not null) speaker = known.Id;
                    else if (sure)
                    {
                        _store.SaveCast(bookId, cast);
                        var added = AddSpeakerUnlocked(bookId, name, Gender.Unknown);
                        cast = _store.LoadCast(bookId);
                        speaker = added.Id;
                        castChanged = true;
                    }
                    else speaker = null;
                }
                if (speaker is not null && cast.Find(speaker) is null) speaker = null;

                var change = new AssistantChange
                {
                    Chapter = chapterIndex, LineId = line.Id, Excerpt = Excerpt(line.Text),
                    FromSpeaker = line.SpeakerId, ToSpeaker = line.SpeakerId, FromUnsure = line.Unsure, FromSource = line.Source,
                    FromConfidence = line.Confidence, FromCue = line.Cue, FromCueFrom = line.CueFrom, ToCue = line.Cue,
                    Confidence = opinion.Confidence, Reason = opinion.Reason,
                };
                bool changed = false;

                if (speaker is not null && sure && (speaker != line.SpeakerId || line.Unsure))
                {
                    line.SpeakerId = speaker;
                    line.Unsure = false;
                    line.Source = AttributionSource.Assistant;
                    line.Confidence = opinion.Confidence;
                    change.ToSpeaker = speaker;
                    changed = true;
                }
                else if (speaker is not null && !sure && speaker != line.SpeakerId)
                {
                    // Not sure enough to change it: offer its pick first in Review.
                    line.Candidates.RemoveAll(c => c.SpeakerId == speaker);
                    line.Candidates.Insert(0, new SpeakerCandidate { SpeakerId = speaker, Score = opinion.Confidence });
                    suggested++;
                }

                var cue = Tones.Normalize(opinion.Cue);
                // A tone written in the text ("she whispered") or chosen by the reader stands, and the assistant
                // only ever adds or changes a tone, never clears one (flattening delivery is the reader's call).
                if (sure && cue is not null && opinion.CueConfidence >= threshold && line.CueFrom is not (CueSource.User or CueSource.Tag) && cue != line.Cue)
                {
                    line.Cue = cue;
                    line.CueFrom = CueSource.Assistant;
                    change.ToCue = cue;
                    change.CueChanged = true;
                    changed = true;
                }
                if (changed) changes.Add(change);
            }

            _store.SaveAnalysis(bookId, analysis);
            if (changes.Count > 0 || castChanged)
                if (_store.LoadBook(bookId) is { } book) Recount(book, _store.LoadCast(bookId));
            return new(changes, suggested);
        }
    }

    /// <summary>
    /// Puts back what an assistant run changed, newest first. A line the reader has edited since is
    /// left as the reader left it. Returns how many lines were restored.
    /// </summary>
    public int UndoAssistantRun(string bookId, string runId)
    {
        lock (_store.EditLock(bookId))
        {
            var log = _store.LoadAssistantLog(bookId);
            var run = log.Runs.FirstOrDefault(r => r.Id == runId);
            if (run is null || run.State == AssistantRunState.Undone) return 0;
            int restored = 0;
            foreach (var chapter in run.Changes.GroupBy(c => c.Chapter))
            {
                var analysis = _store.LoadAnalysis(bookId, chapter.Key);
                if (analysis is null) continue;
                foreach (var change in chapter.Reverse())
                {
                    var line = analysis.Lines.FirstOrDefault(l => l.Id == change.LineId);
                    if (line is null || line.Reviewed || line.Source == AttributionSource.User) continue;
                    if (line.Source == AttributionSource.Assistant && line.SpeakerId == change.ToSpeaker)
                    {
                        line.SpeakerId = change.FromSpeaker;
                        line.Unsure = change.FromUnsure;
                        line.Source = change.FromSource;
                        line.Confidence = change.FromConfidence;
                        restored++;
                    }
                    if (change.CueChanged && line.CueFrom == CueSource.Assistant && line.Cue == change.ToCue)
                    {
                        line.Cue = change.FromCue;
                        line.CueFrom = change.FromCueFrom;
                    }
                }
                _store.SaveAnalysis(bookId, analysis);
            }
            run.State = AssistantRunState.Undone;
            _store.SaveAssistantLog(bookId, log);
            if (_store.LoadBook(bookId) is { } book) Recount(book, _store.LoadCast(bookId));
            return restored;
        }
    }

    /// <summary>Gives each character the voice the assistant suggested (all of them, or just these).</summary>
    public int ApplyVoiceSuggestions(string bookId, IReadOnlyCollection<string>? memberIds = null)
    {
        lock (_store.EditLock(bookId))
        {
            var log = _store.LoadAssistantLog(bookId);
            int applied = 0;
            foreach (var s in log.VoiceSuggestions.Where(s => !s.Applied && (memberIds is null || memberIds.Contains(s.MemberId))))
            {
                SetVoiceUnlocked(bookId, s.MemberId, s.VoiceId);
                s.Applied = true;
                applied++;
            }
            _store.SaveAssistantLog(bookId, log);
            return applied;
        }
    }

    private static CastMember? FindByName(CastSheet cast, string name) => cast.Members.FirstOrDefault(m =>
        m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) || m.Aliases.Any(a => a.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static string Excerpt(string text) => text.Length <= 120 ? text : text[..117] + "…";
}
