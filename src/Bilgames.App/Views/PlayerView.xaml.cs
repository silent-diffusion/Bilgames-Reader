using Bilgames.App.Controls;
using Bilgames.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Bilgames.App.Views;

public sealed partial class PlayerView : UserControl
{
    private ShellViewModel? _shell;

    public PlayerView()
    {
        InitializeComponent();
        // Seek on release (or on keyboard/click), not continuously while dragging.
        Scrubber.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Vm?.BeginScrub()), true);
        Scrubber.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Vm?.EndScrub(Scrubber.Value)), true);
        Scrubber.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => Vm?.EndScrub(Scrubber.Value)), true);
        Scrubber.ValueChanged += (_, e) =>
        {
            // Keyboard arrows on the slider: a jump bigger than the 250 ms tick means the user moved it.
            if (Vm is null || Scrubber.FocusState != FocusState.Keyboard) return;
            if (Math.Abs(e.NewValue - Vm.Position) > 1.5) Vm.Seek(e.NewValue);
        };
    }

    public ShellViewModel Shell
    {
        get => _shell!;
        set { _shell = value; Bindings.Update(); }
    }

    public PlayerViewModel Vm => _shell?.Player!;

    public void ApplyLayout(LayoutClass layout)
    {
        bool expanded = layout == LayoutClass.Expanded, compact = layout == LayoutClass.Compact;
        Body.Padding = compact ? new Thickness(20, 24, 20, 16) : new Thickness(28, 28, 28, 24);
        Frame.BorderThickness = new Thickness(compact ? 0 : 1, 0, 0, 0);
        Frame.Background = expanded ? Ui.Brush("#12161C") : Ui.Brush("#0E1116");
        CollapseButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        KeyHint.Visibility = Ui.Vis(expanded);
    }

    private void Sleep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && int.TryParse(tag, out var minutes)) Vm.SetSleep(minutes);
    }

    private void Chapters_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        if (Vm.Chapters.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "No chapters yet", IsEnabled = false });
        foreach (var c in Vm.Chapters)
        {
            var item = new ToggleMenuFlyoutItem { Text = c.Title + (c.IsReady ? "" : "  ·  not generated"), IsChecked = c.IsCurrent, IsEnabled = c.IsReady };
            var chapter = c;
            item.Click += (_, _) => Vm.GoToChapterCommand.Execute(chapter);
            flyout.Items.Add(item);
        }
        flyout.ShowAt((FrameworkElement)sender);
    }
}
