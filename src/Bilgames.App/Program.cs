using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;

namespace Bilgames.App;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must run first: lets the installer/updater hook install, update and uninstall events.
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // One window only: a second launch brings the first one forward instead of playing twice.
        var instance = AppInstance.FindOrRegisterForKey("BilgamesReader.Main");
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
