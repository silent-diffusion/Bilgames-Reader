using Bilgames.App.Controls;
using Bilgames.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

/// <summary>Settings › Review assistant: turn it on, download the model, and set how sure it must be.</summary>
public sealed partial class SettingsViewModel
{
    public AssistantService Assistant => AppServices.Assistant;

    private static readonly double[] Thresholds = { 0.9, 0.8, 0.7 };

    [ObservableProperty] private bool _assistantEnabled;
    [ObservableProperty] private int _assistantThresholdIndex = 1;
    [ObservableProperty] private string _assistantMemory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AssistantMemoryColor))] private bool _assistantMemoryOk = true;
    /// <summary>Muted when the PC has enough memory, amber as a warning when it may not.</summary>
    public string AssistantMemoryColor => AssistantMemoryOk ? "#9AA3AF" : "#F0C27A";

    public string AssistantSizeText => $"{Assistant.ModelLabel} · {Ui.Size(Assistant.ModelBytes)} download, plus llama.cpp (about 40 MB)";

    private void RefreshAssistant()
    {
        Assistant.Refresh();
        AssistantEnabled = Assistant.Enabled;
        var t = AppServices.Settings.Assistant.ApplyThreshold;
        AssistantThresholdIndex = Array.IndexOf(Thresholds, Thresholds.OrderBy(x => Math.Abs(x - t)).First());
        (AssistantMemoryOk, AssistantMemory) = Assistant.MemoryCheck();
    }

    partial void OnAssistantEnabledChanged(bool value)
    {
        if (_loading) return;
        Assistant.SetEnabled(value);
    }

    partial void OnAssistantThresholdIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= Thresholds.Length) return;
        AppServices.Settings.Assistant.ApplyThreshold = Thresholds[value];
        AppServices.SaveSettings();
    }

    [RelayCommand]
    private async Task DownloadAssistantAsync()
    {
        await Assistant.DownloadAsync();
        RefreshAssistant();
    }

    [RelayCommand]
    private void PauseAssistantDownload() => Assistant.PauseDownload();

    [RelayCommand]
    private async Task DeleteAssistantAsync()
    {
        var ok = await Dialogs.ConfirmAsync("Remove the review assistant?",
            $"Deletes the model ({Ui.Size(Assistant.ModelBytes)}) and llama.cpp from this PC. Changes it already made to your books stay (and can still be undone). You can download it again any time.",
            "Remove", destructive: true);
        if (!ok) return;
        await Assistant.DeleteAsync();
        RefreshAssistant();
    }
}
