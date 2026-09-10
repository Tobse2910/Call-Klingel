using System.Runtime.InteropServices;
using System.Text;
using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Bluetooth;
using PhoneLinkPC.Core.Diagnostics;
using PhoneLinkPC.Core.Platform;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace PhoneLinkPC.Platform.Windows;

/// <summary>
/// Probes the real Windows telephony stack. Nothing here is assumed: every row is the
/// result of an actual API call, and failures are reported with their HRESULT.
/// </summary>
public sealed class WindowsTelephonyDiagnostics : ITelephonyDiagnostics
{
    // Bluetooth SIG service UUIDs that matter for telephony.
    private static readonly Guid HandsFreeAudioGateway = new("0000111f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HandsFreeUnit = new("0000111e-0000-1000-8000-00805f9b34fb");
    private static readonly Guid PhonebookAccessServer = new("0000112f-0000-1000-8000-00805f9b34fb");

    private const int AppmodelErrorNoPackage = 15700;

    public string Backend => "WindowsTelephonyService";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? fullName);

    public async Task<TelephonyDiagnosticsReport> RunFullAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck>();
        AddSystem(checks);
        await AddBluetoothAsync(checks, ct).ConfigureAwait(false);
        await AddHandsFreeAsync(checks, ct).ConfigureAwait(false);
        await AddPhoneLinesAsync(checks, ct).ConfigureAwait(false);
        await AddAudioAsync(checks, ct).ConfigureAwait(false);
        return Report(checks);
    }

    public async Task<TelephonyDiagnosticsReport> CheckCapabilitiesAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck>();
        AddSystem(checks);
        await AddHandsFreeAsync(checks, ct).ConfigureAwait(false);
        return Report(checks);
    }

    public async Task<TelephonyDiagnosticsReport> TestConnectionAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck>();
        await AddBluetoothAsync(checks, ct).ConfigureAwait(false);
        return Report(checks);
    }

    public async Task<TelephonyDiagnosticsReport> FindPhoneLinesAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck>();
        await AddPhoneLinesAsync(checks, ct).ConfigureAwait(false);
        return Report(checks);
    }

    private TelephonyDiagnosticsReport Report(List<DiagnosticCheck> checks) =>
        new() { Backend = Backend, Checks = checks };

    // ---------------------------------------------------------------- System

    private static void AddSystem(List<DiagnosticCheck> checks)
    {
        checks.Add(new DiagnosticCheck
        {
            Category = "System",
            Name = "Betriebssystem",
            Status = DiagnosticStatus.Info,
            Value = HostPlatform.DisplayName,
            Probe = "Environment.OSVersion"
        });
        checks.Add(new DiagnosticCheck
        {
            Category = "System",
            Name = "Architektur / Runtime",
            Status = DiagnosticStatus.Info,
            Value = $"{HostPlatform.Architecture} / {HostPlatform.RuntimeVersion}"
        });
        checks.Add(new DiagnosticCheck
        {
            Category = "System",
            Name = "Windows 11 oder neuer",
            Status = HostPlatform.IsWindows11OrGreater ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
            Value = HostPlatform.IsWindows11OrGreater ? "Ja" : "Nein (Build < 22000)",
            Probe = "Environment.OSVersion.Version.Build >= 22000"
        });

        // Package identity decides whether restricted capabilities could ever apply.
        try
        {
            var len = 0;
            var rc = GetCurrentPackageFullName(ref len, null);
            var unpackaged = rc == AppmodelErrorNoPackage;
            checks.Add(new DiagnosticCheck
            {
                Category = "System",
                Name = "Paketidentität (MSIX)",
                Status = DiagnosticStatus.Info,
                Value = unpackaged ? "Keine - App läuft unpackaged" : "Vorhanden",
                Probe = "kernel32!GetCurrentPackageFullName",
                Detail = unpackaged
                    ? "Ohne MSIX können keine Restricted Capabilities deklariert werden. Der Diagnoselauf zeigt, welche Telefonie-APIs trotzdem funktionieren."
                    : $"Rückgabecode {rc}"
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("System", "Paketidentität (MSIX)", "kernel32!GetCurrentPackageFullName", ex));
        }
    }

    // ------------------------------------------------------------- Bluetooth

    private static async Task AddBluetoothAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            checks.Add(new DiagnosticCheck
            {
                Category = "Bluetooth",
                Name = "Bluetooth-Adapter",
                Status = adapter is null ? DiagnosticStatus.Failed : DiagnosticStatus.Ok,
                Value = adapter is null ? "Nicht gefunden" : $"OK (Adresse {adapter.BluetoothAddress:X12})",
                Probe = "BluetoothAdapter.GetDefaultAsync()",
                Detail = adapter is null ? "Kein Bluetooth-Adapter vorhanden oder deaktiviert." : null
            });

            if (adapter is not null)
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "Bluetooth",
                    Name = "Classic (BR/EDR) unterstützt",
                    Status = adapter.IsClassicSupported ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
                    Value = adapter.IsClassicSupported ? "Ja" : "Nein",
                    Probe = "BluetoothAdapter.IsClassicSupported",
                    Detail = adapter.IsClassicSupported
                        ? null
                        : "HFP benötigt Bluetooth Classic. Ohne BR/EDR ist Telefonie nicht möglich."
                });
            }
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Bluetooth", "Bluetooth-Adapter", "BluetoothAdapter.GetDefaultAsync()", ex));
        }

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            checks.Add(new DiagnosticCheck
            {
                Category = "Bluetooth",
                Name = "Bluetooth eingeschaltet",
                Status = bt is null
                    ? DiagnosticStatus.Failed
                    : bt.State == RadioState.On ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
                Value = bt?.State.ToString() ?? "Kein Bluetooth-Radio",
                Probe = "Radio.GetRadiosAsync()"
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Bluetooth", "Bluetooth eingeschaltet", "Radio.GetRadiosAsync()", ex));
        }

        DeviceInformation? phone = null;
        try
        {
            var paired = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            checks.Add(new DiagnosticCheck
            {
                Category = "Bluetooth",
                Name = "Gekoppelte Geräte",
                Status = paired.Count > 0 ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                Value = paired.Count.ToString(),
                Probe = "DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))",
                Detail = paired.Count == 0
                    ? "Kein Gerät über Bluetooth Classic gekoppelt. Nur Classic trägt HFP - eine reine LE-Kopplung zählt hier nicht mit und wird in der Zeile \"Kopplung Classic vs. LE\" aufgeschlüsselt."
                    : string.Join("\n", paired.Select(d => $"- {d.Name}  [{d.Id}]"))
            });

            foreach (var d in paired)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var dev = await BluetoothDevice.FromIdAsync(d.Id);
                    if (dev is null) continue;

                    var isPhone = dev.ClassOfDevice.MajorClass == BluetoothMajorClass.Phone;
                    if (isPhone) phone ??= d;

                    checks.Add(new DiagnosticCheck
                    {
                        Category = "Bluetooth",
                        Name = $"Gerät: {dev.Name}",
                        Status = isPhone ? DiagnosticStatus.Ok : DiagnosticStatus.Info,
                        Value = $"{dev.ClassOfDevice.MajorClass} / {dev.ConnectionStatus}",
                        Probe = "BluetoothDevice.FromIdAsync(id).ClassOfDevice",
                        Detail = $"Telefon-ID: {dev.DeviceId}"
                    });

                    // Which telephony services does this device actually advertise?
                    try
                    {
                        var svc = await dev.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
                        var uuids = svc.Services.Select(s => s.ServiceId.Uuid).ToList();
                        var hasAg = uuids.Contains(HandsFreeAudioGateway);
                        checks.Add(new DiagnosticCheck
                        {
                            Category = "Bluetooth",
                            Name = $"  HFP-Dienste: {dev.Name}",
                            Status = hasAg ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                            Value = hasAg ? "Hands-Free Audio Gateway (0x111F) vorhanden" : "Kein 0x111F gefunden",
                            Probe = "BluetoothDevice.GetRfcommServicesAsync()",
                            Detail = "Gefundene UUIDs:\n" +
                                     (uuids.Count == 0 ? "(keine)" : string.Join("\n", uuids.Select(Describe)))
                        });
                    }
                    catch (Exception ex)
                    {
                        checks.Add(Failed("Bluetooth", $"  HFP-Dienste: {dev.Name}", "GetRfcommServicesAsync()", ex));
                    }
                }
                catch (Exception ex)
                {
                    checks.Add(Failed("Bluetooth", $"Gerät {d.Name}", "BluetoothDevice.FromIdAsync()", ex));
                }
            }
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Bluetooth", "Gekoppelte Geräte", "DeviceInformation.FindAllAsync()", ex));
        }

        checks.Add(new DiagnosticCheck
        {
            Category = "Bluetooth",
            Name = "Telefon gefunden",
            Status = phone is null ? DiagnosticStatus.Warning : DiagnosticStatus.Ok,
            Value = phone?.Name ?? "Kein Gerät der Klasse Phone",
            Probe = "ClassOfDevice.MajorClass == BluetoothMajorClass.Phone",
            Detail = phone?.Id
        });

        await AddBondStateAsync(checks, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks at both radios instead of Bluetooth Classic alone.
    ///
    /// Every check above filters for Classic, because Classic is the only transport that
    /// carries HFP. A phone bonded over LE only therefore vanishes from all of them, and
    /// the report concluded "no device paired" while the phone sat in the Windows
    /// Bluetooth list looking perfectly connected. That cost a whole debugging session on
    /// 2026-09-10, so the difference is now stated outright.
    /// </summary>
    private static async Task AddBondStateAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        try
        {
            var endpoints = await WindowsBondInspector.ListEndpointsAsync(ct).ConfigureAwait(false);
            var diagnosis = BondAnalyzer.Analyze(endpoints);

            checks.Add(new DiagnosticCheck
            {
                Category = "Bluetooth",
                Name = "Kopplung Classic vs. LE",
                Status = diagnosis.Problem switch
                {
                    BondProblem.None => DiagnosticStatus.Ok,
                    BondProblem.LowEnergyOnlyBond => DiagnosticStatus.Failed,
                    _ => DiagnosticStatus.Warning
                },
                Value = diagnosis.Summary,
                Probe = "DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))"
                        + " + DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))",
                Detail = diagnosis.Remedy + BondTable(endpoints)
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Bluetooth", "Kopplung Classic vs. LE",
                "DeviceInformation.FindAllAsync(AssociationEndpoint)", ex));
        }
    }

    /// <summary>Every endpoint with its transport, so the finding stays checkable by hand.</summary>
    private static string BondTable(IReadOnlyList<BluetoothEndpoint> endpoints)
    {
        var newline = Environment.NewLine;
        if (endpoints.Count == 0) return $"{newline}{newline}Keine Bluetooth-Endpunkte gefunden.";

        var rows = endpoints
            .OrderBy(e => e.NormalizedAddress, StringComparer.Ordinal)
            .ThenBy(e => e.Transport)
            .Select(e =>
                $"- {e.Address}  {(e.Transport == BluetoothTransport.Classic ? "Classic" : "LE     ")}"
                + $"  gekoppelt={(e.IsPaired ? "ja  " : "nein")}"
                + $"  verbunden={(e.IsConnected ? "ja  " : "nein")}"
                + $"  {(e.IsPhone ? "Telefon" : "       ")}  {e.Name}");

        return $"{newline}{newline}Gemessene Endpunkte:{newline}" + string.Join(newline, rows);
    }

    private static string Describe(Guid uuid) =>
        uuid == HandsFreeAudioGateway ? $"{uuid}  <- Hands-Free Audio Gateway (Telefon)"
        : uuid == HandsFreeUnit ? $"{uuid}  <- Hands-Free Unit"
        : uuid == PhonebookAccessServer ? $"{uuid}  <- Phonebook Access Server (PBAP, Kontakte)"
        : uuid.ToString();

    // ------------------------------------------------------ HFP / Telephony

    private static async Task AddHandsFreeAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        string selector;
        try
        {
            selector = PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth);
            checks.Add(new DiagnosticCheck
            {
                Category = "HFP / Telefonie",
                Name = "HFP-Geräteselektor",
                Status = DiagnosticStatus.Ok,
                Value = "Verfügbar",
                Probe = "PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth)",
                Detail = selector
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("HFP / Telefonie", "HFP-Geräteselektor",
                "PhoneLineTransportDevice.GetDeviceSelector(Bluetooth)", ex));
            return;
        }

        DeviceInformationCollection? transports = null;
        try
        {
            transports = await DeviceInformation.FindAllAsync(selector);
            checks.Add(new DiagnosticCheck
            {
                Category = "HFP / Telefonie",
                Name = "HFP-Transportgeräte",
                Status = transports.Count > 0 ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                Value = transports.Count.ToString(),
                Probe = "DeviceInformation.FindAllAsync(hfpSelector)",
                Detail = transports.Count == 0
                    ? "Windows sieht kein Telefon als Hands-Free Audio Gateway. Ursache ist fast immer: Telefon nicht gekoppelt, nicht verbunden, oder das Telefonie-Profil ist in den Bluetooth-Geräteoptionen deaktiviert."
                    : string.Join("\n", transports.Select(d => $"- {d.Name}  [{d.Id}]"))
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("HFP / Telefonie", "HFP-Transportgeräte",
                "DeviceInformation.FindAllAsync(hfpSelector)", ex));
        }

        if (transports is null || transports.Count == 0)
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "HFP / Telefonie",
                Name = "Telefonie-Zugriff erlaubt",
                Status = DiagnosticStatus.Skipped,
                Value = "Nicht prüfbar",
                Probe = "PhoneLineTransportDevice.RequestAccessAsync()",
                Detail = "Übersprungen: RequestAccessAsync ist eine Instanzmethode und benötigt ein vorhandenes HFP-Transportgerät."
            });
            return;
        }

        foreach (var t in transports)
        {
            ct.ThrowIfCancellationRequested();
            PhoneLineTransportDevice device;
            try
            {
                device = PhoneLineTransportDevice.FromId(t.Id);
            }
            catch (Exception ex)
            {
                checks.Add(Failed("HFP / Telefonie", $"Transportgerät {t.Name}",
                    "PhoneLineTransportDevice.FromId()", ex));
                continue;
            }

            try
            {
                var access = await device.RequestAccessAsync();
                checks.Add(new DiagnosticCheck
                {
                    Category = "HFP / Telefonie",
                    Name = $"Telefonie-Zugriff: {t.Name}",
                    Status = access == DeviceAccessStatus.Allowed ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
                    Value = access.ToString(),
                    Probe = "PhoneLineTransportDevice.RequestAccessAsync()",
                    Detail = access == DeviceAccessStatus.Allowed
                        ? "Windows erlaubt dieser App den Telefoniezugriff auf dieses Gerät."
                        : "Zugriff verweigert. DeniedByUser -> Datenschutzeinstellungen (Anrufe). DeniedBySystem -> Richtlinie oder fehlende Berechtigung."
                });
            }
            catch (Exception ex)
            {
                checks.Add(Failed("HFP / Telefonie", $"Telefonie-Zugriff: {t.Name}", "RequestAccessAsync()", ex));
            }

            Safe(checks, "HFP / Telefonie", $"  Audio-Route: {t.Name}",
                "PhoneLineTransportDevice.AudioRoutingStatus",
                () => device.AudioRoutingStatus.ToString(),
                v => v == nameof(TransportDeviceAudioRoutingStatus.CanRouteToLocalDevice)
                    ? DiagnosticStatus.Ok
                    : DiagnosticStatus.Warning,
                "CanRouteToLocalDevice bedeutet: Gesprächsaudio kann auf PC-Mikrofon und PC-Lautsprecher gelegt werden.");

            Safe(checks, "HFP / Telefonie", $"  App registriert: {t.Name}",
                "PhoneLineTransportDevice.IsRegistered()",
                () => device.IsRegistered().ToString(),
                _ => DiagnosticStatus.Info,
                "RegisterApp() meldet die App als Telefonie-Client für dieses Transportgerät an.");

            Safe(checks, "HFP / Telefonie", $"  In-Band Ringing: {t.Name}",
                "PhoneLineTransportDevice.InBandRingingEnabled",
                () => device.InBandRingingEnabled.ToString(),
                _ => DiagnosticStatus.Info,
                "Wenn aktiv, liefert das Telefon den Klingelton über die Bluetooth-Audioverbindung.");
        }
    }

    // --------------------------------------------------------- Phone lines

    private static async Task AddPhoneLinesAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        PhoneCallStore store;
        try
        {
            store = await PhoneCallManager.RequestStoreAsync();
            checks.Add(new DiagnosticCheck
            {
                Category = "Telefonleitungen",
                Name = "PhoneCallStore",
                Status = DiagnosticStatus.Ok,
                Value = "Zugriff erteilt",
                Probe = "PhoneCallManager.RequestStoreAsync()",
                Detail = "Der Telefonie-Store ist erreichbar - auch ohne MSIX-Paket."
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Telefonleitungen", "PhoneCallStore", "PhoneCallManager.RequestStoreAsync()", ex));
            return;
        }

        try
        {
            var defaultLine = await store.GetDefaultLineAsync();
            checks.Add(new DiagnosticCheck
            {
                Category = "Telefonleitungen",
                Name = "Standardleitung",
                Status = defaultLine == Guid.Empty ? DiagnosticStatus.Warning : DiagnosticStatus.Ok,
                Value = defaultLine == Guid.Empty ? "Keine" : defaultLine.ToString(),
                Probe = "PhoneCallStore.GetDefaultLineAsync()"
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Telefonleitungen", "Standardleitung", "PhoneCallStore.GetDefaultLineAsync()", ex,
                "HRESULT 0x8007139F (ERROR_INVALID_STATE) tritt auf, wenn dem System überhaupt keine Telefonleitung bekannt ist - also solange kein Telefon per HFP verbunden ist."));
        }

        // Enumerate lines through the watcher; this is the only way to see Bluetooth lines.
        try
        {
            var watcher = store.RequestLineWatcher();
            var lineIds = new List<Guid>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            watcher.LineAdded += (_, e) => { lock (lineIds) { lineIds.Add(e.LineId); } };
            watcher.EnumerationCompleted += (_, _) => done.TrySetResult(true);
            watcher.Stopped += (_, _) => done.TrySetResult(false);
            watcher.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await done.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Report whatever the watcher managed to deliver.
            }
            finally
            {
                try { watcher.Stop(); } catch { /* watcher already stopped */ }
            }

            Guid[] found;
            lock (lineIds) { found = lineIds.ToArray(); }

            checks.Add(new DiagnosticCheck
            {
                Category = "Telefonleitungen",
                Name = "PhoneLineWatcher",
                Status = found.Length > 0 ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                Value = $"{found.Length} Leitung(en)",
                Probe = "PhoneCallStore.RequestLineWatcher() + LineAdded",
                Detail = found.Length == 0
                    ? "Keine Telefonleitung gefunden. Ohne verbundenes HFP-Telefon ist das der Normalfall."
                    : null
            });

            foreach (var id in found)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var line = await PhoneLine.FromIdAsync(id);
                    checks.Add(new DiagnosticCheck
                    {
                        Category = "Telefonleitungen",
                        Name = $"Leitung: {line.DisplayName}",
                        Status = line.Transport == PhoneLineTransport.Bluetooth
                            ? DiagnosticStatus.Ok
                            : DiagnosticStatus.Info,
                        Value = $"{line.Transport} / {line.NetworkState}",
                        Probe = "PhoneLine.FromIdAsync(lineId)",
                        Detail = $"Id: {line.Id}\nNetz: {line.NetworkName}\nCanDial: {line.CanDial}\nTransportDeviceId: {line.TransportDeviceId}"
                    });

                    var calls = await line.GetAllActivePhoneCallsAsync();
                    checks.Add(new DiagnosticCheck
                    {
                        Category = "Telefonleitungen",
                        Name = $"  Aktive Anrufe: {line.DisplayName}",
                        Status = DiagnosticStatus.Info,
                        Value = $"{calls.AllActivePhoneCalls.Count} (Status {calls.OperationStatus})",
                        Probe = "PhoneLine.GetAllActivePhoneCallsAsync()",
                        Detail = string.Join("\n", calls.AllActivePhoneCalls.Select(c => $"- {c.CallId}: {c.Status}"))
                    });
                }
                catch (Exception ex)
                {
                    checks.Add(Failed("Telefonleitungen", $"Leitung {id}", "PhoneLine.FromIdAsync()", ex));
                }
            }
        }
        catch (Exception ex)
        {
            checks.Add(Failed("Telefonleitungen", "PhoneLineWatcher", "PhoneCallStore.RequestLineWatcher()", ex));
        }
    }

    // ------------------------------------------------------------- Audio

    private static async Task AddAudioAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        Safe(checks, "Audio / Call State", "Anruf aktiv", "PhoneCallManager.IsCallActive",
            () => PhoneCallManager.IsCallActive.ToString(), _ => DiagnosticStatus.Info);
        Safe(checks, "Audio / Call State", "Eingehender Anruf", "PhoneCallManager.IsCallIncoming",
            () => PhoneCallManager.IsCallIncoming.ToString(), _ => DiagnosticStatus.Info);

        var classes = new[]
        {
            ("Wiedergabegeräte", DeviceClass.AudioRender),
            ("Aufnahmegeräte", DeviceClass.AudioCapture)
        };

        foreach (var (label, cls) in classes)
        {
            try
            {
                var devs = await DeviceInformation.FindAllAsync(cls);
                var hfp = devs.Where(d =>
                    d.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("Freisprech", StringComparison.OrdinalIgnoreCase)).ToList();

                checks.Add(new DiagnosticCheck
                {
                    Category = "Audio / Call State",
                    Name = label,
                    Status = DiagnosticStatus.Info,
                    Value = $"{devs.Count} gesamt, {hfp.Count} Hands-Free",
                    Probe = $"DeviceInformation.FindAllAsync(DeviceClass.{cls})",
                    Detail = string.Join("\n", devs.Select(d => (d.IsDefault ? "* " : "- ") + d.Name))
                });
            }
            catch (Exception ex)
            {
                checks.Add(Failed("Audio / Call State", label, $"FindAllAsync({cls})", ex));
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    // ------------------------------------------------------------- Helpers

    private static void Safe(List<DiagnosticCheck> checks, string category, string name, string probe,
                             Func<string> read, Func<string, DiagnosticStatus> rate, string? detail = null)
    {
        try
        {
            var v = read();
            checks.Add(new DiagnosticCheck
            {
                Category = category,
                Name = name,
                Status = rate(v),
                Value = v,
                Probe = probe,
                Detail = detail
            });
        }
        catch (Exception ex)
        {
            checks.Add(Failed(category, name, probe, ex));
        }
    }

    private static DiagnosticCheck Failed(string category, string name, string probe, Exception ex, string? hint = null)
    {
        var e = ex is AggregateException a && a.InnerException is not null ? a.InnerException : ex;
        var text = $"{e.GetType().Name} HRESULT=0x{e.HResult:X8}: {e.Message.Replace("\r", " ").Replace("\n", " ").Trim()}";
        return new DiagnosticCheck
        {
            Category = category,
            Name = name,
            Status = DiagnosticStatus.Failed,
            Value = $"Fehler 0x{e.HResult:X8}",
            Probe = probe,
            Detail = hint is null ? text : text + "\n\n" + hint
        };
    }
}
