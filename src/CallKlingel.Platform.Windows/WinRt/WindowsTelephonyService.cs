using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Bluetooth;
using CallKlingel.Core.Hfp;
using CallKlingel.Core.Models;
using CallKlingel.Core.Pbap;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace CallKlingel.Platform.Windows;

/// <summary>
/// Windows telephony over the Hands-Free Profile.
///
/// It does NOT use Windows.ApplicationModel.Calls. That API is disabled for third-party
/// apps since Windows 11 22H2: RequestAccessAsync returns Allowed, but RegisterApp has no
/// effect and no phone line is ever reported. Measured on 2026-09-10, see
/// docs/WINDOWS_TELEPHONY.md.
///
/// Instead the PC acts as a hands-free unit and speaks HFP to the phone over RFCOMM, which
/// is exactly the role the project set out to implement - and the same protocol the Linux
/// backend will use.
/// </summary>
public sealed class WindowsTelephonyService : ITelephonyService
{
    private static readonly Guid HandsFreeAudioGateway = new("0000111f-0000-1000-8000-00805f9b34fb");

    private readonly List<PhoneDevice> _known = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HfpClient? _hfp;
    private bool _muted;

    /// <summary>
    /// What the phone said while the service level connection was being established. Kept
    /// only until the handshake finishes, and reported when it does not - a stalled
    /// handshake is otherwise indistinguishable from a silent one.
    /// </summary>
    private readonly List<string> _handshakeLines = [];

    public string BackendName => "WindowsTelephonyService (HFP)";
    public bool IsAvailable => OperatingSystem.IsWindows();

    public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;
    public PhoneDevice? ConnectedDevice { get; private set; }
    public CallInfo? CurrentCall { get; private set; }

    public event EventHandler<DeviceEventArgs>? DeviceConnected;
    public event EventHandler<DeviceEventArgs>? DeviceDisconnected;
    public event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;
    public event EventHandler<CallEventArgs>? IncomingCall;
    public event EventHandler<CallEventArgs>? CallAnswered;
    public event EventHandler<CallEventArgs>? CallEnded;
    public event EventHandler<CallStateEventArgs>? CallStateChanged;
    public event EventHandler<CallerInfoEventArgs>? CallerInfoChanged;
    public event EventHandler<AudioRouteEventArgs>? AudioRouteChanged;

    /// <summary>Raised whenever the phone reports new battery or signal values.</summary>
    public event EventHandler<HfpIndicatorEventArgs>? IndicatorsChanged;

    public event EventHandler<string>? ProtocolTrace;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ------------------------------------------------------------- Devices

