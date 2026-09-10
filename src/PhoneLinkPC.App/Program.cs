using Avalonia;
using PhoneLinkPC.Infrastructure.Logging;
using Velopack;

namespace PhoneLinkPC.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // No explicit directory: AppLog picks a writable location that also works under MSIX.
        AppLog.Initialize(debug: args.Contains("--debug"));

        // Must be the first thing that runs, before any window exists.
        //
        // The installer and the updater start this same executable with hook arguments, and
        // this call is what handles them: creating shortcuts on install, removing them on
        // uninstall, restarting into the new version after an update. If a window opens
        // first, the user sees the app flash up during installation and the hooks run late.
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            // A broken update hook must never stop the app from starting - the user still
            // has a working installed version, and it should come up.
            Serilog.Log.Warning(ex, "Velopack-Start konnte nicht ausgeführt werden");
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
