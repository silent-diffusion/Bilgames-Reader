using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.App.ViewModels;
using Bilgames.Core.Model;
using Bilgames.Core.Tts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Views;

public sealed partial class BookView : UserControl
{
    private ShellViewModel? _shell;

    public BookView()
    {
        InitializeComponent();
        // Content keeps its design max width but stays left-aligned (like CSS max-width), so give each
        // section an explicit width = min(available, max).
        Scroller.SizeChanged += (_, _) => SizeSections();
    }

    private void SizeSections()
    {
        double available = Scroller.ActualWidth - Body.Padding.Left - Body.Padding.Right;
        if (available <= 0) return;
        foreach (var (el, max) in new (FrameworkElement, double)[] { (TabStrip, 560), (StructureSection, 760), (CastSection, 900), (ReviewSection, 680), (GenerateSection, 760), (QualityTrack, 480) })
            el.Width = Math.Min(available, max);
    }

    public ShellViewModel Shell
    {
        get => _shell!;
        set { _shell = value; Bindings.Update(); }
    }

    public BookViewModel Vm => _shell?.Book!;

    public void ApplyLayout(LayoutClass layout)
    {
        Body.Padding = layout == LayoutClass.Compact ? new Thickness(20, 24, 20, 16) : new Thickness(28, 28, 28, 24);
        BackButton.Visibility = Ui.Vis(layout == LayoutClass.Compact);
        SizeSections();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Shell.GoLibrary();

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Tapping anywhere on a checklist row toggles it (the checkbox handles its own taps).
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return;
        if ((sender as FrameworkElement)?.Tag is SectionRow row) row.IsOn = !row.IsOn;
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SpeakerOptionViewModel o) Vm.Review.PickCommand.Execute(o);
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileOptionViewModel p) Vm.Generate.PickProfileCommand.Execute(p);
    }

    private void Preview_Voice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CastMemberViewModel m) Vm.Cast.PreviewCommand.Execute(m);
    }

    /// <summary>Voice picker: every installed voice, grouped by accent, current one checked.</summary>
    private void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not CastMemberViewModel member) return;
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var group in VoiceCatalog.All.GroupBy(v => (v.Accent, v.Gender)))
        {
            var sub = new MenuFlyoutSubItem { Text = $"{group.Key.Accent} {(group.Key.Gender == Gender.Female ? "female" : "male")}" };
            foreach (var v in group)
            {
                var item = new ToggleMenuFlyoutItem { Text = $"{v.DisplayName}  ·  grade {v.Grade}", IsChecked = v.Id == member.VoiceId };
                var id = v.Id;
                item.Click += (_, _) => Vm.Cast.SetVoice(member, id);
                sub.Items.Add(item);
            }
            flyout.Items.Add(sub);
        }
        flyout.ShowAt(button);
    }

    /// <summary>"Preview extracted text": exactly what will be read, chapter by chapter.</summary>
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var chapters = Vm.Structure.PreviewChapters;
        if (chapters.Count == 0) return;
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = chapters.Select(c => $"{c.Title} · {KindName(c.Kind)} · {c.WordCount:N0} words").ToList() };
        var text = new StackPanel { Spacing = 12 };
        var scroller = new ScrollViewer { Content = text, Height = 420, Padding = new Thickness(0, 0, 12, 0) };
        var panel = new StackPanel { Spacing = 12, Width = 560, Children = { picker, scroller } };

        void Show(int index)
        {
            text.Children.Clear();
            var ch = Vm.Structure.LoadText(chapters[index].Index);
            if (ch is null) return;
            foreach (var b in ch.Blocks.Take(400))
            {
                if (b.Kind == BlockKind.SceneBreak) { text.Children.Add(new TextBlock { Text = "* * *", HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"] }); continue; }
                text.Children.Add(new TextBlock
                {
                    Text = b.Text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = (FontFamily)Application.Current.Resources["BookFont"],
                    FontSize = b.Kind == BlockKind.Heading ? 18 : 15,
                    FontWeight = b.Kind == BlockKind.Heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    LineHeight = 23,
                });
            }
            foreach (var n in ch.Notes)
                text.Children.Add(new TextBlock { Text = "Note: " + n.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"] });
            scroller.ChangeView(null, 0, null, true);
        }

        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0) Show(picker.SelectedIndex); };
        var first = chapters.ToList().FindIndex(c => c.Kind == SectionKind.Body);
        picker.SelectedIndex = Math.Max(0, first);
        await Dialogs.ShowContentAsync("Extracted text", panel, "", "Close");
    }

    private static string KindName(SectionKind k) => k switch
    {
        SectionKind.FrontMatter => "front matter",
        SectionKind.Preface => "preface",
        SectionKind.BackMatter => "back matter",
        _ => "main text",
    };

    /// <summary>While generating, Pause is secondary and Listen is primary; when idle, Generate leads.</summary>
    public Style JobButtonStyle(bool running, bool complete) =>
        (Style)Application.Current.Resources[running || complete ? "GhostButton" : "AccentButton"];

    public Style ListenButtonStyle(bool running, bool complete) =>
        (Style)Application.Current.Resources[running || complete ? "AccentButton" : "GhostButton"];

    public string LinesText(int count) => $"{count:N0} {(count == 1 ? "line" : "lines")}";

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
