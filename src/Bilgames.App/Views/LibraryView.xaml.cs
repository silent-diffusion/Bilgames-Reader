using Bilgames.App.Controls;
using Bilgames.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bilgames.App.Views;

public sealed partial class LibraryView : UserControl
{
    private ShellViewModel? _shell;

    public LibraryView() => InitializeComponent();

    public ShellViewModel Shell
    {
        get => _shell!;
        set { _shell = value; Bindings.Update(); }
    }

    public LibraryViewModel Vm => _shell?.Library!;

    public void ApplyLayout(LayoutClass layout, bool showRightBorder)
    {
        bool compact = layout == LayoutClass.Compact, expanded = layout == LayoutClass.Expanded;
        Body.Padding = compact ? new Thickness(20, 24, 20, 16) : new Thickness(28, 28, 28, 24);
        Frame.BorderThickness = new Thickness(0, 0, showRightBorder ? 1 : 0, 0);
        SearchBox.Visibility = Ui.Vis(expanded);
        SettingsButton.Visibility = Ui.Vis(compact);
        ListRepeater.Visibility = Ui.Vis(!expanded);
        GridRepeater.Visibility = Ui.Vis(expanded);
        // On narrow panes the actions wrap under the title.
        bool stack = compact || layout == LayoutClass.Medium;
        Grid.SetColumn(Actions, stack ? 0 : 1);
        Grid.SetRow(Actions, stack ? 1 : 0);
        if (Header.RowDefinitions.Count == 0)
        {
            Header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        Actions.HorizontalAlignment = HorizontalAlignment.Left;
        if (!stack) Actions.HorizontalAlignment = HorizontalAlignment.Right;
    }

    public void FocusSearch()
    {
        if (SearchBox.Visibility == Visibility.Visible) SearchInput.Focus(FocusState.Programmatic);
    }

    private void Book_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BookItemViewModel item) Shell.OpenBook(item.Id);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.ContinueItem is { } item) Shell.OpenPlayer(item.Id);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Shell.GoSettings();

    public Visibility HasContinue(BookItemViewModel? item) => Ui.Vis(item is not null);

    public string PercentOf(double fraction) => $"{Math.Round(fraction * 100)}%";

    public Visibility NoMatches(int count, string search) => Ui.Vis(count == 0 && !string.IsNullOrWhiteSpace(search));
}