    public async Task<IReadOnlyList<PhoneDevice>> ScanDevicesAsync(CancellationToken ct = default)
    {
        var result = new List<PhoneDevice>();
        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true));

        foreach (var d in paired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var dev = await BluetoothDevice.FromIdAsync(d.Id);
                if (dev is null) continue;

                var hasHfp = false;
                try
                {
                    var svc = await dev.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
                    hasHfp = svc.Services.Any(s => s.ServiceId.Uuid == HandsFreeAudioGateway);
                }
                catch
                {
                    // Service discovery fails while the phone is out of range.
                }

                var live = ConnectedDevice?.Id == d.Id ? _hfp : null;

                result.Add(new PhoneDevice
                {
                    Id = d.Id,
                    Name = string.IsNullOrWhiteSpace(dev.Name) ? "(unbenannt)" : dev.Name,
                    IsPaired = true,
                    // Only this app's own hands-free session counts. Windows also reports a
                    // phone as connected when some other profile is up, which would make the
                    // list claim a connection this app does not have.
                    IsConnected = live?.IsConnected == true,
                    SupportsHandsFree = hasHfp,
                    BatteryPercent = live?.Indicators.BatteryPercent,
                    SignalStrength = live?.Indicators.SignalStrength,
                    Manufacturer = dev.ClassOfDevice.MajorClass.ToString()
                });
            }
            catch
            {
                result.Add(new PhoneDevice { Id = d.Id, Name = d.Name, IsPaired = true });
            }
        }

        lock (_known)
        {
            _known.Clear();
            _known.AddRange(result);
        }

        return result;
    }

    public async Task<IReadOnlyList<PhoneDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        lock (_known)
        {
            if (_known.Count > 0) return _known.ToArray();
        }

        return await ScanDevicesAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PhoneDevice>> DiscoverNewDevicesAsync(
        TimeSpan duration, IProgress<PhoneDevice>? progress = null, CancellationToken ct = default) =>
        WindowsPairing.DiscoverAsync(duration, progress, ct);

    public Task<TelephonyResult> PairAsync(string deviceId, IProgress<string>? status = null,
                                           CancellationToken ct = default) =>
        WindowsPairing.PairAsync(deviceId, status, ct);

    public Task<TelephonyResult> UnpairAsync(string deviceId, CancellationToken ct = default) =>
        WindowsPairing.UnpairAsync(deviceId, ct);

    public Task<BondDiagnosis> DiagnoseBondAsync(CancellationToken ct = default) =>
        WindowsBondInspector.DiagnoseAsync(ct);

    /// <summary>
    /// Reads the phonebook on its own RFCOMM channel, independent of the hands-free
    /// connection - the phone serves both at the same time, which is what makes importing
    /// contacts during an active connection possible at all.
    /// </summary>
    public async Task<IReadOnlyList<Contact>> ReadPhonebookAsync(
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        // The connected phone if there is one, otherwise the paired phone that could carry
        // a call - importing contacts does not require an active hands-free connection.
        var deviceId = ConnectedDevice?.Id
                       ?? (await GetDevicesAsync(ct).ConfigureAwait(false))
                           .FirstOrDefault(d => d.SupportsHandsFree)?.Id;

        if (deviceId is null)
            throw new InvalidOperationException(
                "Kein gekoppeltes Telefon gefunden, aus dem Kontakte gelesen werden könnten.");

        await using var client = new PhonebookClient(new RfcommObexTransport(
            RfcommObexTransport.PhonebookAccessServer,
            "Das Telefon bietet kein Telefonbuch über Bluetooth an. Am Telefon in den "
            + "Bluetooth-Optionen dieses PCs \"Kontakte und Anrufverlauf teilen\" einschalten."));

        return await client.ReadContactsAsync(deviceId, status, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands a contact to the phone as a vCard file. The phone decides what happens next -
    /// Bluetooth offers no way to write into an address book directly, which is why this
    /// arrives as a notification the user has to tap rather than as a finished entry.
    /// </summary>
    public async Task SendContactAsync(Contact contact, CancellationToken ct = default)
    {
        var deviceId = ConnectedDevice?.Id
                       ?? (await GetDevicesAsync(ct).ConfigureAwait(false))
                           .FirstOrDefault(d => d.SupportsHandsFree)?.Id;

        if (deviceId is null)
            throw new InvalidOperationException("Kein gekoppeltes Telefon gefunden.");

        await using var client = new ObjectPushClient(new RfcommObexTransport(
            RfcommObexTransport.ObjectPush,
            "Das Telefon nimmt keine Dateien über Bluetooth an."));

        await client.SendAsync(deviceId, contact, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------- Connection

    public async Task<TelephonyResult> ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);
            SetConnectionState(DeviceConnectionState.Connecting);

            lock (_handshakeLines) _handshakeLines.Clear();

            var client = new HfpClient(new RfcommHfpTransport(Trace));
            client.IncomingCall += OnIncomingCall;
            client.CallStateChanged += OnCallStateChanged;
            client.IndicatorsChanged += OnIndicatorsChanged;
            client.ProtocolLine += OnProtocolLine;

            // Connecting takes seconds and the user feels every one of them. The phases are
            // timed separately and reported through the protocol trace, because guessing
            // which one dominates is how the wrong thing gets optimised.
            var phase = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await client.ConnectAsync(deviceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                var detail = Describe(ex);
                SetConnectionState(DeviceConnectionState.Error, detail);
                return TelephonyResult.Fail($"Verbindung zum Telefon fehlgeschlagen: {detail}");
            }

            Trace($"RFCOMM-Kanal offen nach {phase.ElapsedMilliseconds} ms");
            phase.Restart();

            _hfp = client;

            // Wait for the service level connection so battery and call state are known.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (!client.IsReady && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100, ct).ConfigureAwait(false);

            Trace($"Handshake {(client.IsReady ? "fertig" : "NICHT fertig")} nach {phase.ElapsedMilliseconds} ms");

            // "Did not finish" is not a diagnosis. Silence from the phone and an unexpected
            // answer need completely different fixes, so the traffic that did arrive is
            // reported - otherwise the log cannot tell the two apart.
            if (!client.IsReady)
            {
                lock (_handshakeLines)
                {
                    Trace(_handshakeLines.Count == 0
                        ? "Das Telefon hat auf AT+BRSF nichts geantwortet - der Kanal ist offen, "
                          + "aber stumm."
                        : $"Empfangen wurden {_handshakeLines.Count} Zeilen: "
                          + string.Join(" | ", _handshakeLines));
                }
            }

            phase.Restart();

            var device = (await ScanDevicesAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(d => d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));

            Trace($"Gerätedaten nach {phase.ElapsedMilliseconds} ms");

            // The scan cannot read the live indicators yet - ConnectedDevice is only being
            // assigned here - so the values from the handshake are applied explicitly.
            ConnectedDevice = device is null
                ? null
                : device with
                {
                    IsConnected = true,
                    BatteryPercent = client.Indicators.BatteryPercent,
                    SignalStrength = client.Indicators.SignalStrength
                };

            SetConnectionState(DeviceConnectionState.Connected);
            if (ConnectedDevice is not null)
                DeviceConnected?.Invoke(this, new DeviceEventArgs(ConnectedDevice));

            AudioRouteChanged?.Invoke(this, new AudioRouteEventArgs(AudioRoute.Phone));

            return client.IsReady
                ? TelephonyResult.Ok()
                : TelephonyResult.Fail(
                    "Verbunden, aber das Telefon hat den Handshake nicht abgeschlossen. "
                    + "Am Telefon den Zugriff auf Anrufe und Kontakte erlauben.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelephonyResult> DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var device = ConnectedDevice;
            await TearDownAsync().ConfigureAwait(false);
            ConnectedDevice = null;
            SetConnectionState(DeviceConnectionState.Disconnected);
            if (device is not null) DeviceDisconnected?.Invoke(this, new DeviceEventArgs(device));
            return TelephonyResult.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    // -------------------------------------------------------- Call control

    public Task<TelephonyResult> AnswerCallAsync(CancellationToken ct = default) =>
        RunAsync(c => c.AnswerAsync(ct), "Annehmen");

    /// <summary>HFP uses the same command to reject an incoming call and to end an active one.</summary>
    public Task<TelephonyResult> RejectCallAsync(CancellationToken ct = default) =>
        RunAsync(c => c.HangUpAsync(ct), "Ablehnen");

    public Task<TelephonyResult> HangupAsync(CancellationToken ct = default) =>
        RunAsync(c => c.HangUpAsync(ct), "Auflegen");

    /// <summary>
    /// Muting is a hands-free side setting: the microphone gain towards the phone is set
    /// to zero. The call stays up, the caller simply hears nothing.
    /// </summary>
    public async Task<TelephonyResult> SetMutedAsync(bool muted, CancellationToken ct = default)
    {
        var result = await RunAsync(
            c => c.SendAsync(HfpProtocol.SetMicrophoneVolume(muted ? 0 : 15), ct),
            muted ? "Stummschalten" : "Stumm aus").ConfigureAwait(false);

        if (!result.Success) return result;

        _muted = muted;
        if (CurrentCall is not null)
        {
            CurrentCall = CurrentCall with { IsMuted = muted };
            CallerInfoChanged?.Invoke(this, new CallerInfoEventArgs(CurrentCall));
        }

        return TelephonyResult.Ok();
    }

    public Task<TelephonyResult> DialAsync(string number, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(number))
            return Task.FromResult(TelephonyResult.Fail("Keine Rufnummer angegeben."));

        // ATD<number>; - the semicolon selects a voice call rather than a data call.
        return RunAsync(c => c.SendAsync($"ATD{number.Trim()};", ct), "Wählen");
    }

    public Task<TelephonyResult> SendDtmfAsync(char key, CancellationToken ct = default) =>
        RunAsync(c => c.SendDtmfAsync(key, ct), "DTMF");

    public Task<CallInfo?> GetCurrentCallAsync(CancellationToken ct = default) =>
        Task.FromResult(CurrentCall);

    private async Task<TelephonyResult> RunAsync(Func<HfpClient, Task> action, string what)
    {
        var client = _hfp;
        if (client is null || !client.IsConnected)
            return TelephonyResult.Fail($"{what} nicht möglich: keine Verbindung zum Telefon.");

        try
        {
            await action(client).ConfigureAwait(false);
            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"{what} fehlgeschlagen: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- Events

    private void OnIncomingCall(object? sender, HfpCallEventArgs e)
    {
        var info = Build(CallState.Ringing, e.Number, CallDirection.Incoming);
        CurrentCall = info;
        IncomingCall?.Invoke(this, new CallEventArgs(info));
    }

    private void OnCallStateChanged(object? sender, HfpCallEventArgs e)
    {
        var previous = CurrentCall?.State ?? CallState.Idle;
        var direction = e.State == CallState.Dialing
            ? CallDirection.Outgoing
            : CurrentCall?.Direction ?? CallDirection.Incoming;

        var info = Build(e.State, e.Number ?? CurrentCall?.PhoneNumber, direction);

        if (e.State == CallState.Active && previous != CallState.Active)
            info = info with { AnsweredAt = DateTimeOffset.Now };
        else if (CurrentCall?.AnsweredAt is { } answered)
            info = info with { AnsweredAt = answered };

        CurrentCall = e.State == CallState.Ended ? null : info;

        CallStateChanged?.Invoke(this, new CallStateEventArgs(info, previous));
        CallerInfoChanged?.Invoke(this, new CallerInfoEventArgs(info));

        switch (e.State)
        {
            case CallState.Active when previous != CallState.Active:
                AudioRouteChanged?.Invoke(this, new AudioRouteEventArgs(AudioRoute.LocalDevice));
                CallAnswered?.Invoke(this, new CallEventArgs(info));
                break;

            case CallState.Ended:
                AudioRouteChanged?.Invoke(this, new AudioRouteEventArgs(AudioRoute.Phone));
                CallEnded?.Invoke(this, new CallEventArgs(info));
                break;
        }
    }

    private void OnProtocolLine(object? sender, string line)
    {
        // Bounded: a phone that chatters must not grow this without limit.
        lock (_handshakeLines)
            if (_handshakeLines.Count < 40)
                _handshakeLines.Add(line);

        ProtocolTrace?.Invoke(this, line);
    }

    /// <summary>
    /// Reports a timing or state note on the same channel as the protocol traffic. The
    /// marker lets the application tell these apart from lines the phone actually sent, so
    /// they can be logged without polluting the protocol view with invented AT commands.
    /// </summary>
    private void Trace(string message) =>
        ProtocolTrace?.Invoke(this, TimingMarker + message);

    /// <summary>Prefix identifying a note produced by this app rather than by the phone.</summary>
    public const string TimingMarker = "## ";

    private void OnIndicatorsChanged(object? sender, HfpIndicatorEventArgs e)
    {
        IndicatorsChanged?.Invoke(this, e);

        if (ConnectedDevice is null) return;

        var battery = e.Indicators.BatteryPercent;
        var signal = e.Indicators.SignalStrength;
        if (battery == ConnectedDevice.BatteryPercent && signal == ConnectedDevice.SignalStrength)
            return;

        ConnectedDevice = ConnectedDevice with { BatteryPercent = battery, SignalStrength = signal };
        DeviceConnected?.Invoke(this, new DeviceEventArgs(ConnectedDevice));
    }

    private CallInfo Build(CallState state, string? number, CallDirection direction) => new()
    {
        CallId = CurrentCall?.CallId ?? Guid.NewGuid().ToString("N"),
        State = state,
        Direction = direction,
        PhoneNumber = number,
        ContactName = CurrentCall?.ContactName,
        IsMuted = _muted,
        AudioRoute = state == CallState.Active ? AudioRoute.LocalDevice : AudioRoute.Phone,
        StartedAt = CurrentCall?.StartedAt ?? DateTimeOffset.Now
    };

    /// <summary>
    /// Bluetooth errors often arrive as a COMException with an empty message, which tells
    /// the user nothing. Translate the common HRESULTs into something actionable.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var e = ex is AggregateException a && a.InnerException is not null ? a.InnerException : ex;
        var message = string.Join(' ', e.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var hint = (uint)e.HResult switch
        {
            0x8007048F => "Das Telefon ist nicht erreichbar. Bluetooth am Telefon prüfen.",
            0x80070490 => "Windows findet den Freisprech-Dienst nicht. Telefon neu verbinden.",
            0x8007274C => "Zeitüberschreitung. Das Telefon ist zu weit weg oder schläft.",
            0x80072740 => "Der Freisprech-Kanal ist bereits belegt. Andere App oder Vorgänger-"
                          + "verbindung schliesst ihn erst nach kurzer Zeit.",
            0x800710DF => "Der Bluetooth-Adapter antwortet nicht.",
            0x80072742 => "Keine Funkverbindung zum Telefon. Es ist gekoppelt, aber nicht "
                          + "verbunden - am Telefon Bluetooth prüfen.",
            0x80072743 => "Das Telefon ist nicht erreichbar. Gekoppelt, aber ohne aktive "
                          + "Bluetooth-Verbindung.",
            0x8007277C => "Der Freisprech-Kanal ist gerade nicht erreichbar. Nach dem Trennen "
                          + "braucht Bluetooth einige Sekunden, bis er wieder nutzbar ist.",
            0x80072741 => "Der Kanal wird bereits von einem anderen Programm genutzt.",
            _ => null
        };

        var text = message.Length > 0 ? message : $"Fehlercode 0x{e.HResult:X8}";
        return hint is null ? text : $"{hint} ({text})";
    }

    private void SetConnectionState(DeviceConnectionState state, string? message = null)
    {
        if (ConnectionState == state) return;
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(state, message));
    }

    private async Task TearDownAsync()
    {
        if (_hfp is not null)
        {
            _hfp.IncomingCall -= OnIncomingCall;
            _hfp.CallStateChanged -= OnCallStateChanged;
            _hfp.IndicatorsChanged -= OnIndicatorsChanged;
            _hfp.ProtocolLine -= OnProtocolLine;
            await _hfp.DisposeAsync().ConfigureAwait(false);
            _hfp = null;
        }

        CurrentCall = null;
        _muted = false;
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
