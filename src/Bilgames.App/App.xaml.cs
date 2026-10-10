using Bilgames.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Bilgames.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppServices.Log("Unhandled: " + e.Exception);
            e.Handled = true; // keep listening even if one screen hits a bug
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppServices.Initialize();
        Controls.Palette.Initialize(AppServices.Settings.Theme);
        Controls.LineContextView.Scale = AppServices.Settings.ReadingTextScale;
        Window = new MainWindow();
        Window.Activate();
        Window.ImportOnStart(EpubArgs(Environment.GetCommandLineArgs().Skip(1)));

        // A second launch (e.g. double-clicking an .epub) is redirected here.
        AppInstance.GetCurrent().Activated += (_, e) => Window.DispatcherQueue.TryEnqueue(() =>
        {
            Window.BringToFront();
            Window.ImportOnStart(EpubArgs(ArgsOf(e)));
        });
    }

    private static IEnumerable<string> ArgsOf(AppActivationArguments e) => e.Kind switch
    {
        ExtendedActivationKind.Launch when e.Data is ILaunchActivatedEventArgs l => SplitArgs(l.Arguments),
        ExtendedActivationKind.File when e.Data is IFileActivatedEventArgs f => f.Files.Select(x => x.Path),
        _ => Array.Empty<string>(),
    };

    private static IEnumerable<string> SplitArgs(string args)
    {
        // Paths may be quoted; take quoted chunks or whitespace-separated tokens.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(args ?? "", "\"([^\"]+)\"|(\\S+)"))
            yield return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    private static List<string> EpubArgs(IEnumerable<string> args) =>
        args.Where(a => (a.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) || a.EndsWith(Core.Storage.LibraryBackup.PackageExtension, StringComparison.OrdinalIgnoreCase)) && File.Exists(a)).ToList();
}
