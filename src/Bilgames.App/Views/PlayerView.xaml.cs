using Bilgames.App.Controls;
using Bilgames.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Bilgames.App.Views;

public sealed partial class PlayerView : UserControl
{
    private ShellViewModel? _shell;

    public PlayerView()
    {
        InitializeComponent();
        // Seek on release (or on keyboard/click), not continuously while dragging.
        SetupTranscript();
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
        set
        {
            _shell = value;
            Bindings.Update();
            value.Player.PropertyChanged += (_, e) =>
            {
                // Let the list lay out first, then bring the paragraph being read into view.
                if (e.PropertyName == nameof(PlayerViewModel.CurrentParagraph))
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FollowReading(animate: true));
            };
            value.Player.Transcript.CollectionChanged += (_, e) =>
            {
                // A new chapter starts at the top, following the reading.
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                {
                    _lastUserScroll = DateTime.MinValue;
                    _programmaticScroll = true;
                    TranscriptScroller.ChangeView(null, 0, null, true);
                }
            };
        }
    }

    // ---------- transcript: tap to jump, free scroll, snap back after 5 s idle ----------

    private static readonly TimeSpan IdleBeforeFollow = TimeSpan.FromSeconds(5);
    private DateTime _lastUserScroll = DateTime.MinValue;
    private bool _programmaticScroll;
    private DispatcherTimer? _followTimer;

    private void SetupTranscript()
    {
        TranscriptList.ElementPrepared += (_, e) =>
        {
            if (e.Element is LineContextView v) { v.LineTapped -= OnLineTapped; v.LineTapped += OnLineTapped; }
        };
        TranscriptList.ElementClearing += (_, e) =>
        {
            if (e.Element is LineContextView v) v.LineTapped -= OnLineTapped;
        };
        // Any reader-driven scrolling (wheel, touch, scrollbar, keys) pauses auto-follow.
        TranscriptScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => UserScrolled()), true);
        TranscriptScroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => UserScrolled()), true);
        TranscriptScroller.DirectManipulationStarted += (_, _) => UserScrolled();
        TranscriptScroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) _programmaticScroll = false;
        };
        TranscriptScroller.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown)
                UserScrolled();
        };
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _followTimer.Tick += (_, _) =>
        {
            if (_lastUserScroll != DateTime.MinValue && DateTime.UtcNow - _lastUserScroll >= IdleBeforeFollow)
            {
                _lastUserScroll = DateTime.MinValue;
                FollowReading(animate: true);
            }
        };
        _followTimer.Start();
    }

    private void UserScrolled()
    {
        if (!_programmaticScroll) _lastUserScroll = DateTime.UtcNow;
    }

    private void OnLineTapped(ContextRun run)
    {
        Vm.JumpToLine(run.LineId);
        _lastUserScroll = DateTime.MinValue; // tapping means "read from here": follow again
    }

    /// <summary>Keeps the paragraph being read in view, unless the reader scrolled within the last 5 s.</summary>
    private void FollowReading(bool animate)
    {
        if (Vm is null || _lastUserScroll != DateTime.MinValue && DateTime.UtcNow - _lastUserScroll < IdleBeforeFollow) return;
        int i = Vm.CurrentParagraph;
        if (i < 0) return;
        var element = TranscriptList.GetOrCreateElement(i);
        element.UpdateLayout();
        _programmaticScroll = true;
        // Start of the paragraph at the top (with a little of the previous one showing above it).
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = 28, AnimationDesired = animate });
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
            var item = new ToggleMenuFlyoutItem { Text = c.Title + (c.IsReady ? "" : "  ·  plays as it's generated"), IsChecked = c.IsCurrent };
            var chapter = c;
            item.Click += (_, _) => Vm.GoToChapterCommand.Execute(chapter);
            flyout.Items.Add(item);
        }
        flyout.ShowAt((FrameworkElement)sender);
    }
}
