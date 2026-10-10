using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Controls;

/// <summary>Small helpers usable from x:Bind function bindings.</summary>
public static class Ui
{
    // One shared brush per colour from the dark design; recoloured in place when the theme changes.
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SolidColorBrush> TextCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A brush for a colour from the (dark) design, shown in the current theme's equivalent.</summary>
    public static SolidColorBrush Brush(string? hex)
    {
        hex ??= "#000000";
        if (Cache.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush(ParseColor(Palette.Map(hex)));
        Cache[hex] = b;
        return b;
    }

    /// <summary>A speaker's colour used for text, kept readable in light and contrast themes.</summary>
    public static SolidColorBrush TextBrush(string? hex)
    {
        hex ??= "#ECEEF1";
        if (TextCache.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush(ParseColor(Palette.TextColor(hex)));
        TextCache[hex] = b;
        return b;
    }

    /// <summary>Called by <see cref="Palette"/> after a theme change.</summary>
    internal static void ClearBrushCache()
    {
        foreach (var (hex, brush) in Cache) brush.Color = ParseColor(Palette.Map(hex));
        foreach (var (hex, brush) in TextCache) brush.Color = ParseColor(Palette.TextColor(hex));
    }

    public static Windows.UI.Color ParseColor(string hex)
    {
        var h = hex.TrimStart('#');
        byte a = 255;
        if (h.Length == 8) { a = Convert.ToByte(h[..2], 16); h = h[2..]; }
        return Windows.UI.Color.FromArgb(a, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    public static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotVis(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisText(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool v) => !v;
    public static Microsoft.UI.Xaml.Media.Brush AccentWhen(bool on) => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[on ? "AccentBrush" : "TextBrush"];
    public static double Percent(double fraction) => Math.Round(fraction * 100, 1);
    public static string Upper(string? s) => (s ?? "").ToUpperInvariant();

    /// <summary>Transcript paragraphs: current fully visible, others dimmed, not-yet-generated faint.</summary>
    public static double TranscriptOpacity(bool current, bool available) => current ? 1.0 : available ? 0.5 : 0.25;

    public static string Duration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "under a minute";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "0 MB",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
