using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Controls;

/// <summary>One stretch of text in a context view: a narration or dialogue line.</summary>
public sealed class ContextRun
{
    public string LineId { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Speaker colour for dialogue; null for narration (drawn in the neutral text colour).</summary>
    public string? Color { get; init; }
    public string Speaker { get; init; } = "";
    public bool IsTarget { get; init; }
}

/// <summary>
/// Paragraphs of book text in Literata as one continuous block. Dialogue is drawn in each speaker's
/// colour, narration in neutral grey, and the target line is bold with a highlight behind it.
/// </summary>
public sealed partial class LineContextView : UserControl
{
    private readonly RichTextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = false,
        LineHeight = 25,
    };

    private readonly List<(TextPointer Start, ContextRun Run)> _runs = new();

    public LineContextView()
    {
        IsTabStop = false;
        _text.FontFamily = (FontFamily)Application.Current.Resources["BookFont"];
        _text.FontSize = 16;
        _text.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        _text.Tapped += OnTapped;
        Content = _text;
    }

    public static readonly DependencyProperty ParagraphsProperty = DependencyProperty.Register(
        nameof(Paragraphs), typeof(IReadOnlyList<IReadOnlyList<ContextRun>>), typeof(LineContextView),
        new PropertyMetadata(null, (d, _) => ((LineContextView)d).Render()));

    public IReadOnlyList<IReadOnlyList<ContextRun>>? Paragraphs
    {
        get => (IReadOnlyList<IReadOnlyList<ContextRun>>?)GetValue(ParagraphsProperty);
        set => SetValue(ParagraphsProperty, value);
    }

    public double TextSize { get => _text.FontSize; set { _text.FontSize = value; _text.LineHeight = Math.Round(value * 1.55); } }

    public static readonly DependencyProperty EmphasizedProperty = DependencyProperty.Register(
        nameof(Emphasized), typeof(bool), typeof(LineContextView),
        new PropertyMetadata(false, (d, e) => ((LineContextView)d)._text.Foreground =
            (Brush)Application.Current.Resources[(bool)e.NewValue ? "TextBrush" : "TextSecondaryBrush"]));

    /// <summary>Full-strength text (the paragraph being read).</summary>
    public bool Emphasized { get => (bool)GetValue(EmphasizedProperty); set => SetValue(EmphasizedProperty, value); }

    /// <summary>Raised when the reader taps a line (used by the player to jump to it).</summary>
    public event Action<ContextRun>? LineTapped;

    private void Render()
    {
        _text.Blocks.Clear();
        _text.TextHighlighters.Clear();
        _runs.Clear();
        var paragraphs = Paragraphs;
        if (paragraphs is null) return;

        var highlight = new TextHighlighter { Background = Ui.Brush("#3A2A12"), Foreground = Ui.Brush("#FFF1D6") };
        foreach (var lines in paragraphs)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var run = new Run { Text = (i > 0 ? " " : "") + line.Text };
                if (line.Color is { } c) run.Foreground = Ui.Brush(c);
                if (line.IsTarget) run.FontWeight = FontWeights.Bold;
                p.Inlines.Add(run);
            }
            _text.Blocks.Add(p);
        }

        // Second pass: tap positions use text pointers; highlight ranges use plain character
        // indices (each paragraph break counts as one character).
        int chars = 0;
        foreach (var (p, lines) in _text.Blocks.OfType<Paragraph>().Zip(paragraphs))
        {
            var runs = p.Inlines.OfType<Run>().ToList();
            for (int i = 0; i < runs.Count && i < lines.Count; i++)
            {
                _runs.Add((runs[i].ContentStart, lines[i]));
                int lead = i > 0 ? 1 : 0; // the joining space
                if (lines[i].IsTarget)
                    highlight.Ranges.Add(new TextRange { StartIndex = chars + lead, Length = lines[i].Text.Length });
                chars += runs[i].Text.Length;
            }
            chars += 0; // paragraph breaks are not counted by TextHighlighter
        }
        if (highlight.Ranges.Count > 0) _text.TextHighlighters.Add(highlight);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (LineTapped is null || _runs.Count == 0) return;
        var pos = _text.GetPositionFromPoint(e.GetPosition(_text));
        if (pos is null) return;
        ContextRun? hit = null;
        foreach (var (start, run) in _runs)
        {
            if (start.Offset <= pos.Offset) hit = run;
            else break;
        }
        if (hit is not null) LineTapped(hit);
    }
}
