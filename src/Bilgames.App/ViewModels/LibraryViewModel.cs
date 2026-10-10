using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Epub;
using Bilgames.Core.Jobs;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

/// <summary>One book as shown in the library list/grid.</summary>
public sealed partial class BookItemViewModel : ObservableObject
{
    public BookItemViewModel(BookRecord book) => Update(book);

    public BookRecord Record { get; private set; } = null!;
    public string Id => Record.Id;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _coverColor = "#2B4A5E";
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _badgeBg = "#232934";
    [ObservableProperty] private string _badgeFg = "#C9CED6";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _generationFraction;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _generationText = "";
    [ObservableProperty] private double _audioSeconds;
    [ObservableProperty] private int _readyChapters;
    [ObservableProperty] private int _totalChapters;

    public string RowName => $"{Title} by {Author}, {Status}";

    /// <summary>Medium layout highlights the book shown in the right pane.</summary>
    public string RowBackground => IsSelected ? "#1F242D" : "#00000000";

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(RowBackground));

    public void Update(BookRecord book, GenerationProgress? progress = null)
    {
        Record = book;
        Title = book.Title;
        Author = book.Author.Length > 0 ? book.Author : "Unknown author";
        CoverColor = book.CoverColor;
        CoverPath = AppServices.Store.CoverPath(book);

        var included = book.IncludedChapters.ToList();
        TotalChapters = included.Count;
        double seconds = 0;
        int ready = 0;
        foreach (var c in included)
        {
            var a = AppServices.Store.LoadChapterAudio(book.Id, c.Index);
            if (a is null) continue;
            seconds += a.DurationSeconds;
            ready++;
        }
        AudioSeconds = seconds;
        ReadyChapters = progress?.ReadyChapters ?? ready;

        var state = progress?.State ?? book.Generation.State;
        IsGenerating = state is JobState.Running or JobState.Queued;
        if (progress is not null) GenerationFraction = progress.Fraction;
        else if (TotalChapters > 0) GenerationFraction = (double)ReadyChapters / TotalChapters;
        var current = progress?.Current;
        GenerationText = current is not null
            ? $"Generating chapter {included.FindIndex(c => c.Index == current.Index) + 1} of {TotalChapters}"
            : IsGenerating ? "Waiting to generate" : ReadyChapters > 0 ? $"{ReadyChapters} of {TotalChapters} chapters ready" : "";

        string est = Ui.Duration(BookRecord.EstimateDuration(included.Sum(c => c.WordCount)));
        if (IsGenerating)
        {
            (Status, BadgeBg, BadgeFg) = ("Generating", "#3A2A12", "#F0C27A");
            Detail = $"{Math.Round(GenerationFraction * 100)}% generated";
        }
        else if (state == JobState.Failed)
        {
            (Status, BadgeBg, BadgeFg) = ("Needs attention", "#3A2A12", "#F0C27A");
            Detail = "Generation stopped";
        }
        else if (TotalChapters > 0 && ReadyChapters == TotalChapters)
        {
            (Status, BadgeBg, BadgeFg) = ("Ready", "#1E3324", "#9BD39A");
            Detail = Ui.Duration(TimeSpan.FromSeconds(seconds));
        }
        else if (state == JobState.Paused)
        {
            (Status, BadgeBg, BadgeFg) = ("Paused", "#232934", "#C9CED6");
            Detail = $"{ReadyChapters} of {TotalChapters} chapters ready";
        }
        else if (book.Analysis == AnalysisState.Done && book.UnsureCount > 0)
        {
            (Status, BadgeBg, BadgeFg) = ("Needs review", "#3A2A12", "#F0C27A");
            Detail = $"{book.UnsureCount:N0} {(book.UnsureCount == 1 ? "line" : "lines")} to check";
        }
        else if (book.Analysis == AnalysisState.Done)
        {
            (Status, BadgeBg, BadgeFg) = ("Analyzed", "#232934", "#C9CED6");
            Detail = $"About {est}";
        }
        else
        {
            (Status, BadgeBg, BadgeFg) = ("Imported", "#232934", "#C9CED6");
            Detail = "Not analyzed yet";
        }
        OnPropertyChanged(nameof(RowName));
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (Title.Contains(query, StringComparison.OrdinalIgnoreCase) || Author.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        // Characters too, as the search placeholder promises.
        var cast = AppServices.Store.LoadCast(Id);
        return cast.Members.Any(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public LibraryViewModel(ShellViewModel shell) => _shell = shell;

    public ObservableCollection<BookItemViewModel> Books { get; } = new();
    public ObservableCollection<BookItemViewModel> Visible { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _countLabel = "";
    [ObservableProperty] private BookItemViewModel? _continueItem;
    [ObservableProperty] private string _continueDetail = "";
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private string _storageLabel = "";
    [ObservableProperty] private double _storageFraction;
    [ObservableProperty] private bool _isEmpty = true;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void Load()
    {
        Books.Clear();
        foreach (var b in AppServices.Store.LoadAll()) Books.Add(new BookItemViewModel(b));
        ApplyFilter();
        RefreshSummary();
    }

    public BookItemViewModel? Find(string id) => Books.FirstOrDefault(b => b.Id == id);

    public void Refresh(string id, GenerationProgress? progress = null)
    {
        var record = AppServices.Store.LoadBook(id);
        var item = Find(id);
        if (record is null)
        {
            if (item is not null) { Books.Remove(item); Visible.Remove(item); }
        }
        else if (item is null) Books.Insert(0, new BookItemViewModel(record));
        else item.Update(record, progress);
        ApplyFilter();
        // Progress arrives after every couple of sentences: keep this cheap (storage is re-measured at most every 15 s).
        RefreshSummary(forceStorage: progress is null);
    }

    private void ApplyFilter()
    {
        var wanted = Books.Where(b => b.Matches(SearchText)).ToList();
        for (int i = Visible.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Visible[i])) Visible.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Visible.Count && Visible[i] == wanted[i]) continue;
            if (Visible.Contains(wanted[i])) Visible.Remove(wanted[i]);
            Visible.Insert(i, wanted[i]);
        }
    }

    // Chapter duration for the "Continue listening" card, so it isn't re-read on every progress update.
    private (string BookId, int Chapter, double? Duration)? _continueAudio;

    public void RefreshSummary(bool forceStorage = true)
    {
        IsEmpty = Books.Count == 0;
        CountLabel = $"{Books.Count} {(Books.Count == 1 ? "book" : "books")}";

        // Continue listening: most recently played book with audio, else whatever is generating.
        var cont = Books.Where(b => b.Record.LastPlayedUtc is not null && b.ReadyChapters > 0).OrderByDescending(b => b.Record.LastPlayedUtc).FirstOrDefault()
                   ?? Books.FirstOrDefault(b => b.IsGenerating)
                   ?? Books.FirstOrDefault(b => b.ReadyChapters > 0);
        ContinueItem = cont;
        if (cont is not null)
        {
            var pos = cont.Record.Position;
            var chapter = pos.ChapterIndex >= 0 && pos.ChapterIndex < cont.Record.Chapters.Count ? cont.Record.Chapters[pos.ChapterIndex] : null;
            double? duration = null;
            if (chapter is not null)
            {
                if (forceStorage || _continueAudio is not { } c || c.BookId != cont.Id || c.Chapter != chapter.Index || c.Duration is null)
                    _continueAudio = (cont.Id, chapter.Index, AppServices.Store.LoadChapterAudio(cont.Id, chapter.Index)?.DurationSeconds);
                duration = _continueAudio.Value.Duration;
            }
            ContinueDetail = chapter is null || duration is null
                ? (cont.ReadyChapters > 0 ? "Ready to start" : "Audio is on its way")
                : $"{chapter.Title} · {Ui.Duration(TimeSpan.FromSeconds(Math.Max(0, duration.Value - pos.Seconds)))} left";
        }

        RefreshStorage(forceStorage);
    }

    private int _measuring;
    private volatile bool _measureAgain;
    private DateTime _measuredAt = DateTime.MinValue;
    private static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Measures models and audio on a background thread: walking thousands of cached segment files
    /// on the UI thread froze the window during generation. While generating, at most every 15 s.
    /// </summary>
    private void RefreshStorage(bool force)
    {
        if (!force && DateTime.UtcNow - _measuredAt < StorageInterval) return;
        if (Interlocked.Exchange(ref _measuring, 1) == 1) { if (force) _measureAgain = true; return; }
        _measuredAt = DateTime.UtcNow;
        var root = AppServices.Store.Root;
        Task.Run(() =>
        {
            long models = AppServices.Store.ModelsSize();
            long audioBytes = AppServices.Store.AudioSize();
            double fraction;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(root)!);
                fraction = Math.Clamp((models + audioBytes) / (double)Math.Max(1, models + audioBytes + drive.AvailableFreeSpace), 0.02, 1);
            }
            catch { fraction = 0.02; }
            return (models, audioBytes, fraction);
        }).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _measuring, 0);
            if (_measureAgain) { _measureAgain = false; AppServices.RunOnUI(() => RefreshStorage(true)); }
            if (!t.IsCompletedSuccessfully) return;
            var (models, audioBytes, fraction) = t.Result;
            AppServices.RunOnUI(() =>
            {
                StorageLabel = $"Models {Ui.Size(models)} · Audio {Ui.Size(audioBytes)}";
                StorageFraction = fraction;
            });
        });
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await Pickers.PickEpubAsync();
        if (path is null) return;
        await ImportPathAsync(path);
    }

    public async Task ImportPathAsync(string path)
    {
        IsImporting = true;
        ImportStatus = "Reading " + Path.GetFileName(path) + "…";
        try
        {
            var voice = AppServices.Settings.DefaultNarratorVoice;
            var book = await Task.Run(() => AppServices.Store.Import(path, voice));
            Books.Insert(0, new BookItemViewModel(book));
            ApplyFilter();
            RefreshSummary();
            _shell.OpenBook(book.Id, BookTab.Structure);
        }
        catch (EpubFormatException ex)
        {
            await Dialogs.InfoAsync("Couldn't import this book", ex.Message);
        }
        catch (Exception ex)
        {
            AppServices.Log("Import failed: " + ex);
            await Dialogs.InfoAsync("Couldn't import this book", "Something went wrong while reading the file: " + ex.Message);
        }
        finally
        {
            IsImporting = false;
            ImportStatus = "";
        }
    }
}
