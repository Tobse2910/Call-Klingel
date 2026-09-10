using CallKlingel.Core.Diagnostics;

namespace CallKlingel.Core.Abstractions;

/// <summary>
/// Probes what the host operating system really permits. Every backend must be able to
/// answer this honestly, including "I cannot do this and here is the exact error".
/// </summary>
public interface ITelephonyDiagnostics
{
    string Backend { get; }

    /// <summary>Full sweep: OS, Bluetooth, HFP, telephony access, lines, audio.</summary>
    Task<TelephonyDiagnosticsReport> RunFullAsync(CancellationToken ct = default);

    /// <summary>Only the OS/permission questions - "darf diese App überhaupt telefonieren?"</summary>
    Task<TelephonyDiagnosticsReport> CheckCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>Only the Bluetooth/device questions.</summary>
    Task<TelephonyDiagnosticsReport> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Only the phone-line enumeration.</summary>
    Task<TelephonyDiagnosticsReport> FindPhoneLinesAsync(CancellationToken ct = default);
}
