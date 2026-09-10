using CallKlingel.Core.Pbap;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace CallKlingel.Platform.Windows;

/// <summary>
/// OBEX transport to the phone's Phonebook Access Server over an RFCOMM socket.
///
/// The phone advertises the service both on RFCOMM channel 19 and over L2CAP (PSM 0x1005,
/// PBAP 1.2). RFCOMM is used because it is what WinRT can open - measured working on
/// 2026-09-10 with 571 contacts transferred.
/// </summary>
public sealed class RfcommObexTransport : IObexTransport
{
    /// <summary>Phonebook Access Server - reading the phone's contacts.</summary>
    public static readonly Guid PhonebookAccessServer = new("0000112f-0000-1000-8000-00805f9b34fb");

    /// <summary>Object Push - the only way to hand a contact to the phone.</summary>
    public static readonly Guid ObjectPush = new("00001105-0000-1000-8000-00805f9b34fb");

    private readonly Guid _serviceId;
    private readonly string _missingServiceMessage;

    public RfcommObexTransport(Guid serviceId, string missingServiceMessage)
    {
        _serviceId = serviceId;
        _missingServiceMessage = missingServiceMessage;
    }

    private StreamSocket? _socket;
    private DataWriter? _writer;
    private DataReader? _reader;

    public async Task ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Bluetooth-Gerät nicht erreichbar.");

        RfcommDeviceService? service = null;

        // Same wake-up dance as the hands-free transport: a sleeping link answers the first
        // service queries with nothing at all. Measured on 2026-09-10 - three rounds of
        // "0 Dienste" followed by a full list once the radio was awake.
        for (var attempt = 0; attempt < 4 && service is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);

            try
            {
                var all = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                service = all.Services.FirstOrDefault(s => s.ServiceId.Uuid == _serviceId);
            }
            catch
            {
                // Out of range or asleep; the next round may reach it.
            }
        }

        if (service is null) throw new InvalidOperationException(_missingServiceMessage);

        // A channel that was just used is not immediately reusable - sending two contacts
        // in a row failed on 2026-09-10 with 0x800704CF ("network not available"), which is
        // what Bluetooth reports while the previous channel is still being released. The
        // hands-free transport already retries for the same reason.
        StreamSocket? socket = null;
        Exception? lastError = null;
        double[] waits = [0.5, 1.0, 1.5, 2.0];

        for (var attempt = 0; attempt <= waits.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = new StreamSocket();
            try
            {
                await candidate.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                    .AsTask(ct).ConfigureAwait(false);

                socket = candidate;
                break;
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                lastError = ex;

                if (attempt < waits.Length)
                    await Task.Delay(TimeSpan.FromSeconds(waits[attempt]), ct).ConfigureAwait(false);
            }
        }

        if (socket is null)
            throw new InvalidOperationException(Describe(lastError), lastError);

        _socket = socket;
        _writer = new DataWriter(socket.OutputStream);
        _reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
    }

    /// <summary>
    /// Bluetooth reports channel problems as network errors, which tells the user nothing.
    /// The ones seen in practice get an explanation they can act on.
    /// </summary>
    private static string Describe(Exception? ex)
    {
        if (ex is null) return "Der Kanal zum Telefon liess sich nicht öffnen.";

        return (uint)ex.HResult switch
        {
            0x800704CF => "Der Kanal zum Telefon ist noch belegt. Nach einer Übertragung "
                          + "braucht Bluetooth ein paar Sekunden, bis er wieder frei ist.",
            0x80072740 => "Der Kanal wird gerade von einer anderen Übertragung genutzt.",
            0x80072743 or 0x80072742 => "Das Telefon ist gekoppelt, aber gerade nicht verbunden.",
            0x8007274C => "Das Telefon antwortet nicht. Zu weit weg oder im Ruhezustand.",
            _ => $"Der Kanal zum Telefon liess sich nicht öffnen ({ex.Message.Trim()})."
        };
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_writer is null) throw new InvalidOperationException("Nicht verbunden.");

        _writer.WriteBytes(data.ToArray());
        await _writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct = default)
    {
        if (_reader is null) throw new InvalidOperationException("Nicht verbunden.");

        var buffer = new byte[count];
        var filled = 0;

        while (filled < count)
        {
            ct.ThrowIfCancellationRequested();

            var loaded = await _reader.LoadAsync((uint)(count - filled)).AsTask(ct).ConfigureAwait(false);
            if (loaded == 0)
                throw new InvalidOperationException(
                    "Das Telefon hat die Telefonbuch-Verbindung geschlossen.");

            var chunk = new byte[loaded];
            _reader.ReadBytes(chunk);
            chunk.CopyTo(buffer, filled);
            filled += (int)loaded;
        }

        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        try { _writer?.DetachStream(); } catch { /* already detached */ }
        try { _reader?.DetachStream(); } catch { /* already detached */ }

        _writer?.Dispose();
        _reader?.Dispose();
        _socket?.Dispose();

        _writer = null;
        _reader = null;
        _socket = null;

        return ValueTask.CompletedTask;
    }
}
