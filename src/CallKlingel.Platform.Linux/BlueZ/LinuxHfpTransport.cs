using System.Text;
using System.Threading.Channels;
using CallKlingel.Core.Hfp;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace CallKlingel.Platform.Linux.BlueZ;

/// <summary>
/// The Hands-Free channel to the phone, opened through BlueZ.
///
/// Unlike Windows, where the app dials an RFCOMM socket itself, BlueZ owns the connection:
/// the app registers as a Hands-Free profile and BlueZ hands back an already connected file
/// descriptor through <c>NewConnection</c>. That inversion is the whole reason this class
/// exists - the protocol above it is the same on both platforms.
/// </summary>
internal sealed class LinuxHfpTransport : IHfpTransport, IPathMethodHandler
{
    private readonly BlueZClient _bluez;
    private readonly Action<string>? _trace;

    /// <summary>Set once BlueZ calls back with the socket, awaited by ConnectAsync.</summary>
    private readonly TaskCompletionSource<FileStream> _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private FileStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _profileRegistered;
    private string? _expectedDevicePath;

    public LinuxHfpTransport(BlueZClient bluez, Action<string>? trace = null)
    {
        _bluez = bluez;
        _trace = trace;
    }

    public bool IsConnected => _stream is not null;

    // --- IPathMethodHandler: this is what BlueZ calls into ------------------------------

    public string Path => BlueZNames.ProfileObjectPath;
    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        string member = context.Request.MemberAsString ?? string.Empty;

        switch (member)
        {
            case "NewConnection":
                HandleNewConnection(context);
                break;

            case "RequestDisconnection":
                Trace("BlueZ meldet Trennung.");
                CloseStream();
                context.Reply(context.CreateReplyWriter(null).CreateMessage());
                break;

            case "Release":
                // BlueZ drops the profile, for example when bluetoothd restarts.
                Trace("Profil von BlueZ freigegeben.");
                _profileRegistered = false;
                context.Reply(context.CreateReplyWriter(null).CreateMessage());
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// NewConnection(object device, fd, dict properties). The file descriptor is an already
    /// connected RFCOMM socket; from here on it behaves like any other byte stream.
    /// </summary>
    private void HandleNewConnection(MethodContext context)
    {
        try
        {
            var reader = context.Request.GetBodyReader();
            string devicePath = reader.ReadObjectPathAsString();
            var handle = reader.ReadHandle<SafeFileHandle>();

            if (handle is null || handle.IsInvalid)
            {
                Trace($"NewConnection ohne brauchbaren Dateideskriptor von {devicePath}.");
                context.ReplyError("org.bluez.Error.Rejected", "Kein gültiger Dateideskriptor.");
                return;
            }

            if (_expectedDevicePath is not null &&
                !string.Equals(devicePath, _expectedDevicePath, StringComparison.Ordinal))
            {
                // A second phone offering itself while we are talking to the first one.
                Trace($"Verbindung von {devicePath} abgelehnt, erwartet wurde {_expectedDevicePath}.");
                handle.Dispose();
                context.ReplyError("org.bluez.Error.Rejected", "Anderes Gerät ist verbunden.");
                return;
            }

            var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
            _stream = stream;

            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => PumpLinesAsync(stream, _readerCts.Token));

            Trace($"HFP-Kanal zu {devicePath} offen.");
            _connected.TrySetResult(stream);

            context.Reply(context.CreateReplyWriter(null).CreateMessage());
        }
        catch (Exception ex)
        {
            Trace($"NewConnection fehlgeschlagen: {ex.Message}");
            _connected.TrySetException(ex);
            context.ReplyError("org.bluez.Error.Failed", ex.Message);
        }
    }

    // --- IHfpTransport -----------------------------------------------------------------

    public async Task ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        await _bluez.ConnectAsync(ct).ConfigureAwait(false);

        var device = await _bluez.FindDeviceAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Gerät {deviceId} ist BlueZ nicht bekannt. Ist es gekoppelt?");

        if (!device.Paired)
            throw new InvalidOperationException($"{device.Name} ist nicht gekoppelt.");

        _expectedDevicePath = device.Path;

        await RegisterProfileAsync().ConfigureAwait(false);

