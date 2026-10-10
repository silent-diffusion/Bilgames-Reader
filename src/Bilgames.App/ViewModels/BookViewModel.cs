using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public sealed partial class BookViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public BookViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Structure = new StructureViewModel(this);
        Cast = new CastViewModel(this);
        Review = new ReviewViewModel(this);
        Generate = new GenerateViewModel(this);
        // The rest of a book finished analysing in the background: show its full cast and review queue.
        AppServices.AnalysisFinished += id => _shell.BookChanged(id);
    }

    public ShellViewModel Shell => _shell;
    public StructureViewModel Structure { get; }
    public CastViewModel Cast { get; }
    public ReviewViewModel Review { get; }
    public GenerateViewModel Generate { get; }

    public BookRecord? Record { get; private set; }
    public string? BookId => Record?.Id;

    /// <summary>Saves this book as a .bilgames file to open on another PC or device.</summary>
    [RelayCommand]
    private async Task ExportBookFile()
    {
        if (BookId is { } id) await LibraryTransfer.ExportBookAsync(id);
    }

    [ObservableProperty] private bool _hasBook;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _coverColor = "#2B4A5E";
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private BookTab _tab = BookTab.Structure;

    public bool IsStructure => Tab == BookTab.Structure;
    public bool IsCast => Tab == BookTab.Cast;
    public bool IsReview => Tab == BookTab.Review;
    public bool IsGenerate => Tab == BookTab.Generate;

    partial void OnTabChanged(BookTab value)
    {
        OnPropertyChanged(nameof(IsStructure));
        OnPropertyChanged(nameof(IsCast));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsGenerate));
        if (value == BookTab.Review) Review.EnsureLoaded();
        if (value == BookTab.Generate) Generate.Refresh();
        _shell.RaiseLayout();
    }

    [RelayCommand] private void ShowStructure() => Tab = BookTab.Structure;
    [RelayCommand] private void ShowCast() => Tab = BookTab.Cast;
    [RelayCommand] private void ShowReview() => Tab = BookTab.Review;
    [RelayCommand] private void ShowGenerate() => Tab = BookTab.Generate;

    public void Load(string id)
    {
        Record = AppServices.Store.LoadBook(id);
        HasBook = Record is not null;
        if (Record is null) return;
        Review.Reset();
        Reload();
        // A freshly imported book starts at Structure; an analyzed one at Cast.
        if (Record.Analysis != AnalysisState.Done && Tab is BookTab.Cast or BookTab.Review) Tab = BookTab.Structure;
    }

    public void Reload()
    {
        if (BookId is null) return;
        Record = AppServices.Store.LoadBook(BookId);
        if (Record is null) { HasBook = false; return; }
        Title = Record.Title;
        Author = Record.Author.Length > 0 ? Record.Author : "Unknown author";
        CoverColor = Record.CoverColor;
        CoverPath = AppServices.Store.CoverPath(Record);
        int chapters = Record.IncludedChapters.Count(c => c.Kind == SectionKind.Body);
        int voices = Math.Max(1, Record.VoiceCount);
        Subtitle = $"{Author} · {chapters} {(chapters == 1 ? "chapter" : "chapters")} · {voices} {(voices == 1 ? "voice" : "voices")}";
        Structure.Refresh();
        Cast.Refresh();
        Generate.Refresh();
    }

    public void Changed()
    {
        if (BookId is null) return;
        _shell.BookChanged(BookId);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Record is null) return;
        int choice = await Dialogs.ChooseAsync(
            $"Delete “{Record.Title}”?",
            "Delete audio only: removes generated audio and caches but keeps the book, its speakers and your voice choices.\n\n" +
            "Delete everything: removes the book, its analysis and all audio from this device.",
            "Delete audio only", "Delete everything");
        if (choice == 0) return;
        var id = Record.Id;
        AppServices.Jobs.Pause(id);
        if (_shell.Player.BookId == id) _shell.Player.Unload();
        if (choice == 1)
        {
            AppServices.Store.Delete(id, DeleteMode.AudioOnly);
            var b = AppServices.Store.LoadBook(id);
            if (b is not null) { b.Generation = new GenerationState(); b.Position = new PlaybackPosition(); AppServices.Store.SaveBook(b); }
            Changed();
        }
        else
        {
            AppServices.Store.Delete(id, DeleteMode.Everything);
            Record = null;
            HasBook = false;
            _shell.Library.Refresh(id);
            _shell.GoLibrary();
        }
    }

    [RelayCommand]
    private void RevealFolder()
    {
        if (BookId is null) return;
        System.Diagnostics.Process.Start("explorer.exe", AppServices.Store.BookDir(BookId));
    }
}

