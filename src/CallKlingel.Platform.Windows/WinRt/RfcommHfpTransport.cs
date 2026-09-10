using System.Runtime.CompilerServices;
using System.Text;
using CallKlingel.Core.Hfp;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace CallKlingel.Platform.Windows;

/// <summary>
/// HFP transport over a Bluetooth RFCOMM socket.
///
/// Verified against a Samsung S26 Ultra on 2026-09-10: the phone accepts the connection
/// and answers the AT handshake, even though Windows keeps its own Hands-Free service
/// bound to the same device.
/// </summary>
public sealed class RfcommHfpTransport : IHfpTransport
{
    private static readonly Guid HandsFreeAudioGateway = new("0000111f-0000-1000-8000-00805f9b34fb");

    private readonly Action<string>? _trace;

    private StreamSocket? _socket;
    private DataWriter? _writer;
    private DataReader? _reader;

    /// <param name="trace">
    /// Receives timing notes for each connection attempt. Retrying here is what decides
    /// how long a reconnect feels, and without per-attempt numbers there is no way to tell
    /// a slow attempt from a long wait between attempts.
    /// </param>
    public RfcommHfpTransport(Action<string>? trace = null) => _trace = trace;

    public bool IsConnected => _socket is not null;

    public async Task ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await BluetoothDevice.FromIdAsync(deviceId)
                     ?? throw new InvalidOperationException("Bluetooth-Gerät nicht erreichbar.");

        RfcommDeviceService? service = null;

        // A paired phone usually lets the radio link drop when nothing is using it, and
        // RFCOMM cannot connect without one. Measured on 2026-09-10: an unfiltered,
        // uncached service query wakes the link - ConnectionStatus goes Disconnected ->
        // Connected - while the filtered ...ForIdAsync variant does not. So always ask for
        // everything first, then pick the Hands-Free service out of the result.
        // No shortcut via the cached service record. Measured on 2026-09-10: ConnectionStatus
        // reads Disconnected even when the link is warm and the uncached query answers in
        // 636 ms, because no profile is connected yet. A cache path guarded on that status
        // would never run, and running it unguarded would skip the very query that wakes a
        // sleeping link. The query itself is the cost, and it is the cost of waking the radio:
        // 636 ms warm, 4761 ms cold, and up to three rounds when the phone is asleep.
        _trace?.Invoke($"Linkzustand vor dem Verbinden: {device.ConnectionStatus}");

        for (var attempt = 0; attempt < 3 && service is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            var sdpWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var all = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                service = all.Services.FirstOrDefault(s => s.ServiceId.Uuid == HandsFreeAudioGateway);
                _trace?.Invoke(
                    $"SDP-Abfrage {attempt + 1}: {all.Services.Count} Dienste nach {sdpWatch.ElapsedMilliseconds} ms"
                    + (service is null ? ", kein HFP" : ", HFP gefunden"));
                if (service is not null) break;
            }
            catch (Exception ex)
            {
                // The phone is asleep or out of range; the next attempt may wake it.
                _trace?.Invoke($"SDP-Abfrage {attempt + 1}: gescheitert nach {sdpWatch.ElapsedMilliseconds} ms (0x{ex.HResult:X8})");
            }
        }

        // Last resort: the cached record. Only worth trying when the link is already up.
        if (service is null && device.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            try
            {
                var cached = await device.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
                service = cached.Services.FirstOrDefault(x => x.ServiceId.Uuid == HandsFreeAudioGateway);
            }
            catch
            {
                // Nothing left to try.
            }
        }

        if (service is null)
            throw new InvalidOperationException(device.ConnectionStatus == BluetoothConnectionStatus.Connected
                ? "Das Telefon ist verbunden, bietet aber kein Hands-Free Audio Gateway an. "
                  + "Am Telefon in den Bluetooth-Optionen den Zugriff auf Anrufe erlauben."
                : "Das Telefon ist gekoppelt, aber gerade nicht verbunden. Am Telefon Bluetooth "
                  + "einschalten und den PC in der Bluetooth-Liste antippen.");
        // A closed RFCOMM channel is not immediately reusable: phone and Bluetooth stack
        // need a moment to release it. Measured on 2026-09-10 - reconnecting right after a
        // disconnect fails, a few seconds later it succeeds. So retry instead of giving up
        // on the first attempt, which is what made reconnecting look broken.
        //
        // The waits are short and many rather than few and long. The earlier 1.5s/3s/4.5s
        // backoff spent up to 9 seconds sleeping and turned a channel that frees up after
        // roughly two seconds into a twelve second reconnect - measured on 2026-09-10:
        // 2042 ms when the channel was free, 10418 ms right after a disconnect. Polling
        // more often finds the moment it is released instead of sleeping past it, while
        // the total budget stays comparable.
        StreamSocket? socket = null;
        Exception? lastError = null;

        // Bounded by wall clock, not by a number of attempts: a failing attempt can itself
        // block for seconds, so counting attempts says nothing about how long a user waits.
        // The budget caps the worst case no matter what each attempt costs.
        double[] waits = [0.4, 0.6, 0.8, 1.0, 1.2, 1.5, 2.0];
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(12);

        for (var attempt = 0; attempt <= waits.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0 && budget.Elapsed > deadline)
            {
                _trace?.Invoke($"RFCOMM: Zeitbudget nach {budget.ElapsedMilliseconds} ms erschöpft");
                break;
            }

            var candidate = new StreamSocket();
            var attemptWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await candidate.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                    .AsTask(ct).ConfigureAwait(false);

                _trace?.Invoke($"RFCOMM-Versuch {attempt + 1}: verbunden nach {attemptWatch.ElapsedMilliseconds} ms");
                socket = candidate;
                break;
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                lastError = ex;
                _trace?.Invoke(
                    $"RFCOMM-Versuch {attempt + 1}: gescheitert nach {attemptWatch.ElapsedMilliseconds} ms "
                    + $"(0x{ex.HResult:X8})");

                if (attempt < waits.Length)
                    await Task.Delay(TimeSpan.FromSeconds(waits[attempt]), ct).ConfigureAwait(false);
            }
        }

        if (socket is null)
            throw lastError ?? new InvalidOperationException("Verbindung nicht möglich.");

        _socket = socket;
        _writer = new DataWriter(socket.OutputStream);
        _reader = new DataReader(socket.InputStream)
        {
            InputStreamOptions = InputStreamOptions.Partial
        };
    }

    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        if (_writer is null) throw new InvalidOperationException("Nicht verbunden.");

        // HFP terminates commands with a carriage return, not with CRLF.
        _writer.WriteString(command + "\r");
        await _writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_reader is null) yield break;

        var buffer = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            uint loaded;
            try
            {
                loaded = await _reader.LoadAsync(512).AsTask(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch
            {
                // The phone closed the channel, or Bluetooth dropped.
                yield break;
            }

            if (loaded == 0)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                continue;
            }

            string chunk;
            try { chunk = _reader.ReadString(loaded); }
            catch { yield break; }

            buffer.Append(chunk);

            // Responses are framed by CR and LF; split on both and drop empties.
            var text = buffer.ToString();
            var lastBreak = text.LastIndexOfAny(['\r', '\n']);
            if (lastBreak < 0) continue;

            var complete = text[..(lastBreak + 1)];
            buffer.Remove(0, lastBreak + 1);

            foreach (var line in complete.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }
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
