using System.Collections.ObjectModel;
using Bilgames.App.Services;
using Bilgames.Core;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public sealed class ContextLineViewModel
{
    public string Label { get; init; } = "";
    public string Color { get; init; } = "#C9CED6";
    public string Text { get; init; } = "";
}

public sealed partial class SpeakerOptionViewModel : ObservableObject
{
    public string SpeakerId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Initials { get; init; } = "";
    public string Color { get; init; } = "#C9CED6";
    public string Confidence { get; init; } = "";
    public string AccessibleName => $"{Name}, {Confidence}";

    [ObservableProperty] private bool _isSelected;

    public string BorderColor => IsSelected ? "#F0A848" : "#2A303A";
    public string Background => IsSelected ? "#232934" : "#00000000";

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(Background));
    }
}

public sealed partial class ReviewViewModel : ObservableObject
{
    private readonly BookViewModel _book;
    private List<ReviewItem>? _queue;
    private ReviewItem? _current;
    private bool _fixMode;

    public ReviewViewModel(BookViewModel book) => _book = book;

    public ObservableCollection<LegendEntry> Legend { get; } = new();
    public ObservableCollection<SpeakerOptionViewModel> Options { get; } = new();

    // ---------- context ----------

    private ChapterAnalysis? _analysis;
    [ObservableProperty] private int _contextLevel;
    [ObservableProperty] private bool _canMoreContext;
    [ObservableProperty] private bool _canLessContext;

    /// <summary>The passage around the line at the current expansion level, speakers colour-coded.</summary>
    private void BuildContext()
    {
        Legend.Clear();
        if (_current is not { } item || _analysis is null) { ContextParagraphs = null; CanMoreContext = CanLessContext = false; return; }
        var blocks = ContextHelpers.Around(_analysis, item.Line.Id, ContextLevel, wide: true);
        ContextParagraphs = ContextHelpers.Runs(blocks, _cast, item.Line.Id);
        foreach (var e in ContextHelpers.Legend(blocks, _cast)) Legend.Add(e);
        CanMoreContext = ContextHelpers.HasMore(_analysis, item.Line.Id, ContextLevel, wide: true);
        CanLessContext = ContextLevel > 0;
    }

    [RelayCommand]
    private void MoreContext()
    {
        if (!CanMoreContext) return;
        ContextLevel++;
        BuildContext();
    }

    [RelayCommand]
    private void LessContext()
    {
        if (ContextLevel == 0) return;
        ContextLevel--;
        BuildContext();
    }

    // ---------- tone ----------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToneLabel), nameof(ToneButtonName), nameof(ToneSource))] private string? _cue;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToneSource))] private CueSource _cueFrom;
    [ObservableProperty] private bool _isHearing;

    public string ToneLabel => ToneText.Label(Cue);
    public string ToneSource => ToneText.Source(CueFrom, Cue);
    public string ToneButtonName => $"Tone: {ToneLabel}. Change how this line is delivered";

    public List<Bilgames.App.Controls.PickerItem> ToneItems() => ToneText.Items(Cue);

    public async Task SetToneAsync(string? cue)
    {
        if (_current is not { } item || _book.BookId is not { } id) return;
        cue = string.IsNullOrEmpty(cue) ? null : cue;
        try { await Task.Run(() => AppServices.Books.SetCue(id, item.ChapterIndex, item.Line.Id, cue)); }
        catch (Exception ex) { AppServices.Log($"Set tone failed: {ex}"); Status = $"Couldn't save the tone ({ex.Message}). Please try again."; return; }
        item.Line.Cue = cue;
        item.Line.CueFrom = CueSource.User;
        Cue = cue;
        CueFrom = CueSource.User;
        _book.Changed();
    }

    /// <summary>Plays the line in the selected speaker's voice with its tone (speed, loudness).</summary>
    [RelayCommand]
    private async Task HearItAsync()
    {
        if (_current is not { } item || IsHearing) return;
        if (!await CastViewModel.EnsureModel()) return;
        var speaker = Options.FirstOrDefault(o => o.IsSelected)?.SpeakerId ?? item.Line.SpeakerId;
        var voice = _cast.Find(speaker)?.VoiceId ?? _cast.Members.FirstOrDefault(m => m.IsNarrator)?.VoiceId ?? Bilgames.Core.Tts.VoiceCatalog.DefaultNarrator;
        IsHearing = true;
        try { await _book.Shell.Player.PreviewAsync(item.Line.Text, voice, Bilgames.Core.Analysis.Tones.DeliveryFor(Cue)); }
        catch (Exception ex) { AppServices.Log("Hear it failed: " + ex.Message); }
        finally { IsHearing = false; }
    }

