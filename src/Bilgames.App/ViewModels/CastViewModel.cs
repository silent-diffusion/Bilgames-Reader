using System.Collections.ObjectModel;
using Bilgames.App.Services;
using Bilgames.Core.Model;
using Bilgames.Core.Tts;
using Bilgames.Tts.Kokoro;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public sealed partial class CastMemberViewModel : ObservableObject
{
    public CastMemberViewModel(CastMember m)
    {
        Id = m.Id;
        Name = m.Name;
        Initials = m.Initials;
        Color = m.Color;
        IsNarrator = m.IsNarrator;
        Update(m);
    }

    public string Id { get; }
    public string Name { get; }
    public string Initials { get; }
    public string Color { get; }
    public bool IsNarrator { get; }

    [ObservableProperty] private string _linesLabel = "";
    [ObservableProperty] private string _voiceId = "";
    [ObservableProperty] private string _voiceName = "";
    [ObservableProperty] private bool _isPreviewing;

    public string VoiceButtonName => $"Change voice for {Name}, currently {VoiceName}";
    public string PreviewName => $"Preview {Name}";
    public string LinesButtonName => $"{Name}: show and reassign lines";

    public void Update(CastMember m)
    {
        VoiceId = m.VoiceId;
        VoiceName = VoiceCatalog.Name(m.VoiceId);
        if (m.IsNarrator)
        {
            LinesLabel = "All non-dialogue text";
        }
        else
        {
            double ratio = m.LineCount == 0 ? 0 : (double)m.ConfidentCount / m.LineCount;
            var sure = ratio >= 0.8 ? "confident" : ratio >= 0.5 ? "mostly sure" : "some guesses";
            LinesLabel = $"{m.LineCount:N0} {(m.LineCount == 1 ? "line" : "lines")} · {sure}";
        }
        OnPropertyChanged(nameof(VoiceButtonName));
    }
}

public sealed partial class CastViewModel : ObservableObject
{
    private readonly BookViewModel _book;
    private List<MergeSuggestion> _bannerGroup = new();

    public CastViewModel(BookViewModel book)
    {
        _book = book;
        Detail = new SpeakerLinesViewModel(book);
    }

    public SpeakerLinesViewModel Detail { get; }

    public ObservableCollection<CastMemberViewModel> Members { get; } = new();

    [ObservableProperty] private bool _isAnalyzed;
    [ObservableProperty] private string _intro = "";
    [ObservableProperty] private bool _showMerge;
    [ObservableProperty] private string _mergeFromText = "";
    [ObservableProperty] private string _mergeIntoText = "";
    [ObservableProperty] private int _unsureCount;
    [ObservableProperty] private string _hiddenLabel = "";

    public bool HasUnsure => UnsureCount > 0;
    partial void OnUnsureCountChanged(int value) => OnPropertyChanged(nameof(HasUnsure));

    public void Refresh()
    {
        var b = _book.Record;
        if (b is null) return;
        IsAnalyzed = b.Analysis == AnalysisState.Done;
        var cast = AppServices.Store.LoadCast(b.Id);
        var shown = cast.Members.Where(m => m.IsNarrator || m.LineCount > 0).ToList();
        int hidden = cast.Members.Count - shown.Count;
        HiddenLabel = hidden > 0 ? $"{hidden} more {(hidden == 1 ? "name appears" : "names appear")} only as possible speakers in Review." : "";

        // Update in place where possible so voice menus don't flicker.
        if (Members.Select(m => m.Id).SequenceEqual(shown.Select(m => m.Id)))
        {
            for (int i = 0; i < shown.Count; i++) Members[i].Update(shown[i]);
        }
        else
        {
            Members.Clear();
            foreach (var m in shown) Members.Add(new CastMemberViewModel(m));
        }

        int speakers = shown.Count(m => !m.IsNarrator);
        Intro = IsAnalyzed
            ? $"{speakers} {(speakers == 1 ? "speaker" : "speakers")} found in {b.Title}. Pick a voice to change it."
            : "Speakers haven't been found yet. Until then, the narrator reads everything.";
        UnsureCount = b.UnsureCount;

        var groups = cast.Suggestions.GroupBy(s => s.IntoId).ToList();
        var first = groups.FirstOrDefault();
        ShowMerge = first is not null;
        if (first is not null)
        {
            _bannerGroup = first.ToList();
            var names = _bannerGroup.Select(s => cast.Find(s.FromId)?.Name).Where(n => n is not null).Select(n => $"“{n}”").ToList();
            MergeFromText = names.Count switch
            {
                1 => names[0],
                2 => $"{names[0]} and {names[1]}",
                _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
            };
            MergeIntoText = cast.Find(first.Key)?.Name ?? "";
        }
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (_book.BookId is not { } id) return;
        var group = _bannerGroup.ToList();
        await Task.Run(() => { foreach (var s in group) AppServices.Books.Merge(id, s.FromId, s.IntoId); });
        _book.Changed();
    }

