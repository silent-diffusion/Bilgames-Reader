using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Bilgames.App;

public sealed partial class MainWindow : Window
{
    // Breakpoints on the window's width in effective pixels (design/tokens.json).
    private const double CompactMax = 640;
    private const double MediumMax = 1008;

    public ShellViewModel Vm { get; }

    public MainWindow()
    {
        Vm = new ShellViewModel();
        InitializeComponent();

        LibraryPane.Shell = Vm;
        BookPane.Shell = Vm;
        SettingsPane.Shell = Vm;
        NotesPane.Shell = Vm;
        PlayerPane.Shell = Vm;

        SetupWindow();
        Vm.LayoutChanged += ApplyLayout;
        Root.SizeChanged += (_, e) => UpdateLayoutClass(e.NewSize.Width);
        Root.PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;
        Root.Loaded += async (_, _) =>
        {
            Vm.Start();
            UpdateLayoutClass(Root.ActualWidth);
            ApplyLayout();
            if (_pendingImports is { } pending) { _pendingImports = null; await ImportAll(pending); }
            await AutoCheckForUpdates();
        };
    }

    private void SetupWindow()
    {
        var appWindow = AppWindow;
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // Open at a comfortable desktop size (in effective pixels), never larger than the screen.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1360 * scale), work.Width - 40);
        int h = Math.Min((int)(860 * scale), work.Height - 40);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 360;
            p.PreferredMinimumHeight = 560;
        }

