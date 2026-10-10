using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Jobs;
using Bilgames.Core.Model;
using Bilgames.Tts.Kokoro;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public sealed class ChapterGroupViewModel
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string DotBg { get; init; } = "#232934";
    public string DotFg { get; init; } = "#9AA3AF";
    public string Mark { get; init; } = "";
    public string Foreground { get; init; } = "#ECEEF1";
}

public sealed partial class ProfileOptionViewModel : ObservableObject
{
    public QualityProfile Profile { get; init; }
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class GenerateViewModel : ObservableObject
{
    private readonly BookViewModel _book;
    private GenerationProgress? _last;

    public GenerateViewModel(BookViewModel book)
    {
        _book = book;
        Profiles.Add(new ProfileOptionViewModel { Profile = QualityProfile.Fast, Name = "Fast", Hint = "Plainer voice" });
        Profiles.Add(new ProfileOptionViewModel { Profile = QualityProfile.Balanced, Name = "Balanced", Hint = "Recommended" });
        Profiles.Add(new ProfileOptionViewModel { Profile = QualityProfile.High, Name = "High quality", Hint = "Slower" });
        AppServices.Models.Changed += () => AppServices.RunOnUI(UpdateModelCard);
    }

    public ObservableCollection<ProfileOptionViewModel> Profiles { get; } = new();
    public ObservableCollection<ChapterGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private string _percentText = "0%";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _timeLeft = "";
    [ObservableProperty] private string _chapterLabel = "";
    [ObservableProperty] private string _segmentsLabel = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _showWarning;
    [ObservableProperty] private string _warningTitle = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private string _storageLabel = "";
    [ObservableProperty] private string _primaryLabel = "Generate audio";
    [ObservableProperty] private bool _canListen;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _needsModel;
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadLabel = "";

    public void Refresh()
    {
        var b = _book.Record;
        if (b is null) return;
        foreach (var p in Profiles) p.IsSelected = p.Profile == b.Quality;
        UpdateModelCard();
        var record = b;
        Task.Run(() => AppServices.Jobs.Snapshot(record)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) AppServices.RunOnUI(() => { if (_book.BookId == record.Id) Apply(t.Result); });
        });
    }

    public void Apply(GenerationProgress p)
    {
        _last = p;
        var b = _book.Record;
        if (b is null) return;
        Percent = Math.Round(p.Fraction * 100);
        PercentText = $"{Percent:0}%";
        IsRunning = p.State is JobState.Running or JobState.Queued;
        IsComplete = p.Chapters.Count > 0 && p.Chapters.All(c => c.Status == ChapterStatus.Ready);
        TimeLeft = IsComplete ? "Complete" : p.Remaining is { } r && r > TimeSpan.Zero ? $"about {Ui.Duration(r)} left" : "";
        int current = p.Current is { } cur ? p.Chapters.IndexOf(cur) + 1 : p.ReadyChapters;
        ChapterLabel = p.Chapters.Count == 0 ? "No chapters selected" : $"Chapter {Math.Max(1, Math.Min(current, p.Chapters.Count))} of {p.Chapters.Count}";
        SegmentsLabel = $"{p.DoneSegments:N0} of {p.TotalSegments:N0} segments";
        CanListen = p.ReadyChapters > 0;
        Error = p.Error;

        bool changed = p.Chapters.Any(c => c.Status == ChapterStatus.Changed);
        PrimaryLabel = IsRunning ? "Pause" : IsComplete ? "Generated" : changed && p.Chapters.All(c => c.Status is ChapterStatus.Ready or ChapterStatus.Changed)
            ? "Update changed chapters" : p.DoneSegments > 0 ? "Resume" : "Generate audio";

        ShowWarning = IsRunning && p.Throttled || IsRunning && (AppServices.Power.OnBattery || AppServices.Power.EnergySaver);
        WarningTitle = AppServices.Power.EnergySaver ? "Energy saver is on." : "On battery.";
        WarningText = "Slowed down to keep the device cool and save power. Plug in for faster progress.";

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(AppServices.Store.Root)!).AvailableFreeSpace;
            StorageLabel = $"Uses {Ui.Size(p.AudioBytes)} · {Ui.Size(free)} free";
        }
        catch { StorageLabel = $"Uses {Ui.Size(p.AudioBytes)}"; }

        BuildGroups(p);
    }

    private void BuildGroups(GenerationProgress p)
    {
        Groups.Clear();
        var accent = "#F0A848";
        int i = 0;
        bool firstQueuedShown = false;
        while (i < p.Chapters.Count)
        {
            var c = p.Chapters[i];
            int j = i;
            if (c.Status is ChapterStatus.Ready or ChapterStatus.Queued or ChapterStatus.Changed)
            {
                // The first queued chapter after the current one gets its own row, as in the design.
                bool single = c.Status == ChapterStatus.Queued && !firstQueuedShown && p.Current is not null;
                if (c.Status == ChapterStatus.Queued) firstQueuedShown = true;
                if (!single)
                    while (j + 1 < p.Chapters.Count && p.Chapters[j + 1].Status == c.Status) j++;
            }
            string name = i == j ? p.Chapters[i].Title : $"Chapters {i + 1}–{j + 1}";
            Groups.Add(c.Status switch
            {
                ChapterStatus.Ready => new ChapterGroupViewModel { Name = name, State = "Ready to play", Mark = "✓", DotBg = "#1E3324", DotFg = "#9BD39A" },
                ChapterStatus.Generating => new ChapterGroupViewModel { Name = name, State = $"Generating · {Math.Round(c.Fraction * 100)}%", Mark = "•", DotBg = accent, DotFg = "#1A1205" },
                ChapterStatus.Changed => new ChapterGroupViewModel { Name = name, State = "Speakers changed · will update", Mark = "↻", DotBg = "#3A2A12", DotFg = "#F0C27A" },
                ChapterStatus.Failed => new ChapterGroupViewModel { Name = name, State = "Failed · will retry", Mark = "!", DotBg = "#3A2A12", DotFg = "#F0C27A" },
                _ => new ChapterGroupViewModel { Name = name, State = "Queued", Mark = "", DotBg = "#232934", DotFg = "#9AA3AF", Foreground = "#9AA3AF" },
            });
            i = j + 1;
        }
    }

    private void UpdateModelCard()
    {
        var b = _book.Record;
        if (b is null) return;
        var model = KokoroModels.For(b.Quality);
        NeedsModel = !AppServices.Models.IsInstalled(model);
        ModelText = $"{model.Label} · {Ui.Size(model.Size)}. Downloads once from GitHub, then everything runs offline.";
        IsDownloading = AppServices.Models.Downloading is not null;
        DownloadPercent = Math.Round(AppServices.Models.Progress * 100);
        DownloadLabel = IsDownloading ? $"Downloading… {DownloadPercent:0}%" : AppServices.Models.Error ?? "";
    }

    [RelayCommand]
    private void PickProfile(ProfileOptionViewModel? option)
    {
        if (option is null || _book.Record is null) return;
        var b = AppServices.Store.LoadBook(_book.Record.Id);
        if (b is null) return;
        bool wasRunning = b.Generation.State is JobState.Running or JobState.Queued;
        if (wasRunning) AppServices.Jobs.Pause(b.Id);
        b = AppServices.Store.LoadBook(b.Id)!;
        b.Quality = option.Profile;
        AppServices.Store.SaveBook(b);
        _book.Changed();
        if (wasRunning && AppServices.Models.IsInstalled(option.Profile)) AppServices.Jobs.Start(b.Id);
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        var b = _book.Record;
        if (b is null) return;
        await AppServices.Models.RequestDownloadAsync(KokoroModels.For(b.Quality));
        Refresh();
    }

    [RelayCommand] private void CancelDownload() => AppServices.Models.Cancel();

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        var b = _book.Record;
        if (b is null) return;
        if (IsRunning)
        {
            AppServices.Jobs.Pause(b.Id);
            return;
        }
        if (IsComplete) return;
        if (!AppServices.Models.IsInstalled(b.Quality))
        {
            if (!await AppServices.Models.RequestDownloadAsync(KokoroModels.For(b.Quality))) return;
        }
        if (b.Analysis != AnalysisState.Done)
        {
            var go = await Dialogs.ConfirmAsync("Generate without finding speakers?",
                "Speakers haven't been found for this book, so the narrator will read everything, including dialogue. You can find speakers later and only the dialogue will be regenerated.",
                "Generate with narrator only");
            if (!go) return;
        }
        AppServices.Jobs.Start(b.Id);
    }

    [RelayCommand]
    private void Listen()
    {
        if (_book.BookId is { } id) _book.Shell.OpenPlayer(id);
    }
}
