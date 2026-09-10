using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Bluetooth;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Platform.Linux;

/// <summary>
/// Linux backend skeleton for milestone 6 (BlueZ / D-Bus / PipeWire).
///
/// This class exists so the UI can be developed and shipped against one interface on both
/// platforms. It deliberately does NOT fake telephony: every call-control method returns a
/// NotSupported result until the BlueZ implementation lands. The UI shows that truthfully
/// rather than pretending a real cellular call was answered.
/// </summary>
public sealed class LinuxTelephonyService : ITelephonyService
{
    private const string NotImplementedYet =
        "Linux-Backend ist noch nicht implementiert (Meilenstein 6, BlueZ/PipeWire).";

    public string BackendName => "LinuxTelephonyService";
    public bool IsAvailable => OperatingSystem.IsLinux();

    public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
    public PhoneDevice? ConnectedDevice => null;
    public CallInfo? CurrentCall => null;

#pragma warning disable CS0067 // Raised once the BlueZ implementation lands in milestone 6.
    public event EventHandler<DeviceEventArgs>? DeviceConnected;
    public event EventHandler<DeviceEventArgs>? DeviceDisconnected;
    public event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;
    public event EventHandler<CallEventArgs>? IncomingCall;
    public event EventHandler<CallEventArgs>? CallAnswered;
    public event EventHandler<CallEventArgs>? CallEnded;
    public event EventHandler<CallStateEventArgs>? CallStateChanged;
    public event EventHandler<CallerInfoEventArgs>? CallerInfoChanged;
    public event EventHandler<AudioRouteEventArgs>? AudioRouteChanged;
    public event EventHandler<string>? ProtocolTrace;
#pragma warning restore CS0067

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PhoneDevice>> ScanDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhoneDevice>>([]);

    public Task<IReadOnlyList<PhoneDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhoneDevice>>([]);

    public Task<IReadOnlyList<PhoneDevice>> DiscoverNewDevicesAsync(
        TimeSpan duration, IProgress<PhoneDevice>? progress = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhoneDevice>>([]);

    public Task<TelephonyResult> PairAsync(string deviceId, IProgress<string>? status = null,
                                           CancellationToken ct = default) => Fail();

    public Task<TelephonyResult> UnpairAsync(string deviceId, CancellationToken ct = default) => Fail();

    /// <summary>
    /// The OBEX exchange in Core is platform neutral; only the transport is missing here.
    /// </summary>
    public Task SendContactAsync(Contact contact, CancellationToken ct = default) =>
        throw new NotSupportedException("Das Linux-Backend ist noch nicht implementiert (Meilenstein 6).");

    public Task<IReadOnlyList<Contact>> ReadPhonebookAsync(
        IProgress<string>? status = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Das Linux-Backend ist noch nicht implementiert (Meilenstein 6).");

    /// <summary>
    /// BlueZ exposes the same per-transport bond state over D-Bus, so this will be a real
    /// implementation in milestone 6. Until then it says so rather than inventing a state.
    /// </summary>
    public Task<BondDiagnosis> DiagnoseBondAsync(CancellationToken ct = default) =>
        Task.FromResult(new BondDiagnosis
        {
            Problem = BondProblem.NoDeviceKnown,
            Summary = "Das Linux-Backend ist noch nicht implementiert (Meilenstein 6).",
            Remedy = "Die Kopplung vorerst mit den Bluetooth-Einstellungen des Systems prüfen."
        });

    public Task<TelephonyResult> ConnectAsync(string deviceId, CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> DisconnectAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> AnswerCallAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> RejectCallAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> HangupAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> SetMutedAsync(bool muted, CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> DialAsync(string number, CancellationToken ct = default) => Fail();

    public Task<CallInfo?> GetCurrentCallAsync(CancellationToken ct = default) => Task.FromResult<CallInfo?>(null);

    private static Task<TelephonyResult> Fail() => Task.FromResult(TelephonyResult.Fail(NotImplementedYet));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