        // Dark title bar matching the navigation background.
        var tb = appWindow.TitleBar;
        var nav = ColorHelper.FromArgb(255, 0x0B, 0x0E, 0x12);
        var hover = ColorHelper.FromArgb(255, 0x1F, 0x24, 0x2D);
        tb.BackgroundColor = nav;
        tb.InactiveBackgroundColor = nav;
        tb.ForegroundColor = ColorHelper.FromArgb(255, 0xEC, 0xEE, 0xF1);
        tb.InactiveForegroundColor = ColorHelper.FromArgb(255, 0x9A, 0xA3, 0xAF);
        tb.ButtonBackgroundColor = nav;
        tb.ButtonInactiveBackgroundColor = nav;
        tb.ButtonForegroundColor = ColorHelper.FromArgb(255, 0xEC, 0xEE, 0xF1);
        tb.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 0x6B, 0x74, 0x82);
        tb.ButtonHoverBackgroundColor = hover;
        tb.ButtonHoverForegroundColor = ColorHelper.FromArgb(255, 0xEC, 0xEE, 0xF1);
        tb.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 0x2A, 0x30, 0x3A);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        Activate();
    }

    private void UpdateLayoutClass(double width)
    {
        if (width <= 0) return;
        Vm.Layout = width < CompactMax ? LayoutClass.Compact : width < MediumMax ? LayoutClass.Medium : LayoutClass.Expanded;
    }

    /// <summary>Re-arranges navigation and panes for the current size class. State is untouched.</summary>
    private void ApplyLayout()
    {
        var layout = Vm.Layout;
        bool compact = layout == LayoutClass.Compact, medium = layout == LayoutClass.Medium, expanded = layout == LayoutClass.Expanded;

        // Navigation host placement
        NavColumn.Width = new GridLength(compact ? 0 : medium ? 92 : 248);
        Grid.SetRow(NavHost, compact ? 1 : 0);
        Grid.SetRowSpan(NavHost, compact ? 1 : 2);
        Grid.SetColumn(NavHost, 0);
        Grid.SetColumnSpan(NavHost, compact ? 2 : 1);
        NavHost.BorderThickness = compact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 1, 0);
        NavLayout.Padding = compact ? new Thickness(8, 6, 8, 12) : medium ? new Thickness(8, 20, 8, 20) : new Thickness(14, 22, 14, 22);
        NavLayout.RowSpacing = compact ? 0 : 20;
        Brand.Visibility = Ui.Vis(expanded);
        NavFooter.Visibility = Ui.Vis(!compact);
        StorageCard.Visibility = Ui.Vis(expanded);

        // Nav items: icon over label (compact/medium) or icon + label in a row (expanded).
        var items = new (SegmentToggle Button, StackPanel Content)[]
        {
            (NavLibrary, NavLibraryContent), (NavListening, NavListeningContent), (NavNotes, NavNotesContent), (NavVoices, NavVoicesContent), (NavJobs, NavJobsContent),
        };
        NavItems.ColumnDefinitions.Clear();
        NavItems.RowDefinitions.Clear();
        for (int i = 0; i < items.Length; i++)
        {
            if (compact) NavItems.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            else NavItems.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(items[i].Button, compact ? i : 0);
            Grid.SetRow(items[i].Button, compact ? 0 : i);
        }
        NavItems.RowSpacing = compact ? 0 : 6;
        foreach (var (button, content) in items.Append((NavSettings, NavSettingsContent)))
        {
            StyleNavItem(button, content, layout);
        }

        // Main area placement
        Grid.SetColumn(MainArea, compact ? 0 : 1);
        Grid.SetColumnSpan(MainArea, compact ? 2 : 1);
        Grid.SetRow(MainArea, 0);
        Grid.SetRowSpan(MainArea, compact ? 1 : 2);

        // Pane visibility and widths
        bool lib = Vm.ShowLibrary, notes = Vm.ShowNotesPage, settings = Vm.ShowSettings, book = Vm.ShowBook && !settings && !notes, player = Vm.ShowPlayer;
        if (medium && (settings || notes)) lib = true;
        LibraryPane.Visibility = Ui.Vis(lib);
        BookPane.Visibility = Ui.Vis(book);
        SettingsPane.Visibility = Ui.Vis(settings);
        NotesPane.Visibility = Ui.Vis(notes);
        PlayerPane.Visibility = Ui.Vis(player);
        LibraryColumn.Width = lib ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BookColumn.Width = book || settings || notes ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PlayerColumn.Width = !player ? new GridLength(0) : expanded ? new GridLength(360) : new GridLength(1, GridUnitType.Star);

        LibraryPane.ApplyLayout(layout, showRightBorder: medium);
        BookPane.ApplyLayout(layout);
        SettingsPane.ApplyLayout(layout);
        NotesPane.ApplyLayout(layout);
        PlayerPane.ApplyLayout(layout);
    }

    private static void StyleNavItem(SegmentToggle button, StackPanel content, LayoutClass layout)
    {
        bool expanded = layout == LayoutClass.Expanded;
        content.Orientation = expanded ? Orientation.Horizontal : Orientation.Vertical;
        content.Spacing = expanded ? 12 : 3;
        content.HorizontalAlignment = expanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        button.HorizontalContentAlignment = expanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        button.Padding = expanded ? new Thickness(14, 0, 14, 0) : new Thickness(4, 6, 4, 6);
        button.Background = layout == LayoutClass.Compact ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent) : Ui.Brush("#1A1F27");
        var label = (TextBlock)content.Children[1];
        label.FontSize = expanded ? 14 : 11;
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        label.HorizontalAlignment = expanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        var icon = (StrokeIcon)content.Children[0];
        icon.HorizontalAlignment = expanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
    }

    // ---------- keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.K)
        {
            if (!Vm.IsExpanded || !Vm.ShowLibrary) Vm.GoLibrary();
            LibraryPane.FocusSearch();
            e.Handled = true;
            return;
        }
        if (focused is TextBox or PasswordBox or AutoSuggestBox or RichEditBox) return;
        if (focused is ComboBox { IsDropDownOpen: true }) return;

        switch (e.Key)
        {
            case VirtualKey.Space:
                // A keyboard user pressing a focused button gets the button; otherwise Space plays/pauses.
                if (focused is ButtonBase { FocusState: FocusState.Keyboard } or ToggleSwitch or CheckBox { FocusState: FocusState.Keyboard }) return;
                Vm.Player.TogglePlay();
                e.Handled = true;
                break;
            case VirtualKey.Left when focused is not Slider:
                Vm.Player.Back();
                e.Handled = true;
                break;
            case VirtualKey.Right when focused is not Slider:
                Vm.Player.Forward();
                e.Handled = true;
                break;
            case VirtualKey.B when Vm.Player.HasAudio:
                Vm.Player.AddBookmark();
                e.Handled = true;
                break;
            case VirtualKey.H when Vm.Player.HasAudio:
                Vm.Player.HighlightCurrent();
                e.Handled = true;
                break;
            case (VirtualKey)219: // [
                Vm.Player.SpeedStep(-1);
                e.Handled = true;
                break;
            case (VirtualKey)221: // ]
                Vm.Player.SpeedStep(+1);
                e.Handled = true;
                break;
        }
    }

    // ---------- lifecycle ----------

    private List<string>? _pendingImports;

    /// <summary>Imports EPUBs passed on the command line / "Open with" once the UI is ready.</summary>
    public void ImportOnStart(List<string> paths)
    {
        if (paths.Count == 0) return;
        if (!Root.IsLoaded) { _pendingImports = paths; return; }
        _ = ImportAll(paths);
    }

    private async Task ImportAll(List<string> paths)
    {
        foreach (var p in paths) await Vm.Library.ImportPathAsync(p);
    }

    private async Task AutoCheckForUpdates()
    {
        // Opt-in only (off by default); enabling it was confirmed in Settings.
        if (!AppServices.Settings.AutoCheckForUpdates || !AppServices.Updates.CanUpdate) return;
        await Task.Delay(TimeSpan.FromSeconds(4));
        await Vm.Settings.RunUpdateCheck(fromLaunch: true);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            Vm.Player.SavePosition(force: true);
            Vm.Remember();
        }
        finally
        {
            AppServices.Shutdown();
        }
    }
}
