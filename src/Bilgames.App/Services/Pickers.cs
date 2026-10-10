using Windows.Storage.Pickers;

namespace Bilgames.App.Services;

/// <summary>System file pickers (unpackaged apps must hand the picker their window handle).</summary>
public static class Pickers
{
    private static nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(App.Window!);

    public static async Task<string?> PickEpubAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".epub");
        picker.FileTypeFilter.Add(Bilgames.Core.Storage.LibraryBackup.PackageExtension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickOpenAsync(string extension)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Save picker offering several formats (the first is the default); starts in Documents.</summary>
    public static async Task<string?> PickSaveAsync(string suggestedName, params (string Label, string Extension)[] choices)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        foreach (var (label, ext) in choices) picker.FileTypeChoices.Add(label, new List<string> { ext });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickSaveAsync(string suggestedName, string label, string extension)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.MusicLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(label, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