    [ObservableProperty] private bool _hasItem;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isAnalyzed;
    [ObservableProperty] private string _progressLabel = "";
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private string _lineText = "";
    [ObservableProperty] private bool _applySimilar = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<Bilgames.App.Controls.ContextRun>>? _contextParagraphs;
    private CastSheet _cast = new();

    // Lines to review when this book's session began, so the counter reads "2 of 479" after a confirm, not "1 of 478".
    private string? _sessionBookId;
    private int _sessionTotal;
    private readonly List<string> _recent = new();

    public void Reset()
    {
        _queue = null;
        _current = null;
        _fixMode = false;
    }

    public void EnsureLoaded()
    {
        IsAnalyzed = _book.Record?.Analysis == AnalysisState.Done;
        if (_fixMode && _current is not null) return;
        if (_queue is null) LoadQueue(0);
    }

    private void LoadQueue(int index)
    {
        if (_book.BookId is not { } id) return;
        _queue = AppServices.Books.ReviewQueue(id);
        if (_sessionBookId != id) { _sessionBookId = id; _sessionTotal = 0; _recent.Clear(); }
        _sessionTotal = Math.Max(_sessionTotal, _queue.Count);
        _fixMode = false;
        Show(_queue.Count == 0 ? null : _queue[Math.Min(index, _queue.Count - 1)]);
    }

    /// <summary>Opened from the player's "Fix" button: review that exact line, whoever said it.</summary>
    public void FocusLine(int chapterIndex, string lineId)
    {
        if (_book.BookId is not { } id) return;
        IsAnalyzed = _book.Record?.Analysis == AnalysisState.Done;
        var item = AppServices.Books.LineForFix(id, chapterIndex, lineId);
        if (item is null) { EnsureLoaded(); return; }
        _fixMode = true;
        Show(item);
    }

    private void Show(ReviewItem? item)
    {
        _current = item;
        HasItem = item is not null;
        IsDone = item is null && IsAnalyzed;
        Legend.Clear();
        ContextParagraphs = null;
        Options.Clear();
        Status = "";
        if (item is null) return;

        var cast = AppServices.Store.LoadCast(_book.BookId!);
        CastMember? Member(string id) => cast.Find(id);

        // "Chapter 1 · 2 of 479 · 477 left": the total stays fixed for the session, so each confirm moves you forward.
        int done = Math.Max(0, _sessionTotal - item.Total), position = done + item.Position, left = item.Total - item.Position;
        ProgressLabel = _fixMode ? $"{item.ChapterTitle} · fixing one line" : $"{item.ChapterTitle} · {position:N0} of {Math.Max(_sessionTotal, item.Total):N0} · {left:N0} left";
        Prompt = _fixMode ? "Who says this?" : item.Line.Source == AttributionSource.Fallback && item.Line.Candidates.Count > 0 ? "No dialogue tag · who says this?" : "Who says this?";
        LineText = item.Line.Text;
        Cue = Bilgames.Core.Analysis.Tones.Normalize(item.Line.Cue);
        CueFrom = item.Line.CueFrom;

        // A few paragraphs either side, as one continuous text with speakers colour-coded.
        _cast = cast;
        _analysis = AppServices.Store.LoadAnalysis(_book.BookId!, item.ChapterIndex);
        ContextLevel = 0;
        BuildContext();

        IEnumerable<(string Id, string Conf)> choices;
        if (_fixMode || item.Line.Candidates.Count == 0)
        {
            choices = cast.Members.Where(m => !m.IsNarrator && m.LineCount > 0).OrderByDescending(m => m.LineCount).Take(8)
                .Select(m => (m.Id, m.Id == item.Line.SpeakerId ? "Current" : $"{m.LineCount:N0} lines"));
        }
        else
        {
            choices = item.Line.Candidates.Where(c => Member(c.SpeakerId) is not null).Take(3)
                .Select(c => (c.SpeakerId, $"{Math.Round(c.Score * 100)}% likely"));
        }
        foreach (var (cid, conf) in choices)
        {
            var m = Member(cid)!;
            Options.Add(new SpeakerOptionViewModel { SpeakerId = cid, Name = m.Name, Initials = m.Initials, Color = m.Color, Confidence = conf });
        }
        // The speakers you picked most recently (e.g. someone you just added), so the next line in the scene is one click.
        foreach (var rid in _recent.Where(r => Options.All(o => o.SpeakerId != r)).Take(3))
        {
            if (Member(rid) is not { } rm || rm.IsNarrator) continue;
            Options.Add(new SpeakerOptionViewModel { SpeakerId = rid, Name = rm.Name, Initials = rm.Initials, Color = rm.Color, Confidence = "Recent" });
        }
        var narrator = cast.Narrator;
        Options.Add(new SpeakerOptionViewModel { SpeakerId = narrator.Id, Name = "Narrator", Initials = "N", Color = narrator.Color, Confidence = _fixMode && item.Line.SpeakerId == narrator.Id ? "Current" : "Fallback" });

        var preselect = _fixMode ? Options.FirstOrDefault(o => o.SpeakerId == item.Line.SpeakerId) : Options.FirstOrDefault();
        if (preselect is not null) preselect.IsSelected = true;
    }

