using Bilgames.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Bilgames.App.Controls;

public enum PaletteKind { Dark, Light, Contrast }

/// <summary>
/// The app's colours for dark, light and Windows contrast themes. The design tokens are shared
/// brushes (Theme/Tokens.xaml), so switching recolours them in place and everything using them
/// updates at once. Colours written as hex in code (speaker chips, status dots) go through
/// <see cref="Map"/>, which translates the dark design's values for the current theme.
/// </summary>
public static class Palette
{
    // token → (dark, light). Dark values are the design's (design/tokens.json).
    private static readonly (string Key, string Dark, string Light)[] Tokens =
    {
        ("BgBrush", "#0E1116", "#F6F4F0"),
        ("NavBgBrush", "#0B0E12", "#ECE9E3"),
        ("SurfaceBrush", "#171B22", "#FFFFFF"),
        ("SurfaceRaisedBrush", "#1F242D", "#F1EEE8"),
        ("SurfaceChipBrush", "#232934", "#E9E5DD"),
        ("PlayerPaneBrush", "#12161C", "#EFECE6"),
        ("InfoBgBrush", "#141820", "#F2EFEA"),
        ("NavSelectedBrush", "#1A1F27", "#E2DDD3"),
        ("LineBrush", "#1F242D", "#E2DDD4"),
        ("LineStrongBrush", "#2A303A", "#D3CCC0"),
        ("LineDashedBrush", "#3A414D", "#B3AB9E"),
        ("TextBrush", "#ECEEF1", "#1B1E23"),
        ("TextSecondaryBrush", "#C9CED6", "#30353D"),
        ("TextMutedBrush", "#9AA3AF", "#535A64"),
        ("TextDimBrush", "#6B7482", "#646B75"),
        ("AccentBrush", "#F0A848", "#9A5800"),
        ("AccentHoverBrush", "#F7C47E", "#B26A0C"),
        ("OnAccentBrush", "#1A1205", "#FFFFFF"),
        ("SuccessBrush", "#9BD39A", "#25692B"),
        ("SuccessBgBrush", "#1E3324", "#DDF0DE"),
        ("WarningBrush", "#F0C27A", "#7D5200"),
        ("WarningBgBrush", "#2A2312", "#FFF3DA"),
        ("WarningBorderBrush", "#4A3B17", "#EBD29C"),
        ("WarningTextBrush", "#F3E3C3", "#4A3500"),
        ("WarningStrongBrush", "#FFF1D6", "#3A2900"),
        ("BadgeAmberBgBrush", "#3A2A12", "#F9E4BF"),
        ("CoverTextBrush", "#F3E9D7", "#F3E9D7"),
        ("OnSpeakerBrush", "#10141A", "#10141A"),
        ("ProgressAudioBrush", "#7FB2F0", "#2C67AE"),
        ("OverlayBrush", "#FFFFFF", "#000000"),
        ("SliderThumbBrush", "#ECEEF1", "#1B1E23"),
        ("SliderThumbHoverBrush", "#FFFFFF", "#000000"),
        ("HighlightTextBrush", "#FFF8EC", "#1B1E23"),
    };

