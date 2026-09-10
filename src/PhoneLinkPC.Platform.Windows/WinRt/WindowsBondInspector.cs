using PhoneLinkPC.Core.Bluetooth;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace PhoneLinkPC.Platform.Windows;

/// <summary>
/// Reads the real bond state of this PC, separately for each Bluetooth radio.
///
/// Every other telephony query on Windows filters for Bluetooth Classic, because Classic
/// is the only transport that carries HFP. A phone bonded over LE only therefore vanishes
/// from all of them, and the app could say no more than "no device paired" - while the
/// phone sat in the Windows Bluetooth list looking perfectly connected.
///
/// Measured on 2026-09-10 with a Samsung S26 Ultra: at address aa:bb:cc:dd:ee:ff the
/// Classic endpoint reported IsPaired=False and the LE endpoint IsPaired=True. See
/// docs/WINDOWS_TELEPHONY.md. This class is the one place that looks at both, so the
/// difference can be named instead of silently swallowed.
/// </summary>
internal static class WindowsBondInspector
{
    /// <summary>
    /// Both lists come from the paired-state device selectors rather than from an
    /// association endpoint query.
    ///
    /// The endpoint query returns the same facts and additionally sees unpaired devices,
    /// but it runs a Bluetooth inquiry and takes a fixed ~30 seconds - measured on
    /// 2026-09-10, which is what made starting the app feel hung. These selectors answer
    /// in well under a tenth of a second, and a bond that does not exist yet is the
    /// device search's job to find, not this one's.
    /// </summary>
    public static async Task<IReadOnlyList<BluetoothEndpoint>> ListEndpointsAsync(CancellationToken ct = default)
    {
        var endpoints = new List<BluetoothEndpoint>();

        var classic = await DeviceInformation
            .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))
            .AsTask(ct).ConfigureAwait(false);

        foreach (var info in classic)
        {
            ct.ThrowIfCancellationRequested();

            ulong? address = null;
            var connected = false;
            var isPhone = false;

            try
            {
                using var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
                if (device is not null)
                {
                    address = device.BluetoothAddress;
                    connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    isPhone = device.ClassOfDevice.MajorClass == BluetoothMajorClass.Phone;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The device record exists but the radio cannot reach it right now. The
                // bond is still a fact, so the endpoint is reported without the details.
            }

            endpoints.Add(new BluetoothEndpoint
            {
                Id = info.Id,
                Address = Format(address),
                Name = NameOf(info),
                Transport = BluetoothTransport.Classic,

                // Membership in this list is the pairing fact. The DeviceInformation of a
                // device interface carries its own Pairing object, and that one reports
                // IsPaired=False even for a bonded device - reading it would invert the
                // whole diagnosis.
                IsPaired = true,
                IsConnected = connected,
                IsPhone = isPhone
            });
        }

        var lowEnergy = await DeviceInformation
            .FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))
            .AsTask(ct).ConfigureAwait(false);

        foreach (var info in lowEnergy)
        {
            ct.ThrowIfCancellationRequested();

            ulong? address = null;
            var connected = false;

            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
                if (device is not null)
                {
                    address = device.BluetoothAddress;
                    connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same as above: the bond is reported even when the device is unreachable.
            }

            endpoints.Add(new BluetoothEndpoint
            {
                Id = info.Id,
                Address = Format(address),
                Name = NameOf(info),
                Transport = BluetoothTransport.LowEnergy,
                IsPaired = true,
                IsConnected = connected,
                IsPhone = await IsPhoneAsync(address, ct).ConfigureAwait(false)
            });
        }

        // An LE endpoint carries no Class of Device of its own. Without carrying the
        // classification across by address, a phone bonded over LE only would be
        // indistinguishable from a sensor that is meant to be LE-only.
        var phoneAddresses = endpoints
            .Where(e => e.IsPhone)
            .Select(e => e.NormalizedAddress)
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < endpoints.Count; i++)
            if (!endpoints[i].IsPhone && phoneAddresses.Contains(endpoints[i].NormalizedAddress))
                endpoints[i] = endpoints[i] with { IsPhone = true };

        return endpoints;
    }

    public static async Task<BondDiagnosis> DiagnoseAsync(CancellationToken ct = default) =>
        BondAnalyzer.Analyze(await ListEndpointsAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Asks the Classic side what kind of device sits at this address. Works even when
    /// only an LE bond exists, which is precisely the case that needs classifying.
    /// </summary>
    private static async Task<bool> IsPhoneAsync(ulong? address, CancellationToken ct)
    {
        if (address is null or 0) return false;

        try
        {
            using var device = await BluetoothDevice
                .FromBluetoothAddressAsync(address.Value).AsTask(ct).ConfigureAwait(false);

            return device?.ClassOfDevice.MajorClass == BluetoothMajorClass.Phone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string NameOf(DeviceInformation info) =>
        string.IsNullOrWhiteSpace(info.Name) ? "(unbenanntes Gerät)" : info.Name;

    /// <summary>Formats an address the way Windows shows it, so it can be compared by eye.</summary>
    private static string Format(ulong? address)
    {
        if (address is null or 0) return string.Empty;

        var hex = address.Value.ToString("X12");
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)))
            .ToLowerInvariant();
    }
}
