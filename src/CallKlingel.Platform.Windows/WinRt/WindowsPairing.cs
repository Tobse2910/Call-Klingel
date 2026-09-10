using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace CallKlingel.Platform.Windows;

/// <summary>
/// Bluetooth discovery and pairing from inside the app.
///
/// Windows does allow an app to pair devices through DeviceInformationPairing, so the user
/// never has to leave Call Klingel for the Windows Bluetooth settings. The confirmation on
/// the phone itself is still required - that is a security decision of the phone, and no
/// desktop app can or should bypass it.
/// </summary>
internal static class WindowsPairing
{
    /// <summary>Finds phones in range that are not paired yet.</summary>
    public static async Task<IReadOnlyList<PhoneDevice>> DiscoverAsync(
        TimeSpan duration, IProgress<PhoneDevice>? progress, CancellationToken ct)
    {
        var found = new Dictionary<string, PhoneDevice>(StringComparer.OrdinalIgnoreCase);
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);

        var watcher = DeviceInformation.CreateWatcher(
            selector,
            ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected"],
            DeviceInformationKind.AssociationEndpoint);

        watcher.Added += (_, info) =>
        {
            var device = new PhoneDevice
            {
                Id = info.Id,
                Name = string.IsNullOrWhiteSpace(info.Name) ? "(unbenanntes Gerät)" : info.Name,
                IsPaired = false,
                IsConnected = false
            };

            lock (found)
            {
                if (!found.TryAdd(info.Id, device)) return;
            }

            progress?.Report(device);
        };

        watcher.Start();
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Report whatever was discovered before the cancellation.
        }
        finally
        {
            try { watcher.Stop(); } catch { /* already stopped */ }
        }

        lock (found) return found.Values.ToArray();
    }

    /// <summary>
    /// Pairs the device. A custom ceremony is used so the confirmation can be accepted
    /// automatically on the PC side - the phone still asks its own user.
    /// </summary>
    public static async Task<TelephonyResult> PairAsync(
        string deviceId, IProgress<string>? status, CancellationToken ct)
    {
        DeviceInformation info;
        try
        {
            info = await DeviceInformation.CreateFromIdAsync(deviceId);
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Gerät nicht gefunden: {ex.Message}");
        }

        var pairing = info.Pairing;
        if (pairing is null) return TelephonyResult.Fail("Dieses Gerät unterstützt kein Koppeln.");

        if (pairing.IsPaired)
        {
            status?.Report("Bereits gekoppelt.");
            return TelephonyResult.Ok();
        }

        if (!pairing.CanPair) return TelephonyResult.Fail("Dieses Gerät lässt sich nicht koppeln.");

        var custom = pairing.Custom;
        void OnRequested(DeviceInformationCustomPairing _, DevicePairingRequestedEventArgs args)
        {
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    status?.Report("Bestätige die Kopplung am Telefon.");
                    args.Accept();
                    break;

                case DevicePairingKinds.ConfirmPinMatch:
                    // Both sides show the same number; the user compares them.
                    status?.Report($"Code am Telefon prüfen: {args.Pin}");
                    args.Accept();
                    break;

                default:
                    status?.Report($"Kopplungsart {args.PairingKind} wird nicht unterstützt.");
                    break;
            }
        }

        custom.PairingRequested += OnRequested;
        try
        {
            status?.Report("Koppeln läuft ...");

            // ProvidePin is deliberately not offered. Windows picks one of the ceremonies
            // listed here, and a phone that shows a comparison code is not asking for a PIN
            // to be typed on the PC - so accepting ProvidePin means guessing a number.
            // Measured on 2026-09-10 against a Samsung S26 Ultra: with ProvidePin in the
            // list Windows chose it, the app sent "0000", and PairAsync returned Failed
            // while the phone stood there waiting for a code comparison. Without it the
            // same phone pairs through ConfirmPinMatch.
            var result = await custom.PairAsync(
                DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch,
                DevicePairingProtectionLevel.Default);

            return result.Status switch
            {
                DevicePairingResultStatus.Paired => TelephonyResult.Ok(),
                DevicePairingResultStatus.AlreadyPaired => TelephonyResult.Ok(),
                DevicePairingResultStatus.RejectedByHandler =>
                    TelephonyResult.Fail("Die Kopplung wurde am Telefon abgelehnt."),
                DevicePairingResultStatus.AuthenticationTimeout =>
                    TelephonyResult.Fail("Zeitüberschreitung. Am Telefon die Bluetooth-Seite offen lassen."),
                DevicePairingResultStatus.ConnectionRejected =>
                    TelephonyResult.Fail("Das Telefon hat die Verbindung abgelehnt."),
                _ => TelephonyResult.Fail($"Koppeln fehlgeschlagen: {result.Status}")
            };
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Koppeln fehlgeschlagen (0x{ex.HResult:X8}): {ex.Message}");
        }
        finally
        {
            custom.PairingRequested -= OnRequested;
        }
    }

    public static async Task<TelephonyResult> UnpairAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(deviceId);
            if (info.Pairing is null || !info.Pairing.IsPaired) return TelephonyResult.Ok();

            var result = await info.Pairing.UnpairAsync();
            return result.Status is DeviceUnpairingResultStatus.Unpaired
                                 or DeviceUnpairingResultStatus.AlreadyUnpaired
                ? TelephonyResult.Ok()
                : TelephonyResult.Fail($"Trennen fehlgeschlagen: {result.Status}");
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Trennen fehlgeschlagen: {ex.Message}");
        }
    }
}