/// <summary>One checklist row on the Structure tab.</summary>
public sealed partial class SectionRow : ObservableObject
{
    private readonly Action<bool> _apply;

    public SectionRow(string name, string detail, bool on, Action<bool> apply)
    {
        Name = name;
        Detail = detail;
        _isOn = on;
        _apply = apply;
    }

    public string Name { get; }
    public string Detail { get; }
    public string AccessibleName => "Include " + Name;

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _apply(value);
}

public sealed partial class StructureViewModel : ObservableObject
{
    private readonly BookViewModel _book;
    private bool _refreshing;

    public StructureViewModel(BookViewModel book) => _book = book;

    public ObservableCollection<SectionRow> Rows { get; } = new();

    [ObservableProperty] private string _estAudio = "";
    [ObservableProperty] private string _storage = "";
    [ObservableProperty] private string _wordsLabel = "";
    [ObservableProperty] private FootnoteMode _footnotes;
    [ObservableProperty] private bool _hasFootnotes;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private double _analyzeProgress;
    [ObservableProperty] private string _analyzeLabel = "Find speakers";
    [ObservableProperty] private string _analyzeHint = "";

    public bool FootInline => Footnotes == FootnoteMode.Inline;
    public bool FootEnd => Footnotes == FootnoteMode.EndOfChapter;
    public bool FootSkip => Footnotes == FootnoteMode.Skip;

    partial void OnFootnotesChanged(FootnoteMode value)
    {
        OnPropertyChanged(nameof(FootInline));
        OnPropertyChanged(nameof(FootEnd));
        OnPropertyChanged(nameof(FootSkip));
        if (!_refreshing) Save(o => o.Footnotes = value);
    }

    [RelayCommand] private void SetFootInline() => Footnotes = FootnoteMode.Inline;
    [RelayCommand] private void SetFootEnd() => Footnotes = FootnoteMode.EndOfChapter;
    [RelayCommand] private void SetFootSkip() => Footnotes = FootnoteMode.Skip;

