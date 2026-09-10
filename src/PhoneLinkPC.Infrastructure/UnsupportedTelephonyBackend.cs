using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Bluetooth;
using PhoneLinkPC.Core.Diagnostics;
using PhoneLinkPC.Core.Models;
using PhoneLinkPC.Core.Platform;

namespace PhoneLinkPC.Infrastructure;

/// <summary>
/// Fallback for hosts with no telephony backend (for example macOS). It reports the
/// situation instead of silently doing nothing.
/// </summary>
internal sealed class UnsupportedTelephonyService : ITelephonyService
{
    private static readonly string Reason =
        $"Für diese Plattform ({HostPlatform.DisplayName}) gibt es kein Telefonie-Backend.";

    public string BackendName => "UnsupportedTelephonyService";
    public bool IsAvailable => false;
    public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
    public PhoneDevice? ConnectedDevice => null;
    public CallInfo? CurrentCall => null;

#pragma warning disable CS0067 // Never raised: this backend has no telephony source.
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

    public Task SendContactAsync(Contact contact, CancellationToken ct = default) =>
        throw new NotSupportedException(Reason);

    public Task<IReadOnlyList<Contact>> ReadPhonebookAsync(
        IProgress<string>? status = null, CancellationToken ct = default) =>
        throw new NotSupportedException(Reason);

    public Task<BondDiagnosis> DiagnoseBondAsync(CancellationToken ct = default) =>
        Task.FromResult(new BondDiagnosis
        {
            Problem = BondProblem.NoDeviceKnown,
            Summary = Reason,
            Remedy = "Die Anwendung auf Windows oder Linux ausführen."
        });

    public Task<TelephonyResult> ConnectAsync(string deviceId, CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> DisconnectAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> AnswerCallAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> RejectCallAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> HangupAsync(CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> SetMutedAsync(bool muted, CancellationToken ct = default) => Fail();
    public Task<TelephonyResult> DialAsync(string number, CancellationToken ct = default) => Fail();
    public Task<CallInfo?> GetCurrentCallAsync(CancellationToken ct = default) => Task.FromResult<CallInfo?>(null);

    private static Task<TelephonyResult> Fail() => Task.FromResult(TelephonyResult.Fail(Reason));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class UnsupportedTelephonyDiagnostics : ITelephonyDiagnostics
{
    public string Backend => "UnsupportedTelephonyDiagnostics";

    public Task<TelephonyDiagnosticsReport> RunFullAsync(CancellationToken ct = default) =>
        Task.FromResult(new TelephonyDiagnosticsReport
        {
            Backend = Backend,
            Checks =
            [
                new DiagnosticCheck
                {
                    Category = "System",
                    Name = "Telefonie-Backend",
                    Status = DiagnosticStatus.Failed,
                    Value = "Keines verfügbar",
                    Detail = $"Plattform {HostPlatform.DisplayName} wird nicht unterstützt. "
                           + "Unterstützt sind Windows und Linux."
                }
            ]
        });

    public Task<TelephonyDiagnosticsReport> CheckCapabilitiesAsync(CancellationToken ct = default) => RunFullAsync(ct);
    public Task<TelephonyDiagnosticsReport> TestConnectionAsync(CancellationToken ct = default) => RunFullAsync(ct);
    public Task<TelephonyDiagnosticsReport> FindPhoneLinesAsync(CancellationToken ct = default) => RunFullAsync(ct);
}
