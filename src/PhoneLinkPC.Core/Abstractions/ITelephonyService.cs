using PhoneLinkPC.Core.Bluetooth;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Abstractions;

/// <summary>
/// The single boundary between the Avalonia UI and platform telephony.
/// The UI reacts only to the abstract states in <see cref="CallState"/> and
/// <see cref="DeviceConnectionState"/> - never to Windows or Linux APIs.
/// </summary>
public interface ITelephonyService : IAsyncDisposable
{
    string BackendName { get; }

    /// <summary>False when the backend cannot run here at all (for example the Linux backend on Windows).</summary>
    bool IsAvailable { get; }

    DeviceConnectionState ConnectionState { get; }
    PhoneDevice? ConnectedDevice { get; }
    CallInfo? CurrentCall { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PhoneDevice>> ScanDevicesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PhoneDevice>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Searches for phones in range that are not paired yet. The user has to open the
    /// Bluetooth screen on the phone for it to be discoverable.
    /// </summary>
    Task<IReadOnlyList<PhoneDevice>> DiscoverNewDevicesAsync(
        TimeSpan duration, IProgress<PhoneDevice>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Pairs a device from inside the app. The pairing confirmation still has to be
    /// accepted on the phone, but the user never has to leave PhoneLink PC.
    /// </summary>
    Task<TelephonyResult> PairAsync(string deviceId, IProgress<string>? status = null,
                                    CancellationToken ct = default);

    /// <summary>
    /// Removes the pairing again. Also used to clear a stale bond reported by
    /// <see cref="DiagnoseBondAsync"/>, passing that diagnosis' endpoint id.
    /// </summary>
    Task<TelephonyResult> UnpairAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Explains the state of the Bluetooth bond in terms the user can act on.
    ///
    /// Needed because every telephony query filters for Bluetooth Classic, so a phone
    /// bonded over LE only vanishes from all of them and the app can otherwise say no more
    /// than "no device paired" - while the phone sits in the system's Bluetooth list,
    /// looking perfectly paired. Measured on 2026-09-10, see docs/WINDOWS_TELEPHONY.md.
    /// </summary>
    Task<BondDiagnosis> DiagnoseBondAsync(CancellationToken ct = default);

    Task<TelephonyResult> ConnectAsync(string deviceId, CancellationToken ct = default);
    Task<TelephonyResult> DisconnectAsync(CancellationToken ct = default);

    Task<TelephonyResult> AnswerCallAsync(CancellationToken ct = default);
    Task<TelephonyResult> RejectCallAsync(CancellationToken ct = default);
    Task<TelephonyResult> HangupAsync(CancellationToken ct = default);
    Task<TelephonyResult> SetMutedAsync(bool muted, CancellationToken ct = default);

    /// <summary>Optional for version 1; backends may report NotSupported.</summary>
    Task<TelephonyResult> DialAsync(string number, CancellationToken ct = default);

    Task<CallInfo?> GetCurrentCallAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the phone's phonebook over Bluetooth. Optional: a backend that cannot do it
    /// says so instead of returning an empty list, because "no contacts" and "cannot read
    /// contacts" are very different answers for the user.
    /// </summary>
    Task<IReadOnlyList<Contact>> ReadPhonebookAsync(
        IProgress<string>? status = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a contact to the phone. It arrives as a file the phone's user has to accept -
    /// no Bluetooth profile lets a PC write into an address book, so this can never be
    /// silent, and the app must not pretend otherwise.
    /// </summary>
    Task SendContactAsync(Contact contact, CancellationToken ct = default);

    event EventHandler<DeviceEventArgs>? DeviceConnected;
    event EventHandler<DeviceEventArgs>? DeviceDisconnected;
    event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;
    event EventHandler<CallEventArgs>? IncomingCall;
    event EventHandler<CallEventArgs>? CallAnswered;
    event EventHandler<CallEventArgs>? CallEnded;
    event EventHandler<CallStateEventArgs>? CallStateChanged;
    event EventHandler<CallerInfoEventArgs>? CallerInfoChanged;
    event EventHandler<AudioRouteEventArgs>? AudioRouteChanged;

    /// <summary>
    /// Raw protocol traffic between PC and phone, for the diagnostics view. Seeing the live
    /// exchange is the only way to tell "nothing happened" from "nothing was received".
    /// </summary>
    event EventHandler<string>? ProtocolTrace;
}