    public void Refresh()
    {
        var b = _book.Record;
        if (b is null) return;
        _refreshing = true;
        Rows.Clear();
        var opts = b.Options;

        string Titles(IEnumerable<ChapterInfo> cs)
        {
            var t = string.Join(", ", cs.Select(c => c.Title).Distinct().Take(3));
            return t.Length > 48 ? t[..47] + "…" : t;
        }

        var front = b.Chapters.Where(c => c.Kind == SectionKind.FrontMatter).ToList();
        var preface = b.Chapters.Where(c => c.Kind == SectionKind.Preface).ToList();
        var body = b.Chapters.Where(c => c.Kind == SectionKind.Body).ToList();
        var back = b.Chapters.Where(c => c.Kind == SectionKind.BackMatter).ToList();

        if (front.Count > 0)
            Rows.Add(new SectionRow("Title & front pages", $"Front matter · {front.Count} {(front.Count == 1 ? "page" : "pages")} · {front.Sum(c => c.WordCount):N0} words",
                opts.IncludeFrontMatter, v => Save(o => o.IncludeFrontMatter = v)));
        if (preface.Count > 0)
            Rows.Add(new SectionRow(Titles(preface), $"Front matter · {preface.Sum(c => c.WordCount):N0} words",
                opts.IncludePreface, v => Save(o => o.IncludePreface = v)));
        if (body.Count > 0)
            Rows.Add(new SectionRow(body.Count == 1 ? body[0].Title : $"Chapters 1–{body.Count}", "Main text · reading order from the EPUB spine",
                opts.IncludeBody, v => Save(o => o.IncludeBody = v)));
        var firstTitle = body.FirstOrDefault()?.Title ?? "Chapter 1";
        Rows.Add(new SectionRow("Chapter headings", $"Read “{firstTitle}” before each chapter",
            opts.ReadChapterHeadings, v => Save(o => o.ReadChapterHeadings = v)));
        if (back.Count > 0)
            Rows.Add(new SectionRow(Titles(back), $"Back matter · {back.Count} {(back.Count == 1 ? "section" : "sections")} · {back.Sum(c => c.WordCount):N0} words",
                opts.IncludeBackMatter, v => Save(o => o.IncludeBackMatter = v)));

        HasFootnotes = b.Chapters.Any(c => c.NoteCount > 0);
        Footnotes = opts.Footnotes;
        UpdateStats();
        AnalyzeLabel = b.Analysis == AnalysisState.Done ? "Find speakers again" : "Find speakers";
        AnalyzeHint = b.AnalyzingRest ? "First chapter done; finding speakers in the rest of the book…" : "Usually under a minute.";
        _refreshing = false;
    }

    private void UpdateStats()
    {
        var b = _book.Record!;
        int words = b.IncludedChapters.Sum(c => c.WordCount);
        var est = BookRecord.EstimateDuration(words);
        EstAudio = Ui.Duration(est);
        // AAC chapters (~8 KB/s) plus the segment cache used for quick re-generation (~48 KB/s).
        double perSecond = 8_000 + (AppServices.Settings.KeepSegmentCache ? 48_000 : 0);
        Storage = Ui.Size((long)(est.TotalSeconds * perSecond));
        WordsLabel = $"{words:N0}";
    }

    private void Save(Action<StructureOptions> change)
    {
        if (_refreshing || _book.Record is null) return;
        var b = AppServices.Store.LoadBook(_book.Record.Id);
        if (b is null) return;
        change(b.Options);
        AppServices.Store.SaveBook(b);
        _book.Changed();
        UpdateStats();
    }

    [RelayCommand]
    private async Task FindSpeakersAsync()
    {
        if (_book.BookId is not { } id || IsAnalyzing) return;
        IsAnalyzing = true;
        AnalyzeProgress = 0;
        AnalyzeLabel = "Finding speakers…";
        try
        {
            var progress = new Progress<double>(p => AnalyzeProgress = p * 100);
            // First time: the first chapter now, so listening can start; the rest follows in the background.
            bool first = _book.Record?.Analysis != AnalysisState.Done;
            var book = await Task.Run(() => AppServices.Books.Analyze(id, progress, firstChapterOnly: first));
            _book.Changed();
            _book.Tab = BookTab.Cast;
            if (book.AnalyzingRest)
            {
                AnalyzeHint = "First chapter done; finding speakers in the rest of the book…";
                _ = AppServices.AnalyzeRestAsync(id);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("Analysis failed: " + ex);
            await Dialogs.InfoAsync("Couldn't analyze this book", ex.Message);
        }
        finally
        {
            IsAnalyzing = false;
            AnalyzeLabel = _book.Record?.Analysis == AnalysisState.Done ? "Find speakers again" : "Find speakers";
        }
    }

    public IReadOnlyList<ChapterInfo> PreviewChapters => _book.Record?.Chapters ?? (IReadOnlyList<ChapterInfo>)Array.Empty<ChapterInfo>();

    public ChapterText? LoadText(int index) => _book.BookId is { } id ? AppServices.Store.LoadChapterText(id, index) : null;
}
