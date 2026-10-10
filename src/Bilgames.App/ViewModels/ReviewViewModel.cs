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

        ProgressLabel = _fixMode ? $"{item.ChapterTitle} · fixing one line" : $"{item.ChapterTitle} · {item.Position:N0} of {item.Total:N0}";
        Prompt = _fixMode ? "Who says this?" : item.Line.Source == AttributionSource.Fallback && item.Line.Candidates.Count > 0 ? "No dialogue tag · who says this?" : "Who says this?";
        LineText = item.Line.Text;

        // A few paragraphs either side, as one continuous text with speakers colour-coded.
        _cast = cast;
        var blocks = ContextHelpers.Trim(AppServices.Books.ContextFor(_book.BookId!, item.ChapterIndex, item.Line.Id, 4, 3), item.Line.Id, 520, 380);
        ContextParagraphs = ContextHelpers.Runs(blocks, cast, item.Line.Id);
        foreach (var e in ContextHelpers.Legend(blocks, cast)) Legend.Add(e);

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
        var narrator = cast.Narrator;
        Options.Add(new SpeakerOptionViewModel { SpeakerId = narrator.Id, Name = "Narrator", Initials = "N", Color = narrator.Color, Confidence = _fixMode && item.Line.SpeakerId == narrator.Id ? "Current" : "Fallback" });

        var preselect = _fixMode ? Options.FirstOrDefault(o => o.SpeakerId == item.Line.SpeakerId) : Options.FirstOrDefault();
        if (preselect is not null) preselect.IsSelected = true;
    }

    /// <summary>"Someone else…": choose any speaker from the searchable list.</summary>
    public List<Bilgames.App.Controls.PickerItem> AllSpeakers() => ContextHelpers.SpeakerItems(_cast, _current?.Line.SpeakerId);

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
        int changed = await Task.Run(() => AppServices.Books.AssignSpeaker(id, item.ChapterIndex, item.Line.Id, choice.SpeakerId, similar));
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
        await Task.Run(() => AppServices.Books.Skip(id, item.ChapterIndex, item.Line.Id));
        _book.Changed();
        LoadQueue(Math.Max(0, item.Position - 1));
    }

    [RelayCommand] private void GoGenerate() => _book.Tab = BookTab.Generate;
}
