using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Bilgames.App.Controls;

/// <summary>
/// A 24-px-grid line icon drawn from SVG path data (1.8 stroke, round caps/joins), coloured by the
/// inherited Foreground so it follows hover/selected states of its button.
/// </summary>
public sealed partial class StrokeIcon : UserControl
{
    private readonly Microsoft.UI.Xaml.Shapes.Path _path = new()
    {
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public StrokeIcon()
    {
        IsTabStop = false;
        Width = 22;
        Height = 22;
        var canvas = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = new Grid { Width = 24, Height = 24, Children = { _path } },
        };
        Content = canvas;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Apply());
        Loaded += (_, _) => Apply();
        Apply();
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(string), typeof(StrokeIcon), new PropertyMetadata("", (d, _) => ((StrokeIcon)d).Apply()));

    public static readonly DependencyProperty StrokeWidthProperty =
        DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(StrokeIcon), new PropertyMetadata(1.8, (d, _) => ((StrokeIcon)d).Apply()));

    public static readonly DependencyProperty FilledProperty =
        DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(StrokeIcon), new PropertyMetadata(false, (d, _) => ((StrokeIcon)d).Apply()));

    public string Data { get => (string)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    private string? _parsed;

    private void Apply()
    {
        if (_parsed != Data && !string.IsNullOrEmpty(Data))
        {
            _path.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Data);
            _parsed = Data;
        }
        if (Filled)
        {
            _path.Fill = Foreground;
            _path.Stroke = null;
        }
        else
        {
            _path.Fill = null;
            _path.Stroke = Foreground;
            _path.StrokeThickness = StrokeWidth;
        }
    }
}

/// <summary>SVG path data for the design's icons (from the AppShell design file).</summary>
public static class Icons
{
    public const string Brand = "M4 5h6a2 2 0 0 1 2 2v12a2 2 0 0 0-2-2H4zM20 5h-6a2 2 0 0 0-2 2v12a2 2 0 0 1 2-2h6z";
    public const string Library = "M4 4h5v16H4zM11 4h4v16h-4zM17 5l3 .8-3.6 14.4-3-.8z";
    public const string Listening = "M3 18v-6a9 9 0 0 1 18 0v6M3 15h4v6H3zM17 15h4v6h-4z";
    public const string Voices = "M9 6a3 3 0 0 1 6 0v5a3 3 0 0 1-6 0zM5 11a7 7 0 0 0 14 0M12 18v3";
    public const string Jobs = "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1";
    public const string Settings = "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6zM19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 0 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 0 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 0 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 0 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z";
    public const string Lock = "M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4";
    public const string Search = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM16 16l5 5";
    public const string Plus = "M12 5v14M5 12h14";
    public const string Back = "M15 5l-7 7 7 7";
    public const string ChevronDown = "M6 9l6 6 6-6";
    public const string List = "M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01";
    public const string Rewind = "M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5";
    public const string Forward = "M21 12a9 9 0 1 1-3-6.7L21 8M21 3v5h-5";
    public const string Play = "M8 5v14l11-7z";
    public const string Pause = "M6 5h4v14H6zM14 5h4v14h-4z";
    public const string Moon = "M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z";
    public const string Download = "M12 3v12M7 10l5 5 5-5M5 21h14";
    public const string Eye = "M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z";
    public const string Thermometer = "M14 14.8V4a2 2 0 0 0-4 0v10.8a4 4 0 1 0 4 0z";
    public const string Plane = "M2 12l7 2 4 8 2-7 7-3-7-2-2-8-4 8z";
    public const string More = "M5 12h.01M12 12h.01M19 12h.01";
    public const string Check = "M5 12l5 5L20 7";
    public const string Close = "M6 6l12 12M18 6L6 18";
    public const string Folder = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
    public const string Trash = "M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3";
    public const string Refresh = "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7";
    public const string Book = "M4 19.5A2.5 2.5 0 0 1 6.5 17H20V3H6.5A2.5 2.5 0 0 0 4 5.5zM4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5";
}
