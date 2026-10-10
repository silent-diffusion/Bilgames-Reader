using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Tts;
using Bilgames.Tts.Kokoro;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

/// <summary>App-side wrapper so XAML templates never type against Core classes.</summary>
public sealed class VoiceOptionViewModel
{
    public VoiceOptionViewModel(VoiceInfo v) { Id = v.Id; Name = v.DisplayName; Description = v.Description; }
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Label => $"{Name} · {Description}";
}

public sealed partial class ModelRowViewModel : ObservableObject
{
    public ModelRowViewModel(ModelInfo model) { Model = model; Refresh(); }

    public ModelInfo Model { get; }
    public string Label => Model.Label;
    public string Use => Model == KokoroModels.Full ? "Used by High quality" : "Used by Fast and Balanced, and voice previews";

    [ObservableProperty] private bool _installed;
    [ObservableProperty] private string _detail = "";

    public void Refresh()
    {
        Installed = AppServices.Models.IsInstalled(Model);
        Detail = Installed ? $"Installed · {Ui.Size(Model.Size)}" : $"Not installed · {Ui.Size(Model.Size)} download";
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _loading;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        foreach (var m in KokoroModels.All) Models.Add(new ModelRowViewModel(m));
        foreach (var v in VoiceCatalog.All) Voices.Add(new VoiceOptionViewModel(v));
        AppServices.Models.Changed += () => AppServices.RunOnUI(Refresh);
        Refresh();
    }

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();
    public ObservableCollection<VoiceOptionViewModel> Voices { get; } = new();

    [ObservableProperty] private bool _autoCheck;
    [ObservableProperty] private bool _keepSegmentCache;
    [ObservableProperty] private VoiceOptionViewModel? _defaultNarrator;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private bool _canUpdate;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _dataFolder = "";
    [ObservableProperty] private string _storageSummary = "";
    [ObservableProperty] private string _downloadStatus = "";

    public void Refresh()
    {
        _loading = true;
        var s = AppServices.Settings;
        AutoCheck = s.AutoCheckForUpdates;
        KeepSegmentCache = s.KeepSegmentCache;
        DefaultNarrator = Voices.FirstOrDefault(v => v.Id == s.DefaultNarratorVoice) ?? Voices[0];
        Version = "Version " + AppServices.Updates.CurrentVersion;
        CanUpdate = AppServices.Updates.CanUpdate;
        if (!CanUpdate && UpdateStatus.Length == 0)
            UpdateStatus = "Updates work in the installed app (this copy is running from a build folder).";
        DataFolder = AppServices.Store.Root;
        StorageSummary = $"Voice models {Ui.Size(AppServices.Store.ModelsSize())} · Audio and caches {Ui.Size(AppServices.Store.AudioSize())}";
        foreach (var m in Models) m.Refresh();
        DownloadStatus = AppServices.Models.Downloading is { } d ? $"Downloading {d.Label}… {AppServices.Models.Progress * 100:0}%" : AppServices.Models.Error ?? "";
        _loading = false;
    }

    async partial void OnAutoCheckChanged(bool value)
    {
        if (_loading) return;
        if (value)
        {
            // Turning this on is consent to a network check at each launch; confirm it explicitly.
            var ok = await Dialogs.ConfirmAsync("Check for updates automatically?",
                "Each time Bilgames Reader starts, it will contact github.com to see whether a newer version exists. " +
                "It never downloads or installs anything without asking you first, and nothing from your books is sent.",
                "Turn on");
            if (!ok)
            {
                _loading = true;
                AutoCheck = false;
                _loading = false;
                return;
            }
        }
        AppServices.Settings.AutoCheckForUpdates = value;
        AppServices.SaveSettings();
    }

    partial void OnKeepSegmentCacheChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.KeepSegmentCache = value;
        AppServices.SaveSettings();
    }

    partial void OnDefaultNarratorChanged(VoiceOptionViewModel? value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.DefaultNarratorVoice = value.Id;
        AppServices.SaveSettings();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!CanUpdate || IsUpdating) return;
        if (!await Dialogs.ConfirmNetworkAsync("check for a newer version of Bilgames Reader", "github.com")) return;
        await RunUpdateCheck(confirmDownload: true);
    }

    /// <summary>Shared with the launch-time auto check (which the user opted into).</summary>
    public async Task RunUpdateCheck(bool confirmDownload)
    {
        IsUpdating = true;
        UpdateStatus = "Checking…";
        try
        {
            var version = await AppServices.Updates.CheckAsync();
            AppServices.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            AppServices.SaveSettings();
            if (version is null)
            {
                UpdateStatus = "You're up to date.";
                return;
            }
            UpdateStatus = $"Version {version} is available.";
            var size = AppServices.Updates.PendingSize;
            var go = await Dialogs.ConfirmAsync($"Update to version {version}?",
                $"Download the update{(size > 0 ? $" ({Ui.Size(size)})" : "")} from github.com, then restart Bilgames Reader to install it. " +
                "Your library, voices and audio are kept. Generation in progress resumes after the restart.",
                "Download and install", "Not now");
            if (!go) return;
            UpdateStatus = "Downloading update…";
            await AppServices.Updates.DownloadAsync(p => AppServices.RunOnUI(() => { UpdateProgress = p; UpdateStatus = $"Downloading update… {p}%"; }));
            UpdateStatus = "Restarting to install…";
            _shell.Player.SavePosition(force: true);
            _shell.Remember();
            AppServices.Updates.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            AppServices.Log("Update failed: " + ex.Message);
            UpdateStatus = "Couldn't check for updates: " + ex.Message;
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task DownloadModelAsync(ModelRowViewModel? row)
    {
        if (row is null) return;
        await AppServices.Models.RequestDownloadAsync(row.Model);
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteModelAsync(ModelRowViewModel? row)
    {
        if (row is null) return;
        var ok = await Dialogs.ConfirmAsync($"Delete {row.Label}?",
            "Books keep their generated audio. Generating new audio or previews with this model will need it downloaded again.", "Delete", destructive: true);
        if (!ok) return;
        AppServices.Models.Delete(row.Model);
        Refresh();
    }

    [RelayCommand]
    private async Task ClearCachesAsync()
    {
        var ok = await Dialogs.ConfirmAsync("Clear segment caches?",
            "Chapters you've already generated stay playable. Fixing a speaker later will regenerate the whole chapter instead of just the changed lines.",
            "Clear caches", destructive: true);
        if (!ok) return;
        await Task.Run(() =>
        {
            foreach (var b in AppServices.Store.LoadAll())
                if (AppServices.Jobs.ActiveBookId != b.Id) AppServices.Store.ClearSegmentCache(b.Id);
        });
        Refresh();
        _shell.Library.RefreshSummary();
    }

    [RelayCommand]
    private void OpenDataFolder() => System.Diagnostics.Process.Start("explorer.exe", AppServices.Store.Root);
}
