using System.Diagnostics;
using PhoneLinkPC.Core.Abstractions;

namespace PhoneLinkPC.Infrastructure;

/// <summary>
/// Opens the operating system's own Bluetooth settings.
///
/// PhoneLink PC deliberately does not pair devices itself: pairing, PIN confirmation and
/// the profile selection belong to the operating system, and a third-party app cannot
/// complete them. The app only reads the result. This helper makes that step reachable
/// instead of leaving the user in a dead end.
/// </summary>
public static class SystemSettings
{
    /// <summary>
    /// Opens Windows' own "Add a device" dialog straight on the Bluetooth step.
    ///
    /// Measured on 2026-09-10: that dialog finds phones our in-app inquiry misses, because
    /// Windows keeps scanning while it is open and puts the PC into discoverable mode at the
    /// same time. Using it is simply the shorter path for the user.
    ///
    /// DevicePairingWizard.exe is tried first because it is a real executable: starting it
    /// either throws or yields a live process, which is proof that a window exists. The
    /// ms-settings URIs cannot be checked at all - ShellExecute reports success for any
    /// registered protocol, whether or not the page behind it opens. On Windows 11 build
    /// 26200 "ms-settings-connectabledevices:devicediscovery" did exactly that: it reported
    /// success while nothing appeared, so the app announced an open pairing dialog, the PC
    /// never went discoverable, and the phone kept showing no PC to pair with.
    /// </summary>
    public static TelephonyResult OpenAddDeviceDialog()
    {
        if (!OperatingSystem.IsWindows())
            return TelephonyResult.Fail("Dieser Dialog gibt es nur unter Windows.");

        var wizard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "DevicePairingWizard.exe");

        if (File.Exists(wizard))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(wizard) { UseShellExecute = true });

                // A wizard that exits within the first moment never showed a window. Waiting
                // this briefly is invisible to the user and keeps the success claim honest.
                if (process is not null && !process.WaitForExit(TimeSpan.FromMilliseconds(600)))
                    return TelephonyResult.Ok();
            }
            catch
            {
                // Fall through to the settings page.
            }
        }

        // Fallback. This one cannot be verified, so it does not claim a pairing dialog -
        // the caller tells the user to start the search there themselves.
        try
        {
            Start("ms-settings:bluetooth");
            return SettingsPageOnly;
        }
        catch
        {
            return TelephonyResult.Fail(
                "Der Kopplungsdialog konnte nicht geöffnet werden. Bitte die "
                + "Windows-Einstellungen -> Bluetooth und Geräte von Hand öffnen.");
        }
    }

    /// <summary>
    /// The pairing wizard was unavailable and only the Bluetooth settings page was opened.
    /// Reported as a failure because the promised dialog is not on screen - the user still
    /// has to press "Gerät hinzufügen" themselves, and the PC only becomes discoverable
    /// once they do.
    /// </summary>
    public static readonly TelephonyResult SettingsPageOnly = TelephonyResult.Fail(
        "Der Kopplungsassistent liess sich nicht starten. Die Bluetooth-Einstellungen sind "
        + "offen - dort auf \"Gerät hinzufügen\" -> \"Bluetooth\" klicken. Erst dann ist "
        + "dieser PC für das Telefon sichtbar.");

    public static TelephonyResult OpenBluetoothSettings()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Start("ms-settings:bluetooth");
                return TelephonyResult.Ok();
            }

            if (OperatingSystem.IsLinux())
            {
                // Desktop environments differ; try the common ones in order.
                foreach (var (file, args) in new[]
                         {
                             ("gnome-control-center", "bluetooth"),
                             ("systemsettings", "kcm_bluetooth"),
                             ("blueman-manager", ""),
                             ("xdg-open", "bluetooth://")
                         })
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
                        return TelephonyResult.Ok();
                    }
                    catch
                    {
                        // Try the next desktop environment.
                    }
                }

                return TelephonyResult.Fail(
                    "Bluetooth-Einstellungen konnten nicht geöffnet werden. "
                    + "Bitte manuell öffnen (z. B. blueman-manager).");
            }

            return TelephonyResult.Fail("Diese Plattform wird nicht unterstützt.");
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Bluetooth-Einstellungen konnten nicht geöffnet werden: {ex.Message}");
        }
    }

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