        // BlueZ connects the profile and then calls NewConnection on the object registered
        // above. The call below returns before that callback arrives, so the result is
        // awaited separately.
        try
        {
            await _bluez.ConnectProfileAsync(device.Path, BlueZNames.HandsFreeUuid).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            throw new InvalidOperationException(
                $"BlueZ konnte das Hands-Free-Profil nicht verbinden: {ex.ErrorName}. " +
                "Meist bietet das Telefon die Freisprechfunktion für diesen PC nicht an.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await _connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                "BlueZ hat den Hands-Free-Kanal nicht geöffnet. Das Telefon muss die " +
                "Freisprechverbindung erlauben; auf manchen Geräten heißt die Einstellung " +
                "\"Anrufe übertragen\" oder \"Telefonaudio\".");
        }
    }

    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("HFP-Kanal ist nicht offen.");

        // HFP wants the carriage return; the phone ignores anything sent without it.
        byte[] payload = Encoding.ASCII.GetBytes(command.EndsWith('\r') ? command : command + "\r");
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct = default) =>
        _lines.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Splits the byte stream into protocol lines. HFP separates on CR and LF and the phone
    /// is free to use either, so both are treated as terminators and empty results dropped.
    /// </summary>
    private async Task PumpLinesAsync(FileStream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var pending = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;

                foreach (char c in Encoding.ASCII.GetString(buffer, 0, read))
                {
                    if (c is '\r' or '\n')
                    {
                        if (pending.Length > 0)
                        {
                            _lines.Writer.TryWrite(pending.ToString());
                            pending.Clear();
                        }
                    }
                    else
                    {
                        pending.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace($"HFP-Kanal abgebrochen: {ex.Message}");
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    // --- Profile registration ------------------------------------------------------------

    private async Task RegisterProfileAsync()
    {
        if (_profileRegistered) return;

        _bluez.Connection.AddMethodHandler(this);

        MessageBuffer BuildRequest()
        {
            using var writer = _bluez.Connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BlueZNames.Service, BlueZNames.ProfileManagerPath,
                                         BlueZNames.ProfileManager, "RegisterProfile",
                                         "osa{sv}", MessageFlags.None);
            writer.WriteObjectPath(BlueZNames.ProfileObjectPath);
            writer.WriteString(BlueZNames.HandsFreeUuid);

            var options = writer.WriteDictionaryStart();

            writer.WriteDictionaryEntryStart();
            writer.WriteString("Name");
            writer.WriteVariantString("Call Klingel");

            // Client role: the phone is the gateway, this PC is the hands-free unit.
            writer.WriteDictionaryEntryStart();
            writer.WriteString("Role");
            writer.WriteVariantString("client");

            writer.WriteDictionaryEnd(options);

            return writer.CreateMessage();
        }

        try
        {
            await _bluez.Connection.CallMethodAsync(BuildRequest()).ConfigureAwait(false);
            _profileRegistered = true;
            Trace("Hands-Free-Profil bei BlueZ registriert.");
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.bluez.Error.AlreadyExists")
        {
            // A previous run left the profile behind; that is fine, it is ours.
            _profileRegistered = true;
            Trace("Hands-Free-Profil war bereits registriert.");
        }
    }

    private async Task UnregisterProfileAsync()
    {
        if (!_profileRegistered) return;

        MessageBuffer BuildRequest()
        {
            using var writer = _bluez.Connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BlueZNames.Service, BlueZNames.ProfileManagerPath,
                                         BlueZNames.ProfileManager, "UnregisterProfile",
                                         "o", MessageFlags.None);
            writer.WriteObjectPath(BlueZNames.ProfileObjectPath);
            return writer.CreateMessage();
        }

        try
        {
            await _bluez.Connection.CallMethodAsync(BuildRequest()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace($"Profil konnte nicht abgemeldet werden: {ex.Message}");
        }
        finally
        {
            _profileRegistered = false;
            _bluez.Connection.RemoveMethodHandler(BlueZNames.ProfileObjectPath);
        }
    }

    private void CloseStream()
    {
        _readerCts?.Cancel();
        _stream?.Dispose();
        _stream = null;
    }

    private void Trace(string line) => _trace?.Invoke($"[HFP] {line}");

    public async ValueTask DisposeAsync()
    {
        CloseStream();

        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); } catch { /* shutting down */ }
        }

        _readerCts?.Dispose();
        await UnregisterProfileAsync().ConfigureAwait(false);
    }
}
