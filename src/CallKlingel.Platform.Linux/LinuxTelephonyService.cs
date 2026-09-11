using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Bluetooth;
using CallKlingel.Core.Hfp;
using CallKlingel.Core.Models;
using CallKlingel.Platform.Linux.BlueZ;

namespace CallKlingel.Platform.Linux;

/// <summary>
/// Telephony on Linux, through BlueZ.
///
/// BlueZ owns the Bluetooth connection here, which turns the Windows flow inside out: the app
/// registers a Hands-Free profile and BlueZ hands back a connected socket, rather than the app
/// dialling one itself. Above that socket the protocol is the shared <see cref="HfpClient"/>,
/// so call handling behaves identically on both platforms.
///
/// Audio is deliberately not touched. PipeWire routes the HFP stream on its own once the
/// profile is up, and a second component fighting it over the same device causes exactly the
/// kind of silent call this app exists to prevent.
/// </summary>
public sealed class LinuxTelephonyService : ITelephonyService
{
    private readonly BlueZClient _bluez;
    private LinuxHfpTransport? _transport;
    private HfpClient? _hfp;
    private IDisposable? _propertyWatch;

    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
    private PhoneDevice? _device;
    private CallInfo? _call;

    public LinuxTelephonyService()
    {
        _bluez = new BlueZClient(Trace);
    }

    public string BackendName => "LinuxTelephonyService (BlueZ)";
    public bool IsAvailable => OperatingSystem.IsLinux();

    public DeviceConnectionState ConnectionState => _state;
    public PhoneDevice? ConnectedDevice => _device;
    public CallInfo? CurrentCall => _call;

    public event EventHandler<DeviceEventArgs>? DeviceConnected;
    public event EventHandler<DeviceEventArgs>? DeviceDisconnected;
    public event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;
    public event EventHandler<CallEventArgs>? IncomingCall;
    public event EventHandler<CallEventArgs>? CallAnswered;
    public event EventHandler<CallEventArgs>? CallEnded;
    public event EventHandler<CallStateEventArgs>? CallStateChanged;
    public event EventHandler<CallerInfoEventArgs>? CallerInfoChanged;

    // PipeWire owns the audio path on Linux and does not report route changes back to us, so
    // this stays silent rather than inventing a value the app would then display as fact.
#pragma warning disable CS0067
    public event EventHandler<AudioRouteEventArgs>? AudioRouteChanged;
#pragma warning restore CS0067

    public event EventHandler<string>? ProtocolTrace;

    // --- Lifecycle ---------------------------------------------------------------------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("Das BlueZ-Backend läuft nur unter Linux.");

        await _bluez.ConnectAsync(ct).ConfigureAwait(false);