    [RelayCommand]
    private void KeepApart()
    {
        if (_book.BookId is not { } id) return;
        foreach (var s in _bannerGroup) AppServices.Books.KeepApart(id, s);
        Refresh();
    }

    [RelayCommand] private void CheckThem() => _book.Tab = BookTab.Review;

    [RelayCommand] private Task FindSpeakersAsync() => _book.Structure.FindSpeakersCommand.ExecuteAsync(null);

    public void SetVoice(CastMemberViewModel member, string voiceId)
    {
        if (_book.BookId is not { } id) return;
        AppServices.Books.SetVoice(id, member.Id, voiceId);
        member.VoiceId = voiceId;
        member.VoiceName = VoiceCatalog.Name(voiceId);
        _book.Changed();
    }

    [RelayCommand]
    private async Task PreviewAsync(CastMemberViewModel? member)
    {
        if (member is null || _book.BookId is not { } id) return;
        if (!await EnsureModel()) return;
        foreach (var m in Members) m.IsPreviewing = m == member;
        try
        {
            var text = SampleLine(id, member);
            await _book.Shell.Player.PreviewAsync(text, member.VoiceId);
        }
        catch (Exception ex)
        {
            AppServices.Log("Preview failed: " + ex.Message);
        }
        finally
        {
            member.IsPreviewing = false;
        }
    }

    /// <summary>Previews and generation need the standard model; offer the (confirmed) download.</summary>
    public static async Task<bool> EnsureModel(ModelInfo? model = null)
    {
        model ??= KokoroModels.Half;
        if (AppServices.Models.IsInstalled(model)) return true;
        var go = await Dialogs.ConfirmAsync("Voice model needed",
            $"Voices need the {model.Label} model. It's a one-time download.",
            $"Download ({Controls.Ui.Size(model.Size)})");
        return go && await AppServices.Models.RequestDownloadAsync(model);
    }

    /// <summary>A line this character actually says in the book, so the preview is in context.</summary>
    private static string SampleLine(string bookId, CastMemberViewModel member)
    {
        var book = AppServices.Store.LoadBook(bookId);
        if (book is not null)
        {
            foreach (var c in book.Chapters.Where(c => c.Kind == SectionKind.Body).Take(30))
            {
                var a = AppServices.Store.LoadAnalysis(bookId, c.Index);
                var line = member.IsNarrator
                    ? a?.Lines.FirstOrDefault(l => !l.IsDialogue && l.Text.Length is > 60 and < 260)
                    : a?.Lines.FirstOrDefault(l => l.IsDialogue && l.SpeakerId == member.Id && !l.Unsure && l.Text.Length is > 20 and < 240);
                if (line is not null) return line.Text;
            }
        }
        return member.IsNarrator ? "This is the narrator's voice for this book." : $"Hello. This is how {member.Name} will sound.";
    }
}

