using Bilgames.App.Controls;
using Bilgames.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bilgames.App.Views;

/// <summary>Every bookmark, highlight and note across the library.</summary>
public sealed partial class NotesView : UserControl
{
    private ShellViewModel? _shell;

    public NotesView() => InitializeComponent();

    public ShellViewModel Shell
    {
        get => _shell!;
        set { _shell = value; Bindings.Update(); }
    }

    public NotesViewModel Vm => _shell?.Notes!;

    public void ApplyLayout(LayoutClass layout)
    {
        Body.Padding = layout == LayoutClass.Compact ? new Thickness(20, 24, 20, 16) : new Thickness(28, 28, 28, 24);
        BackButton.Visibility = Ui.Vis(layout == LayoutClass.Compact);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Shell.GoLibrary();

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GlobalNoteItem item) await Vm.OpenAsync(item);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GlobalNoteItem item) await Vm.EditNoteAsync(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GlobalNoteItem item) Vm.Delete(item);
    }
}