        // Watching BlueZ means a phone that walks out of range is noticed without polling,
        // and the UI stops claiming a connection that is long gone.
        _propertyWatch = await _bluez.WatchDevicePropertiesAsync(OnDevicePropertiesChanged)
            .ConfigureAwait(false);
    }

    private void OnDevicePropertiesChanged(string path, Dictionary<string, Tmds.DBus.Protocol.VariantValue> changed)
    {
        if (_device is null) return;
        if (!changed.TryGetValue("Connected", out var connected)) return;
        if (connected.Type != Tmds.DBus.Protocol.VariantValueType.Bool) return;
        if (connected.GetBool()) return;

        Trace($"BlueZ meldet {path} als getrennt.");
        var lost = _device;
        SetState(DeviceConnectionState.Disconnected);
        _device = null;
        if (lost is not null) DeviceDisconnected?.Invoke(this, new DeviceEventArgs(lost));
    }

    // --- Devices -----------------------------------------------------------------------

    public async Task<IReadOnlyList<PhoneDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var devices = await _bluez.GetDevicesAsync(ct).ConfigureAwait(false);

        return devices
            .Where(d => d.Paired)
            .Select(ToPhoneDevice)
            .ToList();
    }

    public Task<IReadOnlyList<PhoneDevice>> ScanDevicesAsync(CancellationToken ct = default) =>
        GetDevicesAsync(ct);

    public async Task<IReadOnlyList<PhoneDevice>> DiscoverNewDevicesAsync(
        TimeSpan duration, IProgress<PhoneDevice>? progress = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        string? adapter = await _bluez.GetAdapterPathAsync(ct).ConfigureAwait(false);
        if (adapter is null) return [];

        var seen = new Dictionary<string, PhoneDevice>(StringComparer.OrdinalIgnoreCase);

        await _bluez.StartDiscoveryAsync(adapter).ConfigureAwait(false);
        try
        {
            var deadline = DateTimeOffset.UtcNow + duration;
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                foreach (var d in await _bluez.GetDevicesAsync(ct).ConfigureAwait(false))
                {
                    if (d.Paired || d.Address.Length == 0) continue;
                    if (seen.ContainsKey(d.Address)) continue;

                    var phone = ToPhoneDevice(d);
                    seen[d.Address] = phone;
                    progress?.Report(phone);
                }
            }
        }
        finally
        {
            // Discovery keeps the radio busy and drains a laptop battery, so it is stopped
            // even when the caller cancelled.
            try { await _bluez.StopDiscoveryAsync(adapter).ConfigureAwait(false); }
            catch (Exception ex) { Trace($"StopDiscovery fehlgeschlagen: {ex.Message}"); }
        }

        return seen.Values.ToList();
    }

    public async Task<TelephonyResult> PairAsync(
        string deviceId, IProgress<string>? status = null, CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            var device = await _bluez.FindDeviceAsync(deviceId, ct).ConfigureAwait(false);
            if (device is null) return TelephonyResult.Fail($"Gerät {deviceId} nicht gefunden.");

            if (!device.Paired)
            {
                status?.Report("Kopplung angefragt. Bitte am Telefon bestätigen.");
                await _bluez.PairDeviceAsync(device.Path).ConfigureAwait(false);
            }

            // Without Trusted the phone needs a confirmation on every reconnect, which makes
            // an app that is supposed to sit quietly in the background useless.
            status?.Report("Gerät wird als vertrauenswürdig markiert.");
            await _bluez.SetTrustedAsync(device.Path, true).ConfigureAwait(false);

            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Kopplung fehlgeschlagen: {ex.Message}");
        }
    }

    public async Task<TelephonyResult> UnpairAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            string? adapter = await _bluez.GetAdapterPathAsync(ct).ConfigureAwait(false);
            var device = await _bluez.FindDeviceAsync(deviceId, ct).ConfigureAwait(false);

            if (adapter is null || device is null)
                return TelephonyResult.Fail($"Gerät {deviceId} nicht gefunden.");

            await _bluez.RemoveDeviceAsync(adapter, device.Path).ConfigureAwait(false);
            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Entkopplung fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>
    /// BlueZ keeps one device object per address regardless of transport, so the split bond
    /// that plagues Windows cannot happen here. What can happen is a phone that is paired but
    /// does not offer the Audio Gateway role, and that is worth saying plainly.
    /// </summary>
    public async Task<BondDiagnosis> DiagnoseBondAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            if (!await _bluez.IsAdapterPoweredAsync(ct).ConfigureAwait(false))
            {
                return new BondDiagnosis
                {
                    Problem = BondProblem.NoDeviceKnown,
                    Summary = "Bluetooth ist ausgeschaltet.",
                    Remedy = "Bluetooth in den Systemeinstellungen einschalten, dann erneut suchen."
                };
            }

            var devices = await _bluez.GetDevicesAsync(ct).ConfigureAwait(false);
            var endpoints = devices
                .Where(d => d.Paired)
                .Select(d => new BluetoothEndpoint
                {
                    Address = d.Address,
                    Name = d.Name,
                    Transport = BluetoothTransport.Classic,
                    IsPaired = d.Paired,
                    IsConnected = d.Connected,
                    Id = d.Path,
                    IsPhone = d.Icon.Contains("phone", StringComparison.OrdinalIgnoreCase)
                })
                .ToList();

            var diagnosis = BondAnalyzer.Analyze(endpoints);
            if (diagnosis.BlocksTelephony) return diagnosis;

            // The analyzer only sees pairing, not which services the phone offers.
            var withoutGateway = devices.FirstOrDefault(d => d.Paired && !d.SupportsHandsFree);
            if (withoutGateway is not null && devices.All(d => !d.Paired || !d.SupportsHandsFree))
            {
                return new BondDiagnosis
                {
                    Problem = BondProblem.NoDeviceKnown,
                    Summary = $"{withoutGateway.Name} ist gekoppelt, bietet aber keine Freisprechfunktion an.",
                    Remedy = "Am Telefon unter den Bluetooth-Details dieses PCs \"Anrufe\" oder " +
                             "\"Telefonaudio\" erlauben und danach neu verbinden.",
                    DeviceName = withoutGateway.Name,
                    Address = withoutGateway.Address
                };
            }

            return diagnosis;
        }
        catch (Exception ex)
        {
            return new BondDiagnosis
            {
                Problem = BondProblem.NoDeviceKnown,
                Summary = $"BlueZ ist nicht erreichbar: {ex.Message}",
                Remedy = "Läuft der Dienst bluetooth.service? Prüfen mit: systemctl status bluetooth"
            };
        }
    }

    // --- Connection --------------------------------------------------------------------

    public async Task<TelephonyResult> ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await TearDownCallChannelAsync().ConfigureAwait(false);

            SetState(DeviceConnectionState.Connecting);

            var transport = new LinuxHfpTransport(_bluez, Trace);
            var client = new HfpClient(transport);

            client.IncomingCall += OnIncomingCall;
            client.CallStateChanged += OnCallStateChanged;
            client.ProtocolLine += OnProtocolLine;

            await client.ConnectAsync(deviceId, ct).ConfigureAwait(false);

            _transport = transport;
            _hfp = client;

            var blue = await _bluez.FindDeviceAsync(deviceId, ct).ConfigureAwait(false);
            _device = blue is not null ? ToPhoneDevice(blue) : null;

            SetState(DeviceConnectionState.Connected);
            if (_device is not null) DeviceConnected?.Invoke(this, new DeviceEventArgs(_device));

            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            SetState(DeviceConnectionState.Error);
            await TearDownCallChannelAsync().ConfigureAwait(false);
            return TelephonyResult.Fail(ex.Message);
        }
    }

    public async Task<TelephonyResult> DisconnectAsync(CancellationToken ct = default)
    {
        var lost = _device;
        await TearDownCallChannelAsync().ConfigureAwait(false);

        if (lost is not null)
        {
            try
            {
                var blue = await _bluez.FindDeviceAsync(lost.Id, ct).ConfigureAwait(false);
                if (blue is not null) await _bluez.DisconnectDeviceAsync(blue.Path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace($"Trennen über BlueZ fehlgeschlagen: {ex.Message}");
            }
        }

        _device = null;
        SetState(DeviceConnectionState.Disconnected);
        if (lost is not null) DeviceDisconnected?.Invoke(this, new DeviceEventArgs(lost));

        return TelephonyResult.Ok();
    }

    // --- Call control ------------------------------------------------------------------

    public Task<TelephonyResult> AnswerCallAsync(CancellationToken ct = default) =>
        RunAsync(c => c.AnswerAsync(ct), "Anruf annehmen");

    public Task<TelephonyResult> RejectCallAsync(CancellationToken ct = default) =>
        RunAsync(c => c.HangUpAsync(ct), "Anruf abweisen");

    public Task<TelephonyResult> HangupAsync(CancellationToken ct = default) =>
        RunAsync(c => c.HangUpAsync(ct), "Auflegen");

    /// <summary>
    /// Muting belongs to the audio path, which PipeWire owns here. Reporting NotSupported is
    /// honest; a button that silently does nothing is not.
    /// </summary>
    public Task<TelephonyResult> SetMutedAsync(bool muted, CancellationToken ct = default) =>
        Task.FromResult(TelephonyResult.NotSupported(
            "Stummschalten läuft unter Linux über die Systemlautstärke (PipeWire)"));

    public Task<TelephonyResult> DialAsync(string number, CancellationToken ct = default) =>
        RunAsync(c => c.SendAsync($"ATD{number};", ct), "Wählen");

    public Task<CallInfo?> GetCurrentCallAsync(CancellationToken ct = default) =>
        Task.FromResult(_call);

    // --- Contacts ----------------------------------------------------------------------

    /// <summary>
    /// Reading contacts needs PBAP over obexd, which is a separate D-Bus service with its own
    /// session handling. Not implemented yet, and saying so beats returning an empty list that
    /// reads as "this phone has no contacts".
    /// </summary>
    public Task<IReadOnlyList<Contact>> ReadPhonebookAsync(
        IProgress<string>? status = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Das Telefonbuch über Bluetooth zu lesen ist unter Linux noch nicht umgesetzt " +
            "(benötigt obexd/PBAP).");

    public Task SendContactAsync(Contact contact, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Kontakte an das Telefon zu senden ist unter Linux noch nicht umgesetzt " +
            "(benötigt obexd/Object Push).");

    // --- Plumbing ----------------------------------------------------------------------

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("Das BlueZ-Backend läuft nur unter Linux.");
        if (!_bluez.IsConnected)
            await _bluez.ConnectAsync(ct).ConfigureAwait(false);
    }

    private async Task<TelephonyResult> RunAsync(Func<HfpClient, Task> action, string what)
    {
        var client = _hfp;
        if (client is null || !client.IsConnected)
            return TelephonyResult.Fail($"{what}: keine Verbindung zum Telefon.");

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

    private static PhoneDevice ToPhoneDevice(BlueZDevice d) => new()
    {
        // The address is the id everywhere else in the app; the D-Bus path is an
        // implementation detail of this backend.
        Id = d.Address,
        Name = d.Name.Length > 0 ? d.Name : d.Address,
        SupportsHandsFree = d.SupportsHandsFree,
        IsPaired = d.Paired,
        IsConnected = d.Connected,
        SignalStrength = d.Rssi
    };

    private void SetState(DeviceConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(state));
    }

    private void OnIncomingCall(object? sender, HfpCallEventArgs e)
    {
        _call = new CallInfo
        {
            CallId = Guid.NewGuid().ToString("N"),
            State = CallState.Ringing,
            Direction = CallDirection.Incoming,
            PhoneNumber = e.Number,
            AudioRoute = AudioRoute.Phone
        };

        IncomingCall?.Invoke(this, new CallEventArgs(_call));
        if (e.Number is not null)
            CallerInfoChanged?.Invoke(this, new CallerInfoEventArgs(_call));
    }

    private void OnCallStateChanged(object? sender, HfpCallEventArgs e)
    {
        var previous = _call;

        _call = (previous ?? new CallInfo { CallId = Guid.NewGuid().ToString("N") }) with
        {
            State = e.State,
            PhoneNumber = e.Number ?? previous?.PhoneNumber,
            AnsweredAt = e.State == CallState.Active ? (previous?.AnsweredAt ?? DateTimeOffset.Now) : previous?.AnsweredAt
        };

        CallStateChanged?.Invoke(this, new CallStateEventArgs(_call, previous?.State ?? CallState.Idle));

        switch (e.State)
        {
            case CallState.Active when previous?.State != CallState.Active:
                CallAnswered?.Invoke(this, new CallEventArgs(_call));
                break;

            case CallState.Ended or CallState.Idle when previous is not null &&
                                                        previous.State is not (CallState.Ended or CallState.Idle):
                CallEnded?.Invoke(this, new CallEventArgs(_call));
                _call = null;
                break;
        }
    }

    private void OnProtocolLine(object? sender, string line) => ProtocolTrace?.Invoke(this, line);

    private void Trace(string line) => ProtocolTrace?.Invoke(this, line);

    private async Task TearDownCallChannelAsync()
    {
        if (_hfp is not null)
        {
            _hfp.IncomingCall -= OnIncomingCall;
            _hfp.CallStateChanged -= OnCallStateChanged;
            _hfp.ProtocolLine -= OnProtocolLine;
            try { await _hfp.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace($"HFP-Abbau: {ex.Message}"); }
            _hfp = null;
        }

        if (_transport is not null)
        {
            try { await _transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace($"Transport-Abbau: {ex.Message}"); }
            _transport = null;
        }

        _call = null;
    }

    public async ValueTask DisposeAsync()
    {
        _propertyWatch?.Dispose();
        _propertyWatch = null;

        await TearDownCallChannelAsync().ConfigureAwait(false);
        await _bluez.DisposeAsync().ConfigureAwait(false);
    }
}
