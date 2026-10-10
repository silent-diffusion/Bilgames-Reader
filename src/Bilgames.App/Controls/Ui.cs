using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Controls;

/// <summary>Small helpers usable from x:Bind function bindings.</summary>
public static class Ui
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public static SolidColorBrush Brush(string? hex)
    {
        hex ??= "#000000";
        if (Cache.TryGetValue(hex, out var b)) return b;
        var h = hex.TrimStart('#');
        byte a = 255;
        if (h.Length == 8) { a = Convert.ToByte(h[..2], 16); h = h[2..]; }
        var color = Windows.UI.Color.FromArgb(a, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
        b = new SolidColorBrush(color);
        Cache[hex] = b;
        return b;
    }

    public static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotVis(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisText(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool v) => !v;
    public static double Percent(double fraction) => Math.Round(fraction * 100, 1);
    public static string Upper(string? s) => (s ?? "").ToUpperInvariant();

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
