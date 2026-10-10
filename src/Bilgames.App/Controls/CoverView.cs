using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;

namespace Bilgames.App.Controls;

/// <summary>
/// A book cover: the EPUB's own cover image when it has one, otherwise the design's typographic
/// placeholder (solid colour block, title in Literata, author in tiny capitals).
/// </summary>
public sealed partial class CoverView : UserControl
{
    private readonly Border _frame = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _author = new();
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };

    public CoverView()
    {
        IsTabStop = false;
        _title.FontFamily = (FontFamily)Application.Current.Resources["BookFont"];
        _title.FontWeight = new FontWeight(600);
        _title.Foreground = (Brush)Application.Current.Resources["CoverTextBrush"];
        _title.TextWrapping = TextWrapping.WrapWholeWords;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _author.FontSize = 8;
        _author.CharacterSpacing = 120;
        _author.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC6, 0xD6, 0xE0));
        _author.TextTrimming = TextTrimming.CharacterEllipsis;
        _author.TextWrapping = TextWrapping.NoWrap;

        var text = new Grid { RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        text.Children.Add(_title);
        Grid.SetRow(_author, 1);
        text.Children.Add(_author);
        _frame.Child = new Grid { Children = { text, _image } };
        Content = _frame;
        Update();
    }

    public static readonly DependencyProperty TitleProperty = Prop(nameof(Title), typeof(string), "");
    public static readonly DependencyProperty AuthorProperty = Prop(nameof(Author), typeof(string), "");
    public static readonly DependencyProperty CoverColorProperty = Prop(nameof(CoverColor), typeof(string), "#2B4A5E");
    public static readonly DependencyProperty ImagePathProperty = Prop(nameof(ImagePath), typeof(string), null);
    public static readonly DependencyProperty TitleSizeProperty = Prop(nameof(TitleSize), typeof(double), 12.0);
    public static readonly DependencyProperty ShowAuthorProperty = Prop(nameof(ShowAuthor), typeof(bool), false);
    public static readonly DependencyProperty RadiusProperty = Prop(nameof(Radius), typeof(double), 6.0);
    public static readonly DependencyProperty TitleAtTopProperty = Prop(nameof(TitleAtTop), typeof(bool), false);

    private static DependencyProperty Prop(string name, Type t, object? def) =>
        DependencyProperty.Register(name, t, typeof(CoverView), new PropertyMetadata(def, (d, _) => ((CoverView)d).Update()));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Author { get => (string)GetValue(AuthorProperty); set => SetValue(AuthorProperty, value); }
    public string CoverColor { get => (string)GetValue(CoverColorProperty); set => SetValue(CoverColorProperty, value); }
    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public double TitleSize { get => (double)GetValue(TitleSizeProperty); set => SetValue(TitleSizeProperty, value); }
    public bool ShowAuthor { get => (bool)GetValue(ShowAuthorProperty); set => SetValue(ShowAuthorProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public bool TitleAtTop { get => (bool)GetValue(TitleAtTopProperty); set => SetValue(TitleAtTopProperty, value); }

    private string? _loadedImage;

    private void Update()
    {
        _frame.Background = Ui.Brush(CoverColor);
        _frame.CornerRadius = new CornerRadius(Radius);
        _frame.Padding = new Thickness(Math.Max(5, TitleSize * 0.7));
        _title.Text = Title;
        _title.FontSize = TitleSize;
        _title.LineHeight = TitleSize * 1.15;
        _title.VerticalAlignment = TitleAtTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _author.Text = LastName(Author).ToUpperInvariant();
        _author.Visibility = ShowAuthor ? Visibility.Visible : Visibility.Collapsed;

        var path = ImagePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            if (_loadedImage != path)
            {
                _image.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 240 };
                _loadedImage = path;
            }
            _image.Visibility = Visibility.Visible;
            _frame.Padding = new Thickness(0);
            ((Grid)((Grid)_frame.Child).Children[0]).Visibility = Visibility.Collapsed;
        }
        else
        {
            _image.Source = null;
            _loadedImage = null;
            _image.Visibility = Visibility.Collapsed;
            ((Grid)((Grid)_frame.Child).Children[0]).Visibility = Visibility.Visible;
        }
        AutomationPropertiesHelper.SetName(this, $"Cover of {Title}");
    }

    private static string LastName(string author)
    {
        var parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : parts[^1];
    }
}

/// <summary>Speaker avatar: coloured circle with initials. Colour is never the only cue.</summary>
public sealed partial class Avatar : UserControl
{
    private readonly Border _circle = new();
    private readonly TextBlock _text = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = new FontWeight(800),
    };

    public Avatar()
    {
        IsTabStop = false;
        _text.Foreground = (Brush)Application.Current.Resources["OnSpeakerBrush"];
        _circle.Child = _text;
        Content = _circle;
        Update();
    }

    public static readonly DependencyProperty InitialsProperty =
        DependencyProperty.Register(nameof(Initials), typeof(string), typeof(Avatar), new PropertyMetadata("", (d, _) => ((Avatar)d).Update()));
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(string), typeof(Avatar), new PropertyMetadata("#C9CED6", (d, _) => ((Avatar)d).Update()));
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(40.0, (d, _) => ((Avatar)d).Update()));

    public string Initials { get => (string)GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }
    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private void Update()
    {
        _circle.Width = _circle.Height = Size;
        _circle.CornerRadius = new CornerRadius(Size / 2);
        _circle.Background = Ui.Brush(Color);
        _text.Text = Initials;
        _text.FontSize = Size <= 28 ? 11 : Size <= 32 ? 12 : 14;
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject d, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(d, name);
}
