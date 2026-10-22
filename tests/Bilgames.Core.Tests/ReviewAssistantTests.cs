using System.Text.Json.Nodes;
using Bilgames.Core.Assist;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Tests;

/// <summary>A stand-in model: answers with whatever the test's function returns, and records what it was asked.</summary>
internal sealed class ScriptedModel(Func<IReadOnlyList<ChatMessage>, JsonObject, JsonNode> answer) : IChatModel
{
    public List<(string Prompt, JsonObject Schema)> Requests { get; } = new();

    public Task<JsonNode> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, int maxTokens, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add((messages[^1].Content, schema));
        return Task.FromResult(answer(messages, schema));
    }
}

public sealed class ReviewAssistantTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bilgames-assist-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _store;
    private readonly BookService _books;

    public ReviewAssistantTests()
    {
        _store = new LibraryStore(_root);
        _books = new BookService(_store);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Henry and Rinaldi named once each, then untagged lines the analyzer can't be sure of.</summary>
    private (string BookId, List<Line> Unsure) Book(int untagged = 6)
    {
        var paragraphs = new List<string> { "“We should go,” said Henry.", "“Not yet,” said Rinaldi.", "The lamp flickered." };
        for (int i = 0; i < untagged; i++) paragraphs.Add(i % 2 == 0 ? $"“Is it time, number {i}?”" : $"“Soon enough, number {i}.”");
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        for (int i = 0; i < paragraphs.Count; i++) text.Blocks.Add(new Block { Id = $"c0b{i}", Kind = BlockKind.Paragraph, Text = paragraphs[i] });
        var book = new BookRecord { Id = "bk", Title = "Test" };
        book.Chapters.Add(new ChapterInfo { Index = 0, Title = "One", Kind = SectionKind.Body, WordCount = text.WordCount });
        Directory.CreateDirectory(Path.Combine(_store.BookDir("bk"), "text"));
        _store.SaveChapterText("bk", text);
        _store.SaveBook(book);
        _books.Analyze("bk");
        // Make every untagged line unsure, whatever alternation guessed, so the queue is predictable.
        var a = _store.LoadAnalysis("bk", 0)!;
        foreach (var l in a.Lines.Where(l => l.IsDialogue && l.Text.Contains("number"))) { l.Unsure = true; l.Source = AttributionSource.Fallback; l.SpeakerId = CastMember.NarratorId; }
        _store.SaveAnalysis("bk", a);
        _books.Recount(_store.LoadBook("bk")!, _store.LoadCast("bk"));
        return ("bk", a.Lines.Where(l => l.Unsure).ToList());
    }

    private string Id(string name) => _store.LoadCast("bk").Members.First(m => m.Name == name).Id;
    private Line Line(string id) => _store.LoadAnalysis("bk", 0)!.Lines.First(l => l.Id == id);
    private ReviewAssistant Assistant(IChatModel model, double threshold = 0.8) => new(_store, _books, model, () => threshold, () => VoiceCatalog.All);

    private static JsonObject Opinion(string speaker, double confidence, string tone = "neutral", double toneConfidence = 0.9, bool more = false, string? newName = null)
    {
        var o = new JsonObject { ["speaker"] = speaker, ["confidence"] = confidence, ["tone"] = tone, ["toneConfidence"] = toneConfidence, ["needMoreContext"] = more, ["reason"] = "test" };
        if (newName is not null) o["newName"] = newName;
        return o;
    }

    // ---------- applying ----------

    [Fact]
    public void ChangesOnlyWhenSureAndOtherwiseSuggests()
    {
        var (id, unsure) = Book();
        var henry = Id("Henry");
        var result = _books.ApplyAssistant(id, 0, new[]
        {
            new LineOpinion(unsure[0].Id, new AssistantOpinion { SpeakerId = henry, Confidence = 0.9, Reason = "sure" }),
            new LineOpinion(unsure[1].Id, new AssistantOpinion { SpeakerId = henry, Confidence = 0.6, Reason = "maybe" }),
        }, threshold: 0.8);

        Assert.Single(result.Changes);
        Assert.Equal(1, result.Suggested);
        Assert.Equal((henry, false, AttributionSource.Assistant), (Line(unsure[0].Id).SpeakerId, Line(unsure[0].Id).Unsure, Line(unsure[0].Id).Source));
        var suggested = Line(unsure[1].Id);
        Assert.True(suggested.Unsure);
        Assert.Equal(henry, suggested.Candidates[0].SpeakerId);   // its pick comes first in Review
        Assert.Equal("maybe", suggested.Assistant?.Reason);
    }

    [Fact]
    public void NeverOverridesTheReader()
    {
        var (id, unsure) = Book();
        _books.AssignSpeaker(id, 0, unsure[0].Id, Id("Rinaldi"), applyToSimilar: false);
        _books.SetCue(id, 0, unsure[1].Id, "angry");
        var result = _books.ApplyAssistant(id, 0, new[]
        {
            new LineOpinion(unsure[0].Id, new AssistantOpinion { SpeakerId = Id("Henry"), Confidence = 1 }),
            new LineOpinion(unsure[1].Id, new AssistantOpinion { SpeakerId = Id("Henry"), Confidence = 1, Cue = "sad", CueConfidence = 1 }),
        }, 0.8);

        Assert.Equal(Id("Rinaldi"), Line(unsure[0].Id).SpeakerId);
        Assert.Equal("angry", Line(unsure[1].Id).Cue);              // the reader's tone stands
        Assert.Equal(Id("Henry"), Line(unsure[1].Id).SpeakerId);    // (the speaker wasn't confirmed, so it can change)
        Assert.Single(result.Changes);
    }

    [Fact]
    public void AddsOrChangesTonesButNeverClearsOne()
    {
        var (id, unsure) = Book();
        var a = _store.LoadAnalysis(id, 0)!;
        a.Lines.First(l => l.Id == unsure[0].Id).Cue = "excited";
        a.Lines.First(l => l.Id == unsure[0].Id).CueFrom = CueSource.Context;
        _store.SaveAnalysis(id, a);

        _books.ApplyAssistant(id, 0, new[]
        {
            new LineOpinion(unsure[0].Id, new AssistantOpinion { SpeakerId = Id("Henry"), Confidence = 1, Cue = null, CueConfidence = 1 }),       // "neutral"
            new LineOpinion(unsure[1].Id, new AssistantOpinion { SpeakerId = Id("Henry"), Confidence = 1, Cue = "whispered", CueConfidence = 1 }),
            new LineOpinion(unsure[2].Id, new AssistantOpinion { SpeakerId = Id("Henry"), Confidence = 1, Cue = "angry", CueConfidence = 0.5 }), // tone not sure enough
        }, 0.8);

        Assert.Equal("excited", Line(unsure[0].Id).Cue);
        Assert.Equal(("whispered", CueSource.Assistant), (Line(unsure[1].Id).Cue, Line(unsure[1].Id).CueFrom));
        Assert.Null(Line(unsure[2].Id).Cue);
    }

    [Fact]
    public void NamesSomeoneNewOnlyWhenSure()
    {
        var (id, unsure) = Book();
        _books.ApplyAssistant(id, 0, new[]
        {
            new LineOpinion(unsure[0].Id, new AssistantOpinion { NewName = "Catherine", Confidence = 0.95 }),
            new LineOpinion(unsure[1].Id, new AssistantOpinion { NewName = "Ferguson", Confidence = 0.5 }),
        }, 0.8);
        var cast = _store.LoadCast(id);
        Assert.Equal(Id("Catherine"), Line(unsure[0].Id).SpeakerId);
        Assert.DoesNotContain(cast.Members, m => m.Name == "Ferguson");
    }

    [Fact]
    public void UndoPutsBackWhatItChangedButNotWhatTheReaderChangedSince()
    {
        var (id, unsure) = Book();
        var henry = Id("Henry");
        // "Henry" for every numbered line, including Rinaldi's tagged one (so that change is undone too).
        var assistant = Assistant(new ScriptedModel((_, schema) =>
            schema["properties"]!["lines"] is { } lines
                ? new JsonObject { ["lines"] = new JsonArray(Enumerable.Range(1, lines["maxItems"]!.GetValue<int>()).Select(n => (JsonNode)new JsonObject { ["n"] = n, ["speaker"] = henry, ["confidence"] = 0.95, ["tone"] = "neutral", ["toneConfidence"] = 0.9 }).ToArray()) }
                : Opinion(henry, 0.95)));
        var rinaldiLine = _store.LoadAnalysis(id, 0)!.Lines.First(l => l.Text.Contains("Not yet")).Id;
        assistant.RunWholeBookAsync(id, suggestVoices: false, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.All(unsure, l => Assert.Equal(henry, Line(l.Id).SpeakerId));

        _books.AssignSpeaker(id, 0, unsure[0].Id, Id("Rinaldi"), applyToSimilar: false); // the reader corrects one afterwards
        var run = _store.LoadAssistantLog(id).Runs.Last();
        var restored = _books.UndoAssistantRun(id, run.Id);

        Assert.Equal(run.Changes.Count - 1, restored);                  // all but the line the reader changed
        Assert.Equal(Id("Rinaldi"), Line(unsure[0].Id).SpeakerId);
        Assert.Equal(Id("Rinaldi"), Line(rinaldiLine).SpeakerId);         // its wrong change to a tagged line, undone
        Assert.All(unsure.Skip(1), l => Assert.True(Line(l.Id).Unsure));
        Assert.Equal(AssistantRunState.Undone, _store.LoadAssistantLog(id).Runs.Last().State);
    }

    // ---------- asking ----------

    [Fact]
    public async Task AsksForMoreContextAtMostThreeTimes()
    {
        var (id, unsure) = Book(untagged: 40);
        var model = new ScriptedModel((_, _) => Opinion("narrator", 0.3, more: true));
        var (opinion, _) = await Assistant(model).ReviewLineAsync(id, 0, unsure[20].Id, CancellationToken.None);

        Assert.Equal(4, model.Requests.Count);                         // the first look, then three more
        Assert.Equal(ReviewAssistant.MaxContextRequests, opinion.ContextRequests);
        Assert.True(model.Requests[^1].Prompt.Length > model.Requests[0].Prompt.Length); // each time it saw more
        Assert.Contains("★", model.Requests[0].Prompt);
        // On the last request it can't ask for more.
        Assert.Equal(new JsonArray(false).ToJsonString(), model.Requests[^1].Schema["properties"]!["needMoreContext"]!["enum"]!.ToJsonString());
    }

    [Fact]
    public async Task OnlyRealCastIdsCanBeAnswered()
    {
        var (id, unsure) = Book();
        var model = new ScriptedModel((_, _) => Opinion("not-a-character", 0.99));
        var (opinion, change) = await Assistant(model).ReviewLineAsync(id, 0, unsure[0].Id, CancellationToken.None);
        var allowed = model.Requests[0].Schema["properties"]!["speaker"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.Contains(Id("Henry"), allowed);
        Assert.Contains("narrator", allowed);
        Assert.Equal(0, opinion.Confidence); // an id it shouldn't have used counts for nothing
        Assert.Null(change);
    }

    [Fact]
    public async Task ReviewRunPausesAndResumesWhereItStopped()
    {
        var (id, unsure) = Book(untagged: 60);
        var henry = Id("Henry");
        int calls = 0;
        using var cts = new CancellationTokenSource();
        var model = new ScriptedModel((_, schema) =>
        {
            if (++calls == 2) cts.Cancel(); // pause during the second request
            return new JsonObject { ["lines"] = new JsonArray() }; // no opinions: lines stay unsure
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assistant(model).RunReviewAsync(id, null, cts.Token));
        var paused = _store.LoadAssistantLog(id).Runs.Last();
        Assert.Equal(AssistantRunState.Paused, paused.State);

        var resumed = new ScriptedModel((_, schema) => schema["properties"]!["lines"] is not null
            ? new JsonObject { ["lines"] = new JsonArray() }
            : Opinion(henry, 0.95));
        await Assistant(resumed).RunReviewAsync(id, null, CancellationToken.None);
        var log = _store.LoadAssistantLog(id);
        Assert.Single(log.Runs);                                        // the same run, continued
        Assert.Equal(AssistantRunState.Done, log.Runs[0].State);
        // Every line it hadn't answered before the pause was settled; the one it had answered (unsure)
        // isn't asked again, and waits in Review with its opinion.
        var left = _store.LoadAnalysis(id, 0)!.Lines.Where(l => l.Unsure && !l.Reviewed).ToList();
        Assert.True(left.Count <= 1);
        Assert.All(left, l => Assert.Equal(paused.Id, l.Assistant?.RunId));
    }

    // ---------- voices ----------

    [Fact]
    public void CastsVoicesByGenderAccentQualityAndKeepsThemUnique()
    {
        var narrator = new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = "af_heart" };
        var alice = new CastMember { Id = "a", Name = "Alice", LineCount = 400 };
        var rabbit = new CastMember { Id = "r", Name = "Rabbit", LineCount = 30 };
        var queen = new CastMember { Id = "q", Name = "Queen", LineCount = 60 };
        var sketches = new[]
        {
            new CharacterSketch(narrator, Gender.Unknown, "adult", "unknown", "calm"),
            new CharacterSketch(alice, Gender.Female, "child", "british", "curious"),
            new CharacterSketch(rabbit, Gender.Male, "adult", "unknown", "anxious"),
            new CharacterSketch(queen, Gender.Female, "adult", "british", "furious"),
        };
        var picks = VoiceCaster.Cast(sketches, "british", VoiceCatalog.All).ToDictionary(s => s.MemberId, s => s.VoiceId);

        Assert.Equal("bf_emma", picks[narrator.Id]);  // the narrator's voice gender, the setting's accent, best quality
        Assert.Equal("bf_isabella", picks["a"]);       // next best British female (Emma is taken)
        Assert.Equal("bm_george", picks["r"]);         // male, British setting, best quality
        Assert.StartsWith("bf_", picks["q"]);
        Assert.Equal(4, picks.Values.Distinct().Count());
    }
}
