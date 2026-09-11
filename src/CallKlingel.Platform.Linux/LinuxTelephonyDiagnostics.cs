using System.Diagnostics;
using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Diagnostics;
using CallKlingel.Core.Platform;
using CallKlingel.Platform.Linux.BlueZ;

namespace CallKlingel.Platform.Linux;

/// <summary>
/// Linux diagnostics: which daemons are running, and what BlueZ actually reports.
///
/// The daemon checks stay process based on purpose. When bluetoothd is down, asking D-Bus
/// produces a connection error that says nothing useful, while "bluetoothd läuft nicht" is
/// something the user can act on immediately.
/// </summary>
public sealed class LinuxTelephonyDiagnostics : ITelephonyDiagnostics
{
    public string Backend => "LinuxTelephonyService (BlueZ)";

    public async Task<TelephonyDiagnosticsReport> RunFullAsync(CancellationToken ct = default)
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
            return new TelephonyDiagnosticsReport { Backend = Backend, Checks = checks };
        }

        checks.Add(Daemon("BlueZ (bluetoothd)", "bluetoothd",
            "BlueZ stellt die Bluetooth-Profile bereit, inklusive HFP."));
        checks.Add(Daemon("PipeWire", "pipewire",
            "PipeWire übernimmt das Gesprächsaudio zwischen Telefon und PC."));
        checks.Add(Daemon("WirePlumber", "wireplumber",
            "WirePlumber schaltet das Bluetooth-Audioprofil auf HFP um."));

        checks.AddRange(await ProbeBlueZAsync(ct).ConfigureAwait(false));

        return new TelephonyDiagnosticsReport { Backend = Backend, Checks = checks };
    }

    /// <summary>
    /// Asks BlueZ itself: is there an adapter, is it on, and does any paired device offer the
    /// Audio Gateway role. The last one is the question that decides whether telephony can
    /// work at all, and it is invisible in the system's Bluetooth settings.
    /// </summary>
    private static async Task<List<DiagnosticCheck>> ProbeBlueZAsync(CancellationToken ct)
    {
        var checks = new List<DiagnosticCheck>();

        await using var bluez = new BlueZClient();
        try
        {
            await bluez.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "HFP / Telefonie",
                Name = "D-Bus Systembus",
                Status = DiagnosticStatus.Failed,
                Value = "Nicht erreichbar",
                Probe = "org.bluez über den Systembus",
                Detail = ex.Message
            });
            return checks;
        }

        string? adapter = await bluez.GetAdapterPathAsync(ct).ConfigureAwait(false);
        if (adapter is null)
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "HFP / Telefonie",
                Name = "Bluetooth-Adapter",
                Status = DiagnosticStatus.Failed,
                Value = "Keiner gefunden",
                Probe = "org.bluez.Adapter1",
                Detail = "BlueZ kennt keinen Adapter. Steckt ein Bluetooth-Gerät im Rechner, "
                       + "und ist es nicht per rfkill blockiert? Prüfen mit: rfkill list"
            });
            return checks;
        }

        bool powered = await bluez.IsAdapterPoweredAsync(ct).ConfigureAwait(false);
        checks.Add(new DiagnosticCheck
        {
            Category = "HFP / Telefonie",
            Name = "Bluetooth-Adapter",
            Status = powered ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
            Value = powered ? $"Eingeschaltet ({adapter})" : "Ausgeschaltet",
            Probe = "org.bluez.Adapter1.Powered",
            Detail = powered
                ? "Der Adapter ist bereit."
                : "Bluetooth einschalten, zum Beispiel mit: bluetoothctl power on"
        });

        var devices = await bluez.GetDevicesAsync(ct).ConfigureAwait(false);
        var paired = devices.Where(d => d.Paired).ToList();
        var gateways = paired.Where(d => d.SupportsHandsFree).ToList();

        checks.Add(new DiagnosticCheck
        {
            Category = "HFP / Telefonie",
            Name = "Gekoppelte Geräte",
            Status = paired.Count > 0 ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
            Value = paired.Count > 0 ? string.Join(", ", paired.Select(d => d.Name)) : "Keine",
            Probe = "org.bluez.Device1.Paired",
            Detail = paired.Count > 0
                ? "Diese Geräte sind BlueZ bekannt."
                : "Noch kein Telefon gekoppelt. Die Kopplung geht direkt in dieser App."
        });

        checks.Add(new DiagnosticCheck
        {
            Category = "HFP / Telefonie",
            Name = "Freisprechfunktion des Telefons",
            Status = gateways.Count > 0 ? DiagnosticStatus.Ok
                   : paired.Count > 0 ? DiagnosticStatus.Failed
                   : DiagnosticStatus.Skipped,
            Value = gateways.Count > 0
                ? string.Join(", ", gateways.Select(d => d.Name))
                : paired.Count > 0 ? "Von keinem gekoppelten Gerät angeboten" : "Kein Gerät zu prüfen",
            Probe = $"UUID {BlueZUuids.HandsFreeAudioGateway}",
            Detail = gateways.Count > 0
                ? "Das Telefon bietet die Audio-Gateway-Rolle an, Anrufe können übernommen werden."
                : "Ohne diese Rolle kann der PC keine Anrufe übernehmen. Am Telefon in den "
                + "Bluetooth-Details dieses PCs \"Anrufe\" oder \"Telefonaudio\" erlauben."
        });

        var connected = devices.FirstOrDefault(d => d.Connected);
        checks.Add(new DiagnosticCheck
        {
            Category = "HFP / Telefonie",
            Name = "Aktuelle Verbindung",
            Status = connected is not null ? DiagnosticStatus.Ok : DiagnosticStatus.Info,
            Value = connected?.Name ?? "Kein Gerät verbunden",
            Probe = "org.bluez.Device1.Connected"
        });

        return checks;
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
