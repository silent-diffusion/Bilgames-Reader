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

    public static async Task<ContentDialogResult> ShowContentAsync(string title, UIElement content, string primary, string close = "Close", Action<ContentDialog>? setup = null)
    {
        if (Root is null || _open) return ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            XamlRoot = Root,
            RequestedTheme = Controls.Palette.ElementTheme,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
        };
        dialog.DefaultButton = primary.Length > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close;
        setup?.Invoke(dialog);
        return await Show(dialog);
    }

    /// <summary>Asks for a line (or a few lines) of text. Returns null when cancelled.</summary>
    public static async Task<string?> PromptAsync(string title, string? context, string initial, string placeholder, string primary, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 96 : 0,
            MaxLength = 2000,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, placeholder);
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        if (!string.IsNullOrEmpty(context))
            panel.Children.Add(new TextBlock
            {
                Text = context, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.WordEllipsis, FontSize = 14, LineHeight = 21,
                FontFamily = (FontFamily)Application.Current.Resources["BookFont"], Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            });
        panel.Children.Add(box);
        var result = await ShowContentAsync(title, panel, primary, "Cancel", d => d.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); });
        return result == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private static ContentDialog Build(string title, string body) => new()
    {
        Title = title,
        Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 },
        XamlRoot = Root,
        RequestedTheme = Controls.Palette.ElementTheme,
        Background = (Brush)Application.Current.Resources["SurfaceBrush"],
    };

    private static async Task<ContentDialogResult> Show(ContentDialog d)
    {
        _open = true;
        try { return await d.ShowAsync(); }
        finally { _open = false; }
    }
}
