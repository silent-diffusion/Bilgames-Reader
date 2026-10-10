using Bilgames.Core.Model;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Controls;

/// <summary>A highlighted stretch inside one line: characters [Start, Start + Length) of its text.</summary>
public sealed class RunMark
{
    public string HighlightId { get; init; } = "";
    public int Start { get; init; }
    public int Length { get; init; }
    public HighlightColor Color { get; init; }
    public bool HasNote { get; init; }
}

/// <summary>One stretch of text in a context view: a narration or dialogue line.</summary>
public sealed class ContextRun
{
    public string LineId { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Speaker colour for dialogue; null for narration (drawn in the neutral text colour).</summary>
    public string? Color { get; init; }
    public string Speaker { get; init; } = "";
    public bool IsTarget { get; init; }
    /// <summary>The reader's highlights within this line.</summary>
    public IReadOnlyList<RunMark> Marks { get; init; } = Array.Empty<RunMark>();
}

/// <summary>A selected passage: from an offset in one line to an offset in another (same paragraph).</summary>
public sealed class TextSpanSelection
{
    public ContextRun StartRun { get; init; } = null!;
    public int StartOffset { get; init; }
    public ContextRun EndRun { get; init; } = null!;
    public int EndOffset { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>Highlight colours: a deep tint behind the text (dark theme) and a bright swatch for buttons.</summary>
public static class HighlightPalette
{
    public static string Background(HighlightColor c) => c switch
    {
        HighlightColor.Green => "#1E4A33",
        HighlightColor.Blue => "#1F3B63",
        HighlightColor.Pink => "#5A2343",
        _ => "#5A4710",
    };

    public static string Swatch(HighlightColor c) => c switch
    {
        HighlightColor.Green => "#6FCF97",
        HighlightColor.Blue => "#7FB2F0",
        HighlightColor.Pink => "#F4A3B5",
        _ => "#F2C94C",
    };

    public static string Name(HighlightColor c) => c.ToString();
}

/// <summary>
/// Paragraphs of book text in Literata as one continuous block. Dialogue is drawn in each speaker's
/// colour, narration in neutral grey, and the target line is bold with a highlight behind it. With
/// <see cref="AllowAnnotations"/>, text can be selected and highlighted, and right-click offers
/// bookmark / highlight / note actions.
/// </summary>
public sealed partial class LineContextView : UserControl
{
    private readonly RichTextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = false,
        LineHeight = 25,
    };

    private readonly List<(Run Run, ContextRun Line, int Lead)> _runs = new();

    public LineContextView()
    {
        IsTabStop = false;
        _text.FontFamily = (FontFamily)Application.Current.Resources["BookFont"];
        ApplySize();
        Loaded += (_, _) => { ScaleChanged += ApplySize; ApplySize(); };
        Unloaded += (_, _) => ScaleChanged -= ApplySize;
        _text.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        // Selectable text marks taps handled itself, so listen to handled ones too.
        _text.AddHandler(TappedEvent, new TappedEventHandler(OnTapped), true);
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

    private double _textSize = 16;

    /// <summary>Book text size at the default reading size; the reader's chosen scale is applied on top.</summary>
    public double TextSize { get => _textSize; set { _textSize = value; ApplySize(); } }

    private static double _scale = 1.0;
    private static event Action? ScaleChanged;

    /// <summary>Settings › Reading text size: scales book text everywhere it is shown.</summary>
    public static double Scale
    {
        get => _scale;
        set { _scale = Math.Clamp(value, 0.8, 2.0); ScaleChanged?.Invoke(); }
    }

    private void ApplySize()
    {
        _text.FontSize = Math.Round(_textSize * _scale, 1);
        _text.LineHeight = Math.Round(_text.FontSize * 1.55);
    }

    public static readonly DependencyProperty EmphasizedProperty = DependencyProperty.Register(
        nameof(Emphasized), typeof(bool), typeof(LineContextView),
        new PropertyMetadata(false, (d, e) => ((LineContextView)d)._text.Foreground =
            (Brush)Application.Current.Resources[(bool)e.NewValue ? "TextBrush" : "TextSecondaryBrush"]));

    /// <summary>Full-strength text (the paragraph being read).</summary>
    public bool Emphasized { get => (bool)GetValue(EmphasizedProperty); set => SetValue(EmphasizedProperty, value); }

    private bool _allowAnnotations;

    /// <summary>Selectable text with highlight / bookmark actions (the player's transcript).</summary>
    public bool AllowAnnotations
    {
        get => _allowAnnotations;
        set
        {
            if (_allowAnnotations == value) return;
            _allowAnnotations = value;
            _text.IsTextSelectionEnabled = value;
            if (value)
            {
                _text.SelectionFlyout = null; // our own toolbar instead of Copy / Select all
                _text.ContextFlyout = null;
                // Selectable text marks right-taps handled itself, so listen to handled ones too.
                _text.AddHandler(RightTappedEvent, new RightTappedEventHandler((_, e) => ShowMenu(e.GetPosition(_text))), true);
                _text.ContextRequested += OnContextRequested; // keyboard: Shift+F10 / menu key
                _text.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
            }
        }
    }

    /// <summary>Raised when the reader taps a line (used by the player to jump to it).</summary>
    public event Action<ContextRun>? LineTapped;
    /// <summary>Highlight a selection (or a whole line) in a colour; the flag asks for a note too.</summary>
    public event Action<TextSpanSelection, HighlightColor, bool>? HighlightRequested;
    public event Action<ContextRun>? BookmarkRequested;
    public event Action<string>? RemoveHighlightRequested;
    public event Action<string>? EditNoteRequested;

    private void Render()
    {
        _text.Blocks.Clear();
        _text.TextHighlighters.Clear();
        _runs.Clear();
        var paragraphs = Paragraphs;
        if (paragraphs is null) return;

        var target = new TextHighlighter { Background = Ui.Brush("#3A2A12"), Foreground = Ui.Brush("#FFF1D6") };
        var marks = Enum.GetValues<HighlightColor>().ToDictionary(c => c, c => new TextHighlighter { Background = Ui.Brush(HighlightPalette.Background(c)), Foreground = Ui.Brush("#FFF8EC") });
        foreach (var lines in paragraphs)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var run = new Run { Text = (i > 0 ? " " : "") + line.Text };
                if (line.Color is { } c) run.Foreground = Ui.TextBrush(c);
                if (line.IsTarget) run.FontWeight = FontWeights.Bold;
                p.Inlines.Add(run);
                _runs.Add((run, line, i > 0 ? 1 : 0));
            }
            _text.Blocks.Add(p);
        }

        // Highlight ranges use plain character indices; paragraph breaks are not counted.
        int chars = 0;
        foreach (var (run, line, lead) in _runs)
        {
            if (line.IsTarget) target.Ranges.Add(new TextRange { StartIndex = chars + lead, Length = line.Text.Length });
            foreach (var m in line.Marks)
            {
                int start = Math.Clamp(m.Start, 0, line.Text.Length), end = Math.Clamp(m.Start + m.Length, start, line.Text.Length);
                // Join with the previous line's highlight across the space between them.
                bool joinPrev = start == 0 && lead > 0 && _runs.TakeWhile(r => r.Run != run).LastOrDefault().Line?.Marks.Any(x => x.HighlightId == m.HighlightId) == true;
                if (end > start) marks[m.Color].Ranges.Add(new TextRange { StartIndex = chars + lead + start - (joinPrev ? 1 : 0), Length = end - start + (joinPrev ? 1 : 0) });
            }
            chars += run.Text.Length;
        }
        foreach (var h in marks.Values.Where(h => h.Ranges.Count > 0)) _text.TextHighlighters.Add(h);
        if (target.Ranges.Count > 0) _text.TextHighlighters.Add(target);
    }

