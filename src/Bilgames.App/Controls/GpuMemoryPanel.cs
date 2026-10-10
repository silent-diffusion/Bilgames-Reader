using Bilgames.Tts.Kokoro;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Controls;

/// <summary>Why the GPU is too full right now: which apps hold its memory.</summary>
public sealed class GpuMemoryReport
{
    public sealed record Row(string Name, string Size, double Fraction, bool IsThisApp);

    public string Heading { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<Row> Rows { get; init; } = Array.Empty<Row>();

    /// <summary>A report while the GPU is preferred but short on memory; null otherwise.</summary>
    public static GpuMemoryReport? From(KokoroEngine engine)
    {
        if (!engine.GpuMemoryShort || engine.Gpu is not { } gpu) return null;
        long total = Math.Max(1, gpu.DedicatedBytes);
        var users = engine.GpuMemoryUsers;
        return new GpuMemoryReport
        {
            Heading = $"The GPU is busy, so speech is running on the CPU",
            Body = $"{Ui.Size(engine.GpuFreeBytes)} of the {gpu.Name}'s {Ui.Size(gpu.DedicatedBytes)} is free; generating needs about {Ui.Size(engine.GpuBytesNeeded)}. " +
                   "It moves back to the GPU by itself once enough memory frees up" + (users.Count > 0 ? " — closing one of these apps will do it." : "."),
            Rows = users.Select(u => new Row(
                u.IsThisApp ? "Bilgames Reader (this app)" : u.Processes > 1 ? $"{u.Name} ({u.Processes} processes)" : u.Name,
                Ui.Size(u.Bytes), Math.Clamp((double)u.Bytes / total, 0, 1), u.IsThisApp)).ToList(),
        };
    }
}

/// <summary>Renders a <see cref="GpuMemoryReport"/>: a heading, an explanation, and one bar per app.</summary>
public sealed partial class GpuMemoryPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _heading = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _body = new() { FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.Wrap };

    public GpuMemoryPanel()
    {
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        _heading.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        _body.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        var caption = new TextBlock { Text = "USING GPU MEMORY", FontSize = 11, FontWeight = FontWeights.Bold, CharacterSpacing = 80, Margin = new Thickness(0, 4, 0, 0) };
        caption.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        _caption = caption;
        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceRaisedBrush"],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 12),
            Child = new StackPanel { Spacing = 6, Children = { _heading, _body, caption, _rows } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "GPU memory use");
    }

    private readonly TextBlock _caption;

    public static readonly DependencyProperty ReportProperty = DependencyProperty.Register(
        nameof(Report), typeof(GpuMemoryReport), typeof(GpuMemoryPanel),
        new PropertyMetadata(null, (d, _) => ((GpuMemoryPanel)d).Render()));

    public GpuMemoryReport? Report
    {
        get => (GpuMemoryReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    private void Render()
    {
        var r = Report;
        Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
        _rows.Children.Clear();
        if (r is null) return;
        _heading.Text = r.Heading;
        _body.Text = r.Body;
        _caption.Visibility = r.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in r.Rows)
        {
            var name = new TextBlock { Text = row.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Foreground = (Brush)Application.Current.Resources[row.IsThisApp ? "TextMutedBrush" : "TextBrush"];
            var size = new TextBlock { Text = row.Size, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            size.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(size, 1);
            line.Children.Add(name);
            line.Children.Add(size);
            var track = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.Resources["LineStrongBrush"] };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(row.Fraction, 0.004), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - row.Fraction, GridUnitType.Star) });
            track.Children.Add(new Border { Background = (Brush)Application.Current.Resources[row.IsThisApp ? "TextMutedBrush" : "WarningStrongBrush"], CornerRadius = new CornerRadius(2) });
            var item = new StackPanel { Spacing = 4, Children = { line, track } };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, $"{row.Name}: {row.Size}");
            _rows.Children.Add(item);
        }
    }
}