    /// <summary>"Someone else…": choose any speaker from the searchable list.</summary>
    public List<Bilgames.App.Controls.PickerItem> AllSpeakers() => ContextHelpers.SpeakerItems(_cast, _current?.Line.SpeakerId);

    /// <summary>"Someone else…" → a name that isn't in the cast yet: add them and select them.</summary>
    public async Task AddAndPickAsync(string name)
    {
        if (_book.BookId is not { } id) return;
        CastMember member;
        try { member = await Task.Run(() => AppServices.Books.AddSpeaker(id, name)); }
        catch (Exception ex) { AppServices.Log($"Add speaker failed: {ex}"); Status = $"Couldn't add {name} ({ex.Message}). Please try again."; return; }
        AppServices.Log($"Review: added speaker {member.Name} ({member.Id}) to {id}");
        _cast = AppServices.Store.LoadCast(id);
        PickOther(member.Id);
        Status = $"Added {member.Name} (voice {Bilgames.Core.Tts.VoiceCatalog.Name(member.VoiceId)}). Confirm to give them this line.";
    }

    public void PickOther(string speakerId)
    {
        var existing = Options.FirstOrDefault(o => o.SpeakerId == speakerId);
        if (existing is null)
        {
            var m = _cast.Find(speakerId);
            if (m is null) return;
            existing = new SpeakerOptionViewModel { SpeakerId = m.Id, Name = m.Name, Initials = m.Initials, Color = m.Color, Confidence = "Chosen" };
            Options.Insert(Math.Max(0, Options.Count - 1), existing);
        }
        Pick(existing);
    }

    [RelayCommand]
    private void Pick(SpeakerOptionViewModel? option)
    {
        if (option is null) return;
        foreach (var o in Options) o.IsSelected = o == option;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_current is null || _book.BookId is not { } id) return;
        var choice = Options.FirstOrDefault(o => o.IsSelected);
        if (choice is null) return;
        var item = _current;
        bool similar = ApplySimilar && !_fixMode;
        int index = Math.Max(0, item.Position - 1);
        int changed;
        try
        {
            changed = await Task.Run(() => AppServices.Books.AssignSpeaker(id, item.ChapterIndex, item.Line.Id, choice.SpeakerId, similar));
            AppServices.Log($"Review: {id} ch{item.ChapterIndex} {item.Line.Id} → {choice.Name} ({choice.SpeakerId}), {changed} line(s)");
            _recent.Remove(choice.SpeakerId);
            _recent.Insert(0, choice.SpeakerId);
        }
        catch (Exception ex)
        {
            // Never move on as if it saved: the line stays here so it can be confirmed again.
            AppServices.Log($"Review save failed for {item.Line.Id}: {ex}");
            Status = $"Couldn't save that ({ex.Message}). Please try again.";
            return;
        }
        _book.Changed();
        LoadQueue(index);
        Status = changed > 1 ? $"Set {changed} lines in that exchange." : "";
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (_current is null || _book.BookId is not { } id) return;
        var item = _current;
        if (_fixMode) { LoadQueue(0); return; }
        try { await Task.Run(() => AppServices.Books.Skip(id, item.ChapterIndex, item.Line.Id)); }
        catch (Exception ex)
        {
            AppServices.Log($"Review skip failed for {item.Line.Id}: {ex}");
            Status = $"Couldn't save that ({ex.Message}). Please try again.";
            return;
        }
        _book.Changed();
        LoadQueue(Math.Max(0, item.Position - 1));
    }

    [RelayCommand] private void GoGenerate() => _book.Tab = BookTab.Generate;
}