    // ---------- hit testing ----------

    /// <summary>The line and character offset (within the line's text) at a text position.</summary>
    private (ContextRun Line, int Offset)? At(TextPointer? pos)
    {
        if (pos is null || _runs.Count == 0) return null;
        (Run Run, ContextRun Line, int Lead)? hit = null;
        foreach (var r in _runs)
        {
            if (r.Run.ContentStart.Offset <= pos.Offset) hit = r;
            else break;
        }
        if (hit is not { } h) return null;
        int offset = Math.Clamp(pos.Offset - h.Run.ContentStart.Offset - h.Lead, 0, h.Line.Text.Length);
        return (h.Line, offset);
    }

    private (ContextRun Line, int Offset)? AtPoint(Windows.Foundation.Point p) => At(_text.GetPositionFromPoint(p));

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (LineTapped is null || _runs.Count == 0) return;
        if (_allowAnnotations && _text.SelectedText.Length > 0) return; // finishing a selection, not a tap
        if (AtPoint(e.GetPosition(_text)) is { } hit) LineTapped(hit.Line);
    }

    /// <summary>The current selection, widened to whole words; null when nothing is selected.</summary>
    private TextSpanSelection? Selection()
    {
        if (_text.SelectedText.Trim().Length == 0) return null;
        if (At(_text.SelectionStart) is not { } a || At(_text.SelectionEnd) is not { } b) return null;
        int ai = _runs.FindIndex(r => r.Line == a.Line), bi = _runs.FindIndex(r => r.Line == b.Line);
        if (ai > bi || ai == bi && a.Offset > b.Offset) (a, b, ai, bi) = (b, a, bi, ai);
        int start = WordStart(a.Line.Text, a.Offset), end = WordEnd(b.Line.Text, b.Offset);
        if (ai == bi && end <= start) return null;
        var parts = new List<string>();
        for (int i = ai; i <= bi; i++)
        {
            var t = _runs[i].Line.Text;
            int s = i == ai ? start : 0, e = i == bi ? end : t.Length;
            if (e > s) parts.Add(t[s..e]);
        }
        var text = string.Join(" ", parts).Trim();
        return text.Length == 0 ? null : new TextSpanSelection { StartRun = a.Line, StartOffset = start, EndRun = b.Line, EndOffset = end, Text = text };
    }

