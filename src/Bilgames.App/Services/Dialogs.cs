using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Services;

public static class Dialogs
{
    private static bool _open;

    public static XamlRoot? Root => App.Window?.Content?.XamlRoot;

    public static async Task<bool> ConfirmAsync(string title, string body, string primary, string close = "Cancel", bool destructive = false)
    {
        if (Root is null || _open) return false;
        var dialog = Build(title, body);
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText = close;
        dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        return await Show(dialog) == ContentDialogResult.Primary;
    }

    public static async Task InfoAsync(string title, string body)
    {
        if (Root is null || _open) return;
        var dialog = Build(title, body);
        dialog.CloseButtonText = "OK";
        await Show(dialog);
    }

    /// <summary>Three-way choice; returns 1 for primary, 2 for secondary, 0 for cancel.</summary>
    public static async Task<int> ChooseAsync(string title, string body, string primary, string secondary, string close = "Cancel")
    {
        if (Root is null || _open) return 0;
        var dialog = Build(title, body);
        dialog.PrimaryButtonText = primary;
        dialog.SecondaryButtonText = secondary;
        dialog.CloseButtonText = close;
        dialog.DefaultButton = ContentDialogButton.Close;
        var r = await Show(dialog);
        return r == ContentDialogResult.Primary ? 1 : r == ContentDialogResult.Secondary ? 2 : 0;
    }

    public static async Task<ContentDialogResult> ShowContentAsync(string title, UIElement content, string primary, string close = "Close")
    {
        if (Root is null || _open) return ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            XamlRoot = Root,
            RequestedTheme = ElementTheme.Dark,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
        };
        return await Show(dialog);
    }

    private static ContentDialog Build(string title, string body) => new()
    {
        Title = title,
        Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 },
        XamlRoot = Root,
        RequestedTheme = ElementTheme.Dark,
        Background = (Brush)Application.Current.Resources["SurfaceBrush"],
    };

    private static async Task<ContentDialogResult> Show(ContentDialog d)
    {
        _open = true;
        try { return await d.ShowAsync(); }
        finally { _open = false; }
    }
}
