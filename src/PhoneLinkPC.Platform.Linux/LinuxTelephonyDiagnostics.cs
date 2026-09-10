using System.Diagnostics;
using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Diagnostics;
using PhoneLinkPC.Core.Platform;

namespace PhoneLinkPC.Platform.Linux;

/// <summary>
/// Linux diagnostics for milestone 6. It probes only what can be checked without a
/// D-Bus dependency: which of the required daemons are present and running.
///
/// The BlueZ and PipeWire calls themselves are deliberately not implemented yet -
/// see docs/LINUX_TELEPHONY.md for the planned D-Bus surface.
/// </summary>
public sealed class LinuxTelephonyDiagnostics : ITelephonyDiagnostics
{
    public string Backend => "LinuxTelephonyService";

    public Task<TelephonyDiagnosticsReport> RunFullAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck>
        {
            new()
            {
                Category = "System",
                Name = "Betriebssystem",
                Status = DiagnosticStatus.Info,
                Value = HostPlatform.DisplayName
            }
        };

        if (!OperatingSystem.IsLinux())
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "System",
                Name = "Linux-Backend",
                Status = DiagnosticStatus.Skipped,
                Value = "Nicht auf diesem Host",
                Detail = "Das Linux-Backend läuft nur unter Linux. Unter Windows übernimmt "
                       + "WindowsTelephonyService."
            });
            return Task.FromResult(new TelephonyDiagnosticsReport { Backend = Backend, Checks = checks });
        }

        checks.Add(Daemon("BlueZ (bluetoothd)", "bluetoothd",
            "BlueZ stellt die Bluetooth-Profile bereit, inklusive HFP."));
        checks.Add(Daemon("PipeWire", "pipewire",
            "PipeWire übernimmt das Gesprächsaudio zwischen Telefon und PC."));
        checks.Add(Daemon("WirePlumber", "wireplumber",
            "WirePlumber schaltet das Bluetooth-Audioprofil auf HFP um."));

        checks.Add(new DiagnosticCheck
        {
            Category = "HFP / Telefonie",
            Name = "HFP Hands-Free Rolle",
            Status = DiagnosticStatus.Unknown,
            Value = "Noch nicht implementiert",
            Probe = "org.bluez.Profile1 / org.ofono",
            Detail = "Geplant für Meilenstein 6. Der PC soll sich gegenüber dem Telefon als "
                   + "Hands-Free Unit registrieren. Siehe docs/LINUX_TELEPHONY.md."
        });

        return Task.FromResult(new TelephonyDiagnosticsReport { Backend = Backend, Checks = checks });
    }

    public Task<TelephonyDiagnosticsReport> CheckCapabilitiesAsync(CancellationToken ct = default) => RunFullAsync(ct);
    public Task<TelephonyDiagnosticsReport> TestConnectionAsync(CancellationToken ct = default) => RunFullAsync(ct);
    public Task<TelephonyDiagnosticsReport> FindPhoneLinesAsync(CancellationToken ct = default) => RunFullAsync(ct);

    /// <summary>Checks whether a daemon is currently running, by process name.</summary>
    private static DiagnosticCheck Daemon(string name, string processName, string why)
    {
        try
        {
            var running = Process.GetProcessesByName(processName).Length > 0;
            return new DiagnosticCheck
            {
                Category = "Voraussetzungen",
                Name = name,
                Status = running ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
                Value = running ? "Läuft" : "Nicht gefunden",
                Probe = $"Process.GetProcessesByName(\"{processName}\")",
                Detail = running ? why : why + " Der Dienst läuft aktuell nicht."
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticCheck
            {
                Category = "Voraussetzungen",
                Name = name,
                Status = DiagnosticStatus.Failed,
                Value = "Prüfung fehlgeschlagen",
                Detail = ex.Message
            };
        }
    }
}