/// <summary>One of a speaker's lines in the speaker detail list.</summary>
public sealed partial class SpeakerLineItem : ObservableObject
{
    public int ChapterIndex { get; init; }
    public string ChapterTitle { get; init; } = "";
    public string LineId { get; init; } = "";
    public string Text { get; init; } = "";
    public IReadOnlyList<IReadOnlyList<Bilgames.App.Controls.ContextRun>> LineOnly { get; init; } = Array.Empty<IReadOnlyList<Bilgames.App.Controls.ContextRun>>();
    public IReadOnlyList<IReadOnlyList<Bilgames.App.Controls.ContextRun>> WithContext { get; init; } = Array.Empty<IReadOnlyList<Bilgames.App.Controls.ContextRun>>();

    [ObservableProperty] private IReadOnlyList<IReadOnlyList<Bilgames.App.Controls.ContextRun>>? _shown;

    public string ReassignName => $"Reassign this line: {Text}";
}

/// <summary>All lines of one speaker, each optionally with its surrounding paragraphs, reassignable.</summary>
public sealed partial class SpeakerLinesViewModel : ObservableObject
{
    private readonly BookViewModel _book;
    private CastSheet _cast = new();

    public SpeakerLinesViewModel(BookViewModel book) => _book = book;

    public ObservableCollection<SpeakerLineItem> Lines { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _speakerId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private string _color = "#C9CED6";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showContext = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";

    partial void OnShowContextChanged(bool value)
    {
        foreach (var l in Lines) l.Shown = value ? l.WithContext : l.LineOnly;
    }

    public async Task OpenAsync(CastMemberViewModel member)
    {
        if (_book.BookId is not { } id) return;
        SpeakerId = member.Id;
        Name = member.Name;
        Initials = member.Initials;
        Color = member.Color;
        Status = "";
        IsOpen = true;
        await LoadAsync(id);
    }

    private async Task LoadAsync(string bookId)
    {
        IsLoading = true;
        Lines.Clear();
        var speaker = SpeakerId;
        var (cast, items) = await Task.Run(() =>
        {
            var c = AppServices.Store.LoadCast(bookId);
            var lines = AppServices.Books.LinesBy(bookId, speaker);
            var analyses = new Dictionary<int, ChapterAnalysis?>();
            var list = new List<SpeakerLineItem>();
            foreach (var l in lines)
            {
                if (!analyses.TryGetValue(l.ChapterIndex, out var a)) analyses[l.ChapterIndex] = a = AppServices.Store.LoadAnalysis(bookId, l.ChapterIndex);
                var context = a is null ? new List<List<Line>>() : ContextHelpers.Trim(Bilgames.Core.BookService.ContextBlocks(a, l.Line.Id, 2, 2), l.Line.Id, 260, 200);
                list.Add(new SpeakerLineItem
                {
                    ChapterIndex = l.ChapterIndex,
                    ChapterTitle = l.ChapterTitle,
                    LineId = l.Line.Id,
                    Text = l.Line.Text,
                    LineOnly = ContextHelpers.Runs(new[] { new[] { l.Line } }, c, null),
                    WithContext = ContextHelpers.Runs(context, c, l.Line.Id),
                });
            }
            return (c, list);
        });
        _cast = cast;
        foreach (var i in items)
        {
            i.Shown = ShowContext ? i.WithContext : i.LineOnly;
            Lines.Add(i);
        }
        Summary = $"{Lines.Count:N0} {(Lines.Count == 1 ? "line" : "lines")}. Pick “Reassign” on any line that belongs to someone else.";
        IsLoading = false;
    }

    public List<Bilgames.App.Controls.PickerItem> Speakers() => ContextHelpers.SpeakerItems(_cast, SpeakerId);

    public async Task ReassignAsync(SpeakerLineItem item, string speakerId)
    {
        if (_book.BookId is not { } id || speakerId == SpeakerId) return;
        await Task.Run(() => AppServices.Books.AssignSpeaker(id, item.ChapterIndex, item.LineId, speakerId, false));
        Lines.Remove(item);
        var to = _cast.Find(speakerId)?.Name ?? "the narrator";
        Status = $"Moved a line to {to}.";
        Summary = $"{Lines.Count:N0} {(Lines.Count == 1 ? "line" : "lines")}.";
        _book.Changed();
    }

    [RelayCommand] private void Close() => IsOpen = false;
}
