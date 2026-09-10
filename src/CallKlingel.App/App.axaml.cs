using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using CallKlingel.App.ViewModels;
using CallKlingel.App.Views;

namespace CallKlingel.App;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyMotionPreference();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Drops the animation styles when the system asks for reduced motion.
    ///
    /// Windows has a switch for this under Accessibility, and people who turn it off do so
    /// because movement makes them ill or unable to follow the screen - it is not a taste
    /// setting to be overridden. Removing the style file rather than shortening durations
    /// keeps it simple: the app is either fully animated or completely still.
    /// </summary>
    private void ApplyMotionPreference()
    {
        if (AnimationsWanted()) return;

        var motion = Styles.FirstOrDefault(
            s => s is StyleInclude include &&
                 include.Source?.ToString().EndsWith("Motion.axaml", StringComparison.Ordinal) == true);

        if (motion is not null) Styles.Remove(motion);
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action, uint param, ref bool value, uint update);

    private static bool AnimationsWanted()
    {
        // Only Windows exposes the setting here. Elsewhere animate, which is the platform
        // default, rather than guess at a preference that was never expressed.
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            var wanted = true;
            return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref wanted, 0) || wanted;
        }
        catch
        {
            return true;
        }
    }
}
