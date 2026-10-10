using Bilgames.App.Controls;
using Bilgames.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bilgames.App.Views;

public sealed partial class SettingsView : UserControl
{
    private ShellViewModel? _shell;

    public SettingsView() => InitializeComponent();

    public ShellViewModel Shell
    {
        get => _shell!;
        set { _shell = value; Bindings.Update(); }
    }

    public SettingsViewModel Vm => _shell?.Settings!;

    public void ApplyLayout(LayoutClass layout)
    {
        Body.Padding = layout == LayoutClass.Compact ? new Thickness(20, 24, 20, 16) : new Thickness(28, 28, 28, 24);
        BackButton.Visibility = Ui.Vis(layout == LayoutClass.Compact);
        if (Visibility == Visibility.Visible) Vm?.Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Shell.GoLibrary();

    /// <summary>Default narrator: the same Gender › Nationality voice menus as the Cast tab.</summary>
    private void Narrator_Click(object sender, RoutedEventArgs e) =>
        SearchPicker.Show((FrameworkElement)sender, ContextHelpers.VoiceItems(Vm.DefaultNarrator?.Id), "Search voices (e.g. British, male, Emma)", p => Vm.SetDefaultNarrator(p.Id),
            preview: async v => { if (await CastViewModel.EnsureModel()) await Shell.Player.PreviewAsync("The road wound north through the hills, and for a long while nobody spoke.", v.Id); });

    private void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ModelRowViewModel row) Vm.DownloadModelCommand.Execute(row);
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ModelRowViewModel row) Vm.DeleteModelCommand.Execute(row);
    }
}