    private static int WordStart(string t, int i)
    {
        i = Math.Clamp(i, 0, t.Length);
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        while (i > 0 && !char.IsWhiteSpace(t[i - 1])) i--;
        return i;
    }

    private static int WordEnd(string t, int i)
    {
        i = Math.Clamp(i, 0, t.Length);
        while (i > 0 && char.IsWhiteSpace(t[i - 1])) i--;
        while (i < t.Length && !char.IsWhiteSpace(t[i])) i++;
        return i;
    }

    private static TextSpanSelection WholeLine(ContextRun line) =>
        new() { StartRun = line, StartOffset = 0, EndRun = line, EndOffset = line.Text.Length, Text = line.Text };

    // ---------- selection toolbar and context menu ----------

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(_text);
        if (current.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased) return; // the context menu handles that
        var point = current.Position;
        // Let the selection settle, then offer to highlight it.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Selection() is { } sel) ShowToolbar(sel, point);
        });
    }

    private void ShowToolbar(TextSpanSelection sel, Windows.Foundation.Point at)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flyout = new Flyout { Content = bar, Placement = FlyoutPlacementMode.Top, ShouldConstrainToRootBounds = true };
        foreach (var color in Enum.GetValues<HighlightColor>())
        {
            var swatch = new Button
            {
                Width = 32, Height = 32, MinWidth = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(16),
                Background = Ui.Brush(HighlightPalette.Swatch(color)), BorderThickness = new Thickness(0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Highlight {HighlightPalette.Name(color).ToLowerInvariant()}");
            ToolTipService.SetToolTip(swatch, $"Highlight ({HighlightPalette.Name(color).ToLowerInvariant()})");
            swatch.Click += (_, _) => { flyout.Hide(); Clear(); HighlightRequested?.Invoke(sel, color, false); };
            bar.Children.Add(swatch);
        }
        var note = new Button { Content = "Note…", MinHeight = 32, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 0, 0) };
        ToolTipService.SetToolTip(note, "Highlight and add a note");
        note.Click += (_, _) => { flyout.Hide(); Clear(); HighlightRequested?.Invoke(sel, HighlightColor.Yellow, true); };
        var copy = new Button { Content = "Copy", MinHeight = 32, Padding = new Thickness(10, 0, 10, 0) };
        copy.Click += (_, _) => { flyout.Hide(); CopyText(sel.Text); };
        bar.Children.Add(note);
        bar.Children.Add(copy);
        // Keep the toolbar (about 320 px wide) inside the window so it opens right above the selection
        // instead of being moved somewhere else.
        const double half = 170;
        try
        {
            var abs = _text.TransformToVisual(null).TransformPoint(at);
            double rootWidth = XamlRoot?.Size.Width ?? double.MaxValue;
            double x = Math.Clamp(abs.X, half, Math.Max(half, rootWidth - half));
            at = new Windows.Foundation.Point(at.X + (x - abs.X), Math.Max(at.Y - 8, 0));
        }
        catch { }
        flyout.ShowAt(_text, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.Top, ShowMode = FlyoutShowMode.Transient });
    }

    private void Clear()
    {
        try { _text.Select(_text.ContentStart, _text.ContentStart); } catch { }
    }

    private static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        // Pointer right-clicks are handled by RightTapped; this is the keyboard (menu key / Shift+F10).
        if (!e.TryGetPosition(_text, out _)) ShowMenu(null);
    }

    /// <summary>Right-click / press-and-hold: highlight, bookmark, play from here, note, remove.</summary>
    private void ShowMenu(Windows.Foundation.Point? at)
    {
        bool hasPoint = at.HasValue;
        var point = at.GetValueOrDefault();
        var hit = hasPoint ? AtPoint(point) : _runs.Count > 0 ? (_runs[0].Line, 0) : null;
        var selection = Selection();
        if (hit is null && selection is null) return;

        var menu = new MenuFlyout();
        var span = selection ?? WholeLine(hit!.Value.Line);
        var label = selection is null ? "Highlight this line" : "Highlight selection";
        var sub = new MenuFlyoutSubItem { Text = label };
        foreach (var color in Enum.GetValues<HighlightColor>())
        {
            var item = new MenuFlyoutItem { Text = HighlightPalette.Name(color) };
            item.Icon = new FontIcon { Glyph = "●", FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = Ui.Brush(HighlightPalette.Swatch(color)) };
            item.Click += (_, _) => { Clear(); HighlightRequested?.Invoke(span, color, false); };
            sub.Items.Add(item);
        }
        menu.Items.Add(sub);
        var withNote = new MenuFlyoutItem { Text = selection is null ? "Highlight line with a note…" : "Highlight with a note…" };
        withNote.Click += (_, _) => { Clear(); HighlightRequested?.Invoke(span, HighlightColor.Yellow, true); };
        menu.Items.Add(withNote);

        if (hit is { } h)
        {
            var bookmark = new MenuFlyoutItem { Text = "Bookmark this line" };
            bookmark.Click += (_, _) => BookmarkRequested?.Invoke(h.Line);
            menu.Items.Add(bookmark);
            var play = new MenuFlyoutItem { Text = "Play from here" };
            play.Click += (_, _) => LineTapped?.Invoke(h.Line);
            menu.Items.Add(play);

            // On an existing highlight: edit its note or remove it.
            var mark = h.Line.Marks.FirstOrDefault(m => h.Offset >= m.Start && h.Offset <= m.Start + m.Length);
            if (mark is not null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var edit = new MenuFlyoutItem { Text = mark.HasNote ? "Edit note…" : "Add a note…" };
                edit.Click += (_, _) => EditNoteRequested?.Invoke(mark.HighlightId);
                var remove = new MenuFlyoutItem { Text = "Remove highlight" };
                remove.Click += (_, _) => RemoveHighlightRequested?.Invoke(mark.HighlightId);
                menu.Items.Add(edit);
                menu.Items.Add(remove);
            }
        }
        if (selection is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var copy = new MenuFlyoutItem { Text = "Copy" };
            copy.Click += (_, _) => CopyText(selection.Text);
            menu.Items.Add(copy);
        }
        if (hasPoint) menu.ShowAt(_text, point);
        else menu.ShowAt(_text);
    }
}
