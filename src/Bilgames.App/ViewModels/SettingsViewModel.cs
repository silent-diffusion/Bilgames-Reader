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
        AppServices.Engine.DeviceChanged += () => AppServices.RunOnUI(RefreshDevice);
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
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _installLabel = "Install update";

    public void Refresh()
    {
        _loading = true;
        var s = AppServices.Settings;
        AutoCheck = s.AutoCheckForUpdates;
        KeepSegmentCache = s.KeepSegmentCache;
        PreferGpu = s.Processor == Bilgames.Core.Tts.ProcessorPreference.Gpu;
        DefaultNarrator = Voices.FirstOrDefault(v => v.Id == s.DefaultNarratorVoice) ?? Voices[0];
        Version = "Version " + AppServices.Updates.CurrentVersion;
        CanUpdate = AppServices.Updates.CanUpdate;
        if (!CanUpdate && UpdateStatus.Length == 0)
            UpdateStatus = "Updates work in the installed app.";
        DataFolder = AppServices.Store.Root;
        // Measured off the UI thread: the segment cache can hold thousands of files.
        Task.Run(() => (AppServices.Store.ModelsSize(), AppServices.Store.AudioSize())).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) AppServices.RunOnUI(() => StorageSummary = $"Voice models {Ui.Size(t.Result.Item1)} · Audio and caches {Ui.Size(t.Result.Item2)}");
        });
        foreach (var m in Models) m.Refresh();
        DownloadStatus = AppServices.Models.Downloading is { } d ? $"Downloading {d.Label}… {AppServices.Models.Progress * 100:0}%" : AppServices.Models.Error ?? "";
        _loading = false;
        RefreshDevice();
    }

    partial void OnAutoCheckChanged(bool value)
    {
        if (_loading) return;
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
        await RunUpdateCheck(fromLaunch: false);
    }

    /// <summary>Checks GitHub for a newer release. From the launch-time check, offers to install right away.</summary>
    public async Task RunUpdateCheck(bool fromLaunch)
    {
        IsUpdating = true;
        UpdateAvailable = false;
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
            var size = AppServices.Updates.PendingSize;
            UpdateStatus = $"Version {version} is available. Your library, voices and audio are kept.";
            InstallLabel = $"Install version {version}{(size > 0 ? $" ({Ui.Size(size)})" : "")}";
            UpdateAvailable = true;
            IsUpdating = false;
            if (fromLaunch && await Dialogs.ConfirmAsync($"Version {version} is available", "Install it now? Bilgames Reader restarts to finish; anything generating picks up where it left off.", "Install now", "Later"))
                await InstallUpdateAsync();
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
    private async Task InstallUpdateAsync()
    {
        if (!UpdateAvailable) return;
        IsUpdating = true;
        try
        {
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
            UpdateStatus = "The update couldn't be installed: " + ex.Message;
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // ---------- processing (CPU / GPU) ----------

    [ObservableProperty] private bool _preferGpu;
    [ObservableProperty] private bool _hasGpu;
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _deviceNow = "";
    [ObservableProperty] private string _deviceDetail = "";
    [ObservableProperty] private bool _gpuNeedsModel;
    [ObservableProperty] private GpuMemoryReport? _gpuMemory;

    public void RefreshDevice()
    {
        var engine = AppServices.Engine;
        HasGpu = engine.Gpu is not null;
        GpuName = engine.Gpu?.Name ?? "No compatible GPU found";
        GpuNeedsModel = HasGpu && PreferGpu && !engine.GpuModelInstalled;
        DeviceNow = engine.DeviceLabel;
        DeviceDetail = GpuNeedsModel
            ? $"GPU processing uses the {KokoroModels.Gpu.Label} model ({Ui.Size(KokoroModels.Gpu.Size)})."
            : engine.DeviceDetail;
        GpuMemory = PreferGpu ? GpuMemoryReport.From(engine) : null;
        if (GpuMemory is not null) DeviceDetail = ""; // the memory panel explains it
    }

    partial void OnPreferGpuChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Processor = value ? Bilgames.Core.Tts.ProcessorPreference.Gpu : Bilgames.Core.Tts.ProcessorPreference.Cpu;
        AppServices.SaveSettings();
        RefreshDevice();
    }

    [RelayCommand]
    private async Task DownloadGpuModelAsync()
    {
        await AppServices.Models.RequestDownloadAsync(KokoroModels.Gpu);
        Refresh();
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