    // Other hex values used in code that need a light equivalent (highlight colours, the current-line mark).
    private static readonly Dictionary<string, string> LightExtras = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#5A4710"] = "#FBE9A0", ["#1E4A33"] = "#CDEED8", ["#1F3B63"] = "#D2E3FA", ["#5A2343"] = "#F8D3DE",
        ["#FFF8EC"] = "#1B1E23", ["#D8923A"] = "#9A5800",
    };

    private static readonly Dictionary<string, string> DarkToLight = BuildLightMap();
    private static Dictionary<string, string> BuildLightMap()
    {
        var map = new Dictionary<string, string>(LightExtras, StringComparer.OrdinalIgnoreCase);
        foreach (var (_, dark, light) in Tokens) map.TryAdd(dark, light);
        return map;
    }

    // Which dark values are backgrounds / accents, for contrast themes (everything else is text or lines).
    private static readonly HashSet<string> BackgroundValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "#0E1116", "#0B0E12", "#171B22", "#1F242D", "#232934", "#12161C", "#141820", "#1A1F27",
        "#1E3324", "#2A2312", "#3A2A12", "#1A1205", "#10141A", "#5A4710", "#1E4A33", "#1F3B63", "#5A2343",
    };
    private static readonly HashSet<string> AccentValues = new(StringComparer.OrdinalIgnoreCase) { "#F0A848", "#F7C47E", "#D8923A" };

    private static readonly UISettings UiSettings = new();
    private static readonly AccessibilitySettings Accessibility = new();
    private static AppTheme _choice = AppTheme.System;

    public static PaletteKind Current { get; private set; } = PaletteKind.Dark;
    public static ElementTheme ElementTheme => Current == PaletteKind.Light ? ElementTheme.Light : ElementTheme.Dark;

    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event Action? Changed;

    /// <summary>Applies the user's choice now, and again whenever Windows switches light/dark or a contrast theme.</summary>
    public static void Initialize(AppTheme choice)
    {
        _choice = choice;
        Apply();
        try { UiSettings.ColorValuesChanged += (_, _) => Services.AppServices.RunOnUI(Apply); } catch { }
        // (Desktop apps can't subscribe to AccessibilitySettings.HighContrastChanged; turning a contrast
        // theme on or off raises ColorValuesChanged too.)
    }

    public static void Choose(AppTheme choice)
    {
        _choice = choice;
        Apply();
    }

    private static bool IsHighContrast() { try { return Accessibility.HighContrast; } catch { return false; } }

    private static PaletteKind Resolve()
    {
        if (IsHighContrast()) return PaletteKind.Contrast;
        return _choice switch
        {
            AppTheme.Light => PaletteKind.Light,
            AppTheme.Dark => PaletteKind.Dark,
            // Windows' "app mode": a light background colour means apps should be light.
            _ => UiSettings.GetColorValue(UIColorType.Background).R > 128 ? PaletteKind.Light : PaletteKind.Dark,
        };
    }

    private static void Apply()
    {
        var kind = Resolve();
        Current = kind;
        var resources = Application.Current.Resources;
        foreach (var (key, dark, light) in Tokens)
        {
            if (!resources.TryGetValue(key, out var value) || value is not SolidColorBrush brush) continue;
            brush.Color = kind switch
            {
                PaletteKind.Light => Parse(light),
                PaletteKind.Contrast => ContrastFor(dark, key),
                _ => Parse(dark),
            };
        }
        Ui.ClearBrushCache();
        Changed?.Invoke();
    }

    /// <summary>The current theme's version of a colour from the dark design.</summary>
    public static string Map(string hex) => Current switch
    {
        PaletteKind.Light => DarkToLight.TryGetValue(hex, out var l) ? l : hex,
        PaletteKind.Contrast => ToHex(ContrastFor(hex, null, keepUnknown: true)),
        _ => hex,
    };

    /// <summary>
    /// A speaker's colour used as text: their pastel as-is on dark, a darker shade of it on light
    /// (so it stays readable), and the plain text colour in contrast themes.
    /// </summary>
    public static string TextColor(string hex) => DarkToLight.ContainsKey(hex) ? Map(hex) : Current switch
    {
        PaletteKind.Light => ToHex(Mix(Parse(hex), Color.FromArgb(255, 0, 0, 0), 0.55)),
        PaletteKind.Contrast => ToHex(Sys(UIElementType.WindowText)),
        _ => hex,
    };

    /// <summary>Title-bar colours matching the navigation column.</summary>
    public static (Color Bg, Color Fg, Color FgInactive, Color Hover, Color Pressed) TitleBar => Current switch
    {
        PaletteKind.Light => (Parse("#ECE9E3"), Parse("#1B1E23"), Parse("#646B75"), Parse("#E2DDD3"), Parse("#D3CCC0")),
        PaletteKind.Contrast => (Sys(UIElementType.Window), Sys(UIElementType.WindowText), Sys(UIElementType.GrayText), Sys(UIElementType.Highlight), Sys(UIElementType.Highlight)),
        _ => (Parse("#0B0E12"), Parse("#ECEEF1"), Parse("#6B7482"), Parse("#1F242D"), Parse("#2A303A")),
    };

    private static Color ContrastFor(string darkHex, string? key, bool keepUnknown = false)
    {
        if (key is "OnAccentBrush") return Sys(UIElementType.HighlightText);
        if (key is "OverlayBrush" or "HighlightTextBrush") return Sys(UIElementType.WindowText);
        if (key is "CoverTextBrush" or "OnSpeakerBrush") return Parse(darkHex); // drawn on the book's own colours
        if (AccentValues.Contains(darkHex)) return Sys(UIElementType.Highlight);
        if (BackgroundValues.Contains(darkHex)) return Sys(UIElementType.Window);
        if (keepUnknown && !DarkToLight.ContainsKey(darkHex)) return Parse(darkHex); // e.g. a speaker's chip colour
        return Sys(UIElementType.WindowText);
    }

    private static Color Sys(UIElementType t) => UiSettings.UIElementColor(t);

    private static Color Parse(string hex)
    {
        var h = hex.TrimStart('#');
        byte a = 255;
        if (h.Length == 8) { a = Convert.ToByte(h[..2], 16); h = h[2..]; }
        return Color.FromArgb(a, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
