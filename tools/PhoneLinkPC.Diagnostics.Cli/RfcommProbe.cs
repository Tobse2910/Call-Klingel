using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PhoneLinkPC.Diagnostics.Cli;

/// <summary>
/// Feasibility probe for plan B: talk HFP ourselves.
///
/// Instead of asking Windows for a phone line - which Microsoft disabled for third-party
/// apps in Windows 11 22H2 - the PC opens an RFCOMM channel to the phone's Hands-Free
/// Audio Gateway (UUID 0x111F) and speaks the HFP AT protocol directly.
///
/// This probe answers one question: can we open that channel at all, while Windows'
/// own Hands-Free service is bound to the same phone?
/// </summary>
internal static class RfcommProbe
{
    private static readonly Guid HandsFreeAudioGateway = new("0000111f-0000-1000-8000-00805f9b34fb");

    public static async Task RunAsync(Action<string> log, int listenSeconds)
    {
        log("=== RFCOMM-Machbarkeitstest (HFP selbst sprechen) ===");

        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        log($"Gekoppelte Geräte: {paired.Count}");

        BluetoothDevice? phone = null;
        foreach (var d in paired)
        {
            var dev = await BluetoothDevice.FromIdAsync(d.Id);
            if (dev?.ClassOfDevice.MajorClass == BluetoothMajorClass.Phone) { phone = dev; break; }
        }

        if (phone is null) { log("ABBRUCH: kein Telefon gefunden."); return; }
        log($"Telefon: {phone.Name}  (Status {phone.ConnectionStatus})");

        RfcommDeviceServicesResult services;
        try
        {
            services = await phone.GetRfcommServicesForIdAsync(
                RfcommServiceId.FromUuid(HandsFreeAudioGateway), BluetoothCacheMode.Uncached);
            log($"HFP-AG-Dienste gefunden: {services.Services.Count}");
        }
        catch (Exception ex) { log($"Dienstsuche FEHLER: {Describe(ex)}"); return; }

        if (services.Services.Count == 0)
        {
            log("ABBRUCH: Telefon bietet kein Hands-Free Audio Gateway an.");
            return;
        }

        var service = services.Services[0];
        log($"Dienst-Id: {service.ConnectionServiceName}  Host: {service.ConnectionHostName}");

        using var socket = new StreamSocket();
        try
        {
            // SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication is what
            // paired-device RFCOMM normally needs.
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
            log("*** RFCOMM-VERBINDUNG HERGESTELLT ***");
        }
        catch (Exception ex)
        {
            log($"RFCOMM-Verbindung FEHLGESCHLAGEN: {Describe(ex)}");
            log("");
            log("Häufigste Ursache: Windows hat den HFP-Kanal bereits selbst belegt.");
            log("Gegenprobe: in den Bluetooth-Geräteeigenschaften den Dienst");
            log("'Freisprechtelefonie' deaktivieren und erneut testen.");
            return;
        }

        // Speak the HFP handshake. If the phone answers, plan B is viable.
        try
        {
            var writer = new DataWriter(socket.OutputStream);
            var reader = new DataReader(socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial
            };

            async Task SendAsync(string command)
            {
                log($"  >> {command}");
                writer.WriteString(command + "\r");
                await writer.StoreAsync();
            }

            // Service Level Connection handshake, as defined by the HFP spec.
            await SendAsync("AT+BRSF=871");

            var deadline = DateTime.Now.AddSeconds(listenSeconds);
            var buffer = new StringBuilder();

            while (DateTime.Now < deadline)
            {
                var loaded = await reader.LoadAsync(256);
                if (loaded == 0) { await Task.Delay(100); continue; }

                var chunk = reader.ReadString(loaded);
                buffer.Append(chunk);

                foreach (var raw in chunk.Split('\r', '\n'))
                {
                    var lineText = raw.Trim();
                    if (lineText.Length == 0) continue;
                    log($"  << {lineText}");

                    // Continue the handshake so the phone reaches the state where it
                    // reports calls via RING and +CLIP.
                    if (lineText.StartsWith("+BRSF")) await SendAsync("AT+CIND=?");
                    else if (lineText.StartsWith("+CIND: (")) await SendAsync("AT+CIND?");
                    else if (lineText.StartsWith("+CIND:")) await SendAsync("AT+CMER=3,0,0,1");
                    else if (lineText == "OK") { /* step done */ }
                }
            }

            await SendAsync("AT+CLIP=1");
            log("");
            log("Handshake beendet. Wenn oben +BRSF und +CIND stehen, spricht das Telefon mit uns.");
        }
        catch (Exception ex)
        {
            log($"AT-Kommunikation FEHLER: {Describe(ex)}");
        }
    }

    private static string Describe(Exception ex)
    {
        var e = ex is AggregateException a && a.InnerException is not null ? a.InnerException : ex;
        return $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message.Replace("\r", " ").Replace("\n", " ").Trim()}";
    }
}
