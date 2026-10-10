using System.Collections.ObjectModel;
using Bilgames.App.Services;
using Bilgames.Core;
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
    public string MoreName => $"More options for {Name}";
    public int LineCount { get; private set; }

    public void Update(CastMember m)
    {
        VoiceId = m.VoiceId;
        VoiceName = VoiceCatalog.Name(m.VoiceId);
        LineCount = m.LineCount;
        if (m.IsNarrator)
        {
            LinesLabel = "All non-dialogue text";
        }
        else if (m.LineCount == 0)
        {
            LinesLabel = "No lines yet";
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
        Sheet = cast;
        // Narrator first, then speakers just added (no lines yet) so they are easy to find, then everyone else.
        var shown = cast.Members.Where(m => m.IsNarrator || m.LineCount > 0 || m.UserAdded)
            .OrderByDescending(m => m.IsNarrator).ThenByDescending(m => m.UserAdded && m.LineCount == 0).ThenByDescending(m => m.LineCount).ToList();
        int hidden = cast.Members.Count - shown.Count;
        HiddenLabel = hidden > 0 ? $"{hidden} more {(hidden == 1 ? "name appears" : "names appear")} only as possible speakers in Review." : "";

        // Update in place where possible so voice menus don't flicker.
        if (Members.Select(m => (m.Id, m.Name)).SequenceEqual(shown.Select(m => (m.Id, m.Name))))
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

    /// <summary>The cast as last loaded (for the merge dialog).</summary>
    public CastSheet Sheet { get; private set; } = new();

    [ObservableProperty] private string _castStatus = "";

    /// <summary>Adds a speaker. Returns the new (or existing, same-named) member's id.</summary>
    public async Task<string?> AddSpeakerAsync(string name, Gender gender)
    {
        if (_book.BookId is not { } id) return null;
        CastMember member;
        try { member = await Task.Run(() => AppServices.Books.AddSpeaker(id, name, gender)); }
        catch (Exception ex) { AppServices.Log($"Add speaker failed: {ex}"); CastStatus = $"Couldn't add {name} ({ex.Message}). Please try again."; return null; }
        CastStatus = $"Added {member.Name} with the voice {VoiceCatalog.Name(member.VoiceId)}.";
        _book.Changed();
        return member.Id;
    }

    public async Task MergeAsync(IReadOnlyCollection<string> fromIds, string intoId)
    {
        if (_book.BookId is not { } id) return;
        var names = fromIds.Select(f => Sheet.Find(f)?.Name).Where(n => n is not null).ToList();
        var into = Sheet.Find(intoId)?.Name ?? "";
        try { await Task.Run(() => AppServices.Books.MergeMany(id, fromIds, intoId)); }
        catch (Exception ex) { AppServices.Log($"Merge failed: {ex}"); CastStatus = $"Couldn't merge ({ex.Message}). Please try again."; return; }
        CastStatus = $"Merged {string.Join(", ", names)} into {into}.";
        if (Detail.IsOpen && fromIds.Contains(Detail.SpeakerId)) Detail.CloseCommand.Execute(null);
        _book.Changed();
    }

    /// <summary>Renames a speaker; if the name is someone else's, offers to merge the two.</summary>
    public async Task RenameAsync(CastMemberViewModel member)
    {
        if (_book.BookId is not { } id || member.IsNarrator) return;
        var name = await Dialogs.PromptAsync($"Rename {member.Name}", "Their old name is remembered, so finding speakers again keeps the new one.",
            member.Name, "Name", "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == member.Name) return;
        string? takenBy = null;
        BookService.RenameResult result;
        try { result = await Task.Run(() => AppServices.Books.RenameSpeaker(id, member.Id, name, out takenBy)); }
        catch (Exception ex) { AppServices.Log($"Rename failed: {ex}"); CastStatus = $"Couldn't rename ({ex.Message})."; return; }
        if (result == BookService.RenameResult.NameTaken && takenBy is not null)
        {
            var other = Sheet.Find(takenBy)?.Name ?? name;
            if (await Dialogs.ConfirmAsync($"{other} already exists", $"Is {member.Name} the same character as {other}? Merging moves all of {member.Name}'s lines to {other}.", $"Merge into {other}"))
                await MergeAsync(new[] { member.Id }, takenBy);
            return;
        }
        if (result != BookService.RenameResult.Renamed) return;
        CastStatus = $"Renamed {member.Name} to {name.Trim()}.";
        if (Detail.IsOpen && Detail.SpeakerId == member.Id) Detail.Name = name.Trim();
        _book.Changed();
    }

    /// <summary>Removes a speaker; their lines go back to the narrator.</summary>
    public async Task RemoveAsync(CastMemberViewModel member)
    {
        if (_book.BookId is not { } id || member.IsNarrator) return;
        if (member.LineCount > 0)
        {
            var go = await Dialogs.ConfirmAsync($"Remove {member.Name}?",
                $"Their {member.LineCount:N0} {(member.LineCount == 1 ? "line goes" : "lines go")} back to the narrator. To give the lines to someone else instead, use Merge.",
                "Remove", destructive: true);
            if (!go) return;
        }
        await Task.Run(() => AppServices.Books.Merge(id, member.Id, CastMember.NarratorId));
        CastStatus = $"Removed {member.Name}.";
        if (Detail.IsOpen && Detail.SpeakerId == member.Id) Detail.CloseCommand.Execute(null);
        _book.Changed();
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

    /// <summary>
    /// For the voice picker's ▶: a function that reads this character's own line in any voice, so
    /// voices can be auditioned as they'd actually sound for them. The line is looked up once.
    /// </summary>
    public Func<Bilgames.App.Controls.PickerItem, Task> VoiceAuditioner(CastMemberViewModel member)
    {
        Task<string>? line = null;
        return async voice =>
        {
            if (_book.BookId is not { } id) return;
            if (!await EnsureModel()) return;
            line ??= Task.Run(() => SampleLine(id, member));
            await _book.Shell.Player.PreviewAsync(await line, voice.Id);
        };
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
    public IReadOnlyList<IReadOnlyList<Bilgames.App.Controls.ContextRun>> WithContext { get; set; } = Array.Empty<IReadOnlyList<Bilgames.App.Controls.ContextRun>>();

    [ObservableProperty] private IReadOnlyList<IReadOnlyList<Bilgames.App.Controls.ContextRun>>? _shown;

    public string ReassignName => $"Reassign this line: {Text}";

    // ---------- expanding the context of this one line ----------

    internal ChapterAnalysis? Analysis { get; init; }
    internal CastSheet Cast { get; init; } = new();
    private int _contextLevel;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMore), nameof(ShowLess))] private bool _canMoreContext;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLess))] private bool _canLessContext;
    /// <summary>The list's "Context" switch is on (expansion only applies then).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMore), nameof(ShowLess))] private bool _showingContext = true;

    public bool ShowMore => ShowingContext && CanMoreContext;
    public bool ShowLess => ShowingContext && CanLessContext;

    [RelayCommand] private void MoreContext() => SetContextLevel(_contextLevel + 1);
    [RelayCommand] private void LessContext() => SetContextLevel(_contextLevel - 1);

    private void SetContextLevel(int level)
    {
        if (Analysis is null) return;
        _contextLevel = Math.Clamp(level, 0, ContextHelpers.MaxContextLevel);
        WithContext = ContextHelpers.Runs(ContextHelpers.Around(Analysis, LineId, _contextLevel, wide: false), Cast, LineId);
        CanMoreContext = ContextHelpers.HasMore(Analysis, LineId, _contextLevel, wide: false);
        CanLessContext = _contextLevel > 0;
        if (ShowingContext) Shown = WithContext;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToneLabel), nameof(ToneHint), nameof(ToneButtonName))] private string? _cue;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToneHint))] private CueSource _cueFrom;
    public string ToneLabel => ToneText.Label(Cue);
    public string ToneHint => $"Tone: {ToneLabel} ({ToneText.Source(CueFrom, Cue).ToLowerInvariant()}). Change it";
    public string ToneButtonName => $"Tone: {ToneLabel}. Change how this line is delivered";
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
        foreach (var l in Lines) { l.Shown = value ? l.WithContext : l.LineOnly; l.ShowingContext = value; }
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
                var context = a is null ? new List<List<Line>>() : ContextHelpers.Around(a, l.Line.Id, 0, wide: false);
                list.Add(new SpeakerLineItem
                {
                    ChapterIndex = l.ChapterIndex,
                    ChapterTitle = l.ChapterTitle,
                    LineId = l.Line.Id,
                    Text = l.Line.Text,
                    LineOnly = ContextHelpers.Runs(new[] { new[] { l.Line } }, c, null),
                    WithContext = ContextHelpers.Runs(context, c, l.Line.Id),
                    Analysis = a,
                    Cast = c,
                    CanMoreContext = a is not null && ContextHelpers.HasMore(a, l.Line.Id, 0, wide: false),
                    Cue = Bilgames.Core.Analysis.Tones.Normalize(l.Line.Cue),
                    CueFrom = l.Line.CueFrom,
                });
            }
            return (c, list);
        });
        _cast = cast;
        foreach (var i in items)
        {
            i.Shown = ShowContext ? i.WithContext : i.LineOnly;
            i.ShowingContext = ShowContext;
            Lines.Add(i);
        }
        Summary = $"{Lines.Count:N0} {(Lines.Count == 1 ? "line" : "lines")}. Pick “Reassign” on any line that belongs to someone else.";
        IsLoading = false;
    }

    public List<Bilgames.App.Controls.PickerItem> Speakers() => ContextHelpers.SpeakerItems(_cast, SpeakerId);

    public async Task SetToneAsync(SpeakerLineItem item, string? cue)
    {
        if (_book.BookId is not { } id) return;
        cue = string.IsNullOrEmpty(cue) ? null : cue;
        await Task.Run(() => AppServices.Books.SetCue(id, item.ChapterIndex, item.LineId, cue));
        item.Cue = cue;
        item.CueFrom = CueSource.User;
        Status = $"Tone set to {item.ToneLabel.ToLowerInvariant()}.";
        _book.Changed();
    }

    /// <summary>Reassign → a name that isn't in the cast yet: add them, then give them the line.</summary>
    public async Task ReassignToNewAsync(SpeakerLineItem item, string name)
    {
        if (_book.BookId is not { } id) return;
        var member = await Task.Run(() => AppServices.Books.AddSpeaker(id, name));
        _cast = AppServices.Store.LoadCast(id);
        await ReassignAsync(item, member.Id);
    }

    public async Task ReassignAsync(SpeakerLineItem item, string speakerId)
    {
        if (_book.BookId is not { } id || speakerId == SpeakerId) return;
        try { await Task.Run(() => AppServices.Books.AssignSpeaker(id, item.ChapterIndex, item.LineId, speakerId, false)); }
        catch (Exception ex) { AppServices.Log($"Reassign failed: {ex}"); Status = $"Couldn't save that ({ex.Message}). Please try again."; return; }
        Lines.Remove(item);
        var to = _cast.Find(speakerId)?.Name ?? "the narrator";
        Status = $"Moved a line to {to}.";
        Summary = $"{Lines.Count:N0} {(Lines.Count == 1 ? "line" : "lines")}.";
        _book.Changed();
    }

    [RelayCommand] private void Close() => IsOpen = false;
}
