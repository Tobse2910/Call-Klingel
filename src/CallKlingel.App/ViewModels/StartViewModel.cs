using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Bluetooth;
using CallKlingel.Core.Models;
using CallKlingel.Core.Platform;
using CallKlingel.Infrastructure;
using Serilog;

namespace CallKlingel.App.ViewModels;

/// <summary>
/// Start page: platform, backend status, paired phones, and pairing a new one.
///
/// Pairing happens inside the app through DeviceInformationPairing. The user only has to
/// confirm on the phone - which is the phone's own security decision and cannot be skipped.
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    private readonly ITelephonyService _telephony;

    private readonly DispatcherTimer _clock;

    public StartViewModel(ITelephonyService telephony)
    {
        _telephony = telephony;

        // The mock screen shows a real clock, which is what makes it read as a device
        // rather than an illustration. It ticks ON the minute rather than every 20 seconds:
        // a fixed interval leaves the display on the previous minute for up to a third of a
        // minute, and on a screen imitating a phone that reads as a frozen app.
        _clock = new DispatcherTimer { Interval = UntilNextMinute() };
        _clock.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(ClockText));
            _clock.Interval = UntilNextMinute();
        };
        _clock.Start();

        // The page has to follow the live connection, not just the paired-device list.
        // Without this it kept announcing "Verbunden" after the link had dropped.
        ConnectionState = telephony.ConnectionState;
        telephony.ConnectionStateChanged += (_, e) =>
            Dispatcher.UIThread.Post(() =>
            {
                ConnectionState = e.State;
                Refresh();
            });

        // Battery and signal arrive here, and only here. The paired-device list carries
        // whatever was true during the last scan, which is why the drawn handset showed an
        // empty red battery while the sidebar next to it read "Akku ca. 60 %" - two answers
        // to one question, on one screen. The backend re-raises DeviceConnected on every
        // indicator change, so following it keeps the mock screen honest.
        LiveDevice = telephony.ConnectedDevice;

        telephony.DeviceConnected += (_, e) =>
            Dispatcher.UIThread.Post(() =>
            {
                LiveDevice = e.Device;
                Refresh();
            });

        telephony.DeviceDisconnected += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                LiveDevice = null;
                Refresh();
            });
        PlatformName = HostPlatform.DisplayName;
        BackendName = telephony.BackendName;
        BackendAvailable = telephony.IsAvailable;
        BackendStatus = telephony.IsAvailable
            ? $"{telephony.BackendName} aktiv"
            : $"{telephony.BackendName} auf dieser Plattform nicht verfügbar";
    }

    public string PlatformName { get; }
    public string BackendName { get; }
    public bool BackendAvailable { get; }

    [ObservableProperty] private string _backendStatus;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private string? _scanMessage;
    [ObservableProperty] private string? _pairingMessage;
    [ObservableProperty] private bool _hasScanned;

    /// <summary>
    /// Why no phone is usable, when the scan comes back without one. Without this the app
    /// could only show an empty list, even in the case where the phone is bonded over
    /// Bluetooth LE only - visible in the system's Bluetooth list, useless for telephony.
    /// </summary>
    [ObservableProperty] private BondDiagnosis? _bondDiagnosis;

    /// <summary>The live link state, mirrored from the backend.</summary>
    [ObservableProperty] private DeviceConnectionState _connectionState;

    /// <summary>
    /// The phone as the live connection currently reports it, including battery and signal.
    /// Null while nothing is connected - which is a different thing from "no phone paired".
    /// </summary>
    [ObservableProperty] private PhoneDevice? _liveDevice;

    /// <summary>Milliseconds until the wall clock reaches the next full minute.</summary>
    private static TimeSpan UntilNextMinute()
    {
        var now = DateTime.Now;
        var remaining = TimeSpan.FromMinutes(1)
                        - TimeSpan.FromSeconds(now.Second)
                        - TimeSpan.FromMilliseconds(now.Millisecond);

        // Never schedule a zero or negative interval, which would spin the timer.
        return remaining > TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMinutes(1);
    }

    /// <summary>Phones Windows already knows.</summary>
    public ObservableCollection<PhoneDevice> Devices { get; } = [];

    /// <summary>Phones in range that are not paired yet.</summary>
    public ObservableCollection<PhoneDevice> NewDevices { get; } = [];

    public bool IsBusy => IsScanning || IsDiscovering || IsPairing;

    /// <summary>Nothing paired: offer pairing instead of showing an empty list.</summary>
    public bool ShowPairingHint => !IsBusy && HasScanned && Devices.Count == 0 && !ShowBondProblem;

    /// <summary>Paired but not connected - the state that used to look like success.</summary>
    public bool ShowNotConnectedHint =>
        !IsBusy && HasHandsFreePhone && ConnectionState != DeviceConnectionState.Connected
        && ConnectionState != DeviceConnectionState.Connecting;

    /// <summary>
    /// True when the bond itself is the reason telephony cannot start - most importantly
    /// an LE-only bond, which no amount of re-scanning will fix.
    /// </summary>
    public bool ShowBondProblem =>
        !IsBusy && HasScanned && BondDiagnosis is { Problem: BondProblem.LowEnergyOnlyBond };

    public string BondProblemTitle => "Falsche Kopplungsart";

    public string? BondProblemText => BondDiagnosis?.Summary;

    public string? BondRemedyText => BondDiagnosis?.Remedy;

    /// <summary>The stale bond can be removed from here; the phone side stays manual.</summary>
    public bool CanRemoveStaleBond =>
        ShowBondProblem && !string.IsNullOrWhiteSpace(BondDiagnosis?.StaleLowEnergyEndpointId);

    /// <summary>Set once the user asks for the pairing panel on a PC that already has a phone.</summary>
    [ObservableProperty] private bool _pairingExpanded;

    /// <summary>
    /// Pairing is the first thing a new user needs and pure noise afterwards. Once a phone
    /// is ready the panel folds away behind a single link, so the screen shows the working
    /// connection instead of instructions for a problem that is already solved.
    /// </summary>
    public bool ShowPairingSection => !HasHandsFreePhone || PairingExpanded;

    /// <summary>The link that brings the panel back, shown only while it is folded away.</summary>
    public bool ShowPairingToggle => HasHandsFreePhone && !PairingExpanded;

    [RelayCommand]
    private void ExpandPairing()
    {
        PairingExpanded = true;
        Refresh();
    }

    /// <summary>Paired, but nothing that can carry a call.</summary>
    public bool ShowNoHfpHint => !IsBusy && Devices.Count > 0 && Devices.All(d => !d.SupportsHandsFree);

    public bool ShowNewDevices => NewDevices.Count > 0;

    // --- Hero state, so the view stays free of logic ---

    /// <summary>
    /// A paired phone that offers hands-free. This says nothing about whether a connection
    /// exists right now - it is about pairing, and it decides whether pairing help is shown.
    /// </summary>
    public bool HasHandsFreePhone => Devices.Any(d => d.SupportsHandsFree);

    /// <summary>
    /// Ready means a live connection, not a paired device.
    ///
    /// This used to ask only whether some paired phone supported hands-free, so the page
    /// announced "Verbunden" with a green tick while the sidebar next to it said the phone
    /// was unreachable - seen on 2026-09-10 after five failed RFCOMM attempts. Two claims
    /// about the same fact, one of them false.
    /// </summary>
    public bool IsPhoneReady => ConnectionState == DeviceConnectionState.Connected;

    public string HeroTitle => ConnectionState switch
    {
        DeviceConnectionState.Connected => "Verbunden",
        DeviceConnectionState.Connecting => "Verbinde ...",
        DeviceConnectionState.Error => "Telefon nicht erreichbar",
        _ => HasHandsFreePhone ? "Nicht verbunden" : "Noch kein Telefon"
    };

    public string HeroSubtitle
    {
        get
        {
            var phone = Phone;
            if (phone is null) return "Koppel dein Telefon, um loszulegen.";

            var name = ShortName(phone.Name);
            return ConnectionState switch
            {
                DeviceConnectionState.Connected => $"Dein {name} ist bereit.",
                DeviceConnectionState.Connecting => $"Verbinde mich mit deinem {name} ...",
                DeviceConnectionState.Error =>
                    $"Dein {name} ist gekoppelt, antwortet aber gerade nicht.",
                _ => $"Dein {name} ist gekoppelt, aber nicht verbunden."
            };
        }
    }

    public string ReadyHint => ConnectionState switch
    {
        DeviceConnectionState.Connected => "Bereit, wenn es klingelt.",
        DeviceConnectionState.Connecting => "Einen Moment noch.",
        _ when HasHandsFreePhone => "Anrufe erscheinen hier, sobald die Verbindung steht.",
        _ => "Sobald ein Telefon gekoppelt ist, erscheinen Anrufe hier."
    };

    /// <summary>Label drawn on the phone illustration.</summary>
    public string PhoneLabel =>
        Phone?.Name is { } name ? ShortName(name) : "Kein Telefon";

    // --- Live values drawn on the mock screen ---

    public string ClockText => DateTime.Now.ToString("HH:mm");

    /// <summary>
    /// The connected phone if there is one, otherwise the paired one. The order matters:
    /// only the live device carries current battery and signal values.
    /// </summary>
    private PhoneDevice? Phone => LiveDevice ?? Devices.FirstOrDefault(d => d.SupportsHandsFree);

    public int? BatteryPercent => Phone?.BatteryPercent;
    public int? SignalStrength => Phone?.SignalStrength;

    public int SignalBars => SignalStrength is { } s ? (int)Math.Round(s / 5.0 * 4) : 0;

    /// <summary>
    /// True once the phone has actually reported a level. Without this the drawn battery
    /// showed an unknown value as an empty red cell - an alarm about a measurement that was
    /// never taken.
    /// </summary>
    public bool HasBatteryReading => BatteryPercent is not null;

    /// <summary>Battery pill is 14px wide inside its outline.</summary>
    public double BatteryFillWidth => Math.Max(1.5, (BatteryPercent ?? 0) / 100.0 * 14.0);

    public IBrush BatteryFillBrush => BatteryPercent switch
    {
        null => Brush("MutedBrush"),
        <= 15 => Brush("NegativeBrush"),
        <= 35 => Brush("WarnBrush"),
        _ => Brush("PositiveBrush")
    };

    public IBrush Bar1 => BarBrush(1);
    public IBrush Bar2 => BarBrush(2);
    public IBrush Bar3 => BarBrush(3);
    public IBrush Bar4 => BarBrush(4);

    private IBrush BarBrush(int index) =>
        SignalBars >= index ? Brush("TextBrush") : Brush("StrokeBrush");

    public double DeviceGlowOpacity => IsPhoneReady ? 0.5 : 0.15;

    public IBrush DeviceGlyphBrush =>
        IsPhoneReady ? Brush("PositiveBrush") : Brush("MutedBrush");

    /// <summary>
    /// Follows the same state as the headline. Anything else puts two answers to one
    /// question on the same screen - "Verbinde ..." next to "Getrennt" was exactly that.
    /// </summary>
    public string DeviceStateText => ConnectionState switch
    {
        DeviceConnectionState.Connected => "Bereit",
        DeviceConnectionState.Connecting => "Verbindet",
        DeviceConnectionState.Error => "Nicht erreichbar",
        _ => "Getrennt"
    };

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var found) == true
            ? found as IBrush ?? Brushes.Gray
            : Brushes.Gray;

    /// <summary>Dim the phone glyph while nothing is connected.</summary>
    public double PhoneGlyphOpacity => IsPhoneReady ? 1.0 : 0.35;

    /// <summary>Bluetooth names are long; keep the recognisable part.</summary>
    private static string ShortName(string name)
    {
        var cut = name.IndexOf(" von ", StringComparison.OrdinalIgnoreCase);
        var text = cut > 0 ? name[..cut] : name;
        return text.Length > 22 ? text[..22].TrimEnd() + "..." : text;
    }


    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        IsScanning = true;
        ScanMessage = null;
        Refresh();

        try
        {
            await _telephony.InitializeAsync();
            var found = await _telephony.ScanDevicesAsync();

            Devices.Clear();
            foreach (var d in found) Devices.Add(d);

            var hfp = found.Count(d => d.SupportsHandsFree);
            HasScanned = true;
            ScanMessage = found.Count == 0
                ? null
                : $"{found.Count} Gerät(e) gekoppelt, davon {hfp} mit Freisprech-Profil (HFP).";

            // An empty list is not an answer. Ask what is actually wrong with the bond, so
            // the LE-only case can be named instead of looking like "no phone here".
            BondDiagnosis = hfp == 0 ? await _telephony.DiagnoseBondAsync() : null;

            Log.Information("Bluetooth-Scan: {Count} Geräte, {Hfp} mit HFP. Bond: {Bond}",
                found.Count, hfp, BondDiagnosis?.Problem.ToString() ?? "ok");
        }
        catch (Exception ex)
        {
            ScanMessage = $"Scan fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Bluetooth-Scan fehlgeschlagen");
        }
        finally
        {
            IsScanning = false;
            Refresh();
        }
    }

    /// <summary>Searches for phones in range that still need pairing.</summary>
    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsBusy) return;

        IsDiscovering = true;
        NewDevices.Clear();
        PairingMessage = "Suche läuft, bis zu 60 Sekunden. Am Telefon die Bluetooth-Seite offen lassen.";
        Refresh();

        try
        {
            var progress = new Progress<PhoneDevice>(d =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (NewDevices.All(x => x.Id != d.Id)) NewDevices.Add(d);
                    Refresh();
                }));

            // Measured on 2026-09-10: a Samsung phone can take well over 30 seconds to show
            // up in a Bluetooth inquiry. A 20 second window reported "nothing found" while
            // the phone was right there, so the search now runs long enough to be honest.
            var found = await _telephony.DiscoverNewDevicesAsync(TimeSpan.FromSeconds(60), progress);

            // The search only ever reports unpaired devices. Telling someone whose phone is
            // already connected to go delete the pairing is not just unhelpful, it is the
            // exact opposite of what they should do.
            PairingMessage = found.Count > 0
                ? $"{found.Count} Gerät(e) in Reichweite. Zum Koppeln auswählen."
                : IsPhoneReady
                    ? "Kein weiteres Telefon in Reichweite. Dein gekoppeltes Telefon taucht "
                      + "hier nicht auf - die Suche zeigt ausschliesslich noch nicht "
                      + "gekoppelte Geräte."
                    : "Kein Gerät gefunden. Prüfen: Bluetooth am Telefon an, die "
                      + "Bluetooth-Seite offen, und der PC in der Geräteliste des Telefons "
                      + "gelöscht, falls er dort noch als alte Kopplung steht.";

            Log.Information("Gerätesuche: {Count} neue Geräte", found.Count);
        }
        catch (Exception ex)
        {
            PairingMessage = $"Suche fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Gerätesuche fehlgeschlagen");
        }
        finally
        {
            IsDiscovering = false;
            Refresh();
        }
    }

    /// <summary>Pairs the selected device and immediately refreshes the paired list.</summary>
    [RelayCommand]
    private async Task PairAsync(PhoneDevice? device)
    {
        if (device is null || IsBusy) return;

        IsPairing = true;
        Refresh();

        try
        {
            var status = new Progress<string>(text =>
                Dispatcher.UIThread.Post(() => PairingMessage = text));

            var result = await _telephony.PairAsync(device.Id, status);

            if (result.Success)
            {
                PairingMessage = $"{device.Name} ist gekoppelt.";
                Log.Information("Gerät gekoppelt: {Name}", device.Name);
                NewDevices.Remove(device);
                IsPairing = false;
                await ScanAsync();
                return;
            }

            PairingMessage = result.Error;
            Log.Warning("Koppeln fehlgeschlagen: {Error}", result.Error);
        }
        catch (Exception ex)
        {
            PairingMessage = $"Koppeln fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Koppeln fehlgeschlagen");
        }
        finally
        {
            IsPairing = false;
            Refresh();
        }
    }

    /// <summary>
    /// Removes the stale LE-only bond so a Classic pairing can start from a clean slate.
    ///
    /// While that bond exists the phone treats this PC as a device it already knows and
    /// never shows its pairing confirmation, which Windows then reports as an
    /// authentication timeout - exactly what happened on 2026-09-10. The phone has to
    /// forget the PC as well; only its own user can do that, so the app says so.
    /// </summary>
    [RelayCommand]
    private async Task RemoveStaleBondAsync()
    {
        var endpointId = BondDiagnosis?.StaleLowEnergyEndpointId;
        if (IsBusy || string.IsNullOrWhiteSpace(endpointId)) return;

        IsPairing = true;
        Refresh();

        try
        {
            var result = await _telephony.UnpairAsync(endpointId);

            if (result.Success)
            {
                PairingMessage =
                    "Die alte Kopplung wurde am PC entfernt. Jetzt am Telefon unter Bluetooth "
                    + "den PC ebenfalls entkoppeln bzw. vergessen - erst dann fragt es beim "
                    + "nächsten Koppeln wieder nach. Danach 'Telefon suchen'.";
                Log.Information("Alte LE-Kopplung entfernt: {Endpoint}", endpointId);
            }
            else
            {
                PairingMessage = result.Error;
                Log.Warning("Alte Kopplung konnte nicht entfernt werden: {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            PairingMessage = $"Kopplung konnte nicht entfernt werden: {ex.Message}";
            Log.Error(ex, "Entfernen der alten Kopplung fehlgeschlagen");
        }
        finally
        {
            IsPairing = false;
            Refresh();
        }

        await ScanAsync();
    }

    /// <summary>
    /// Opens Windows' own pairing dialog. It scans longer than an app can and makes the PC
    /// discoverable at the same time, so it finds phones the in-app search misses.
    /// </summary>
    [RelayCommand]
    private void OpenAddDevice()
    {
        var result = SystemSettings.OpenAddDeviceDialog();
        PairingMessage = result.Success
            ? "Windows-Kopplungsdialog geöffnet. Wähle dort dein Telefon aus. Danach findet "
              + "die App es automatisch."
            : result.Error;

        if (!result.Success)
            Log.Warning("Kopplungsdialog konnte nicht geöffnet werden: {Error}", result.Error);
    }

    /// <summary>Opens the Bluetooth settings page, for removing an old pairing.</summary>
    [RelayCommand]
    private void OpenBluetoothSettings()
    {
        var result = SystemSettings.OpenBluetoothSettings();
        if (!result.Success)
        {
            PairingMessage = result.Error;
            Log.Warning("Bluetooth-Einstellungen konnten nicht geöffnet werden: {Error}", result.Error);
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowPairingHint));
        OnPropertyChanged(nameof(ShowBondProblem));
        OnPropertyChanged(nameof(BondProblemText));
        OnPropertyChanged(nameof(BondRemedyText));
        OnPropertyChanged(nameof(CanRemoveStaleBond));
        OnPropertyChanged(nameof(ShowPairingSection));
        OnPropertyChanged(nameof(ShowPairingToggle));
        OnPropertyChanged(nameof(ShowNoHfpHint));
        OnPropertyChanged(nameof(ShowNewDevices));
        OnPropertyChanged(nameof(IsPhoneReady));
        OnPropertyChanged(nameof(HasHandsFreePhone));
        OnPropertyChanged(nameof(ShowNotConnectedHint));
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroSubtitle));
        OnPropertyChanged(nameof(ReadyHint));
        OnPropertyChanged(nameof(PhoneLabel));
        OnPropertyChanged(nameof(PhoneGlyphOpacity));

        foreach (var name in new[]
                 {
                     nameof(BatteryPercent), nameof(SignalStrength), nameof(SignalBars),
                     nameof(BatteryFillWidth), nameof(BatteryFillBrush),
                     nameof(HasBatteryReading), nameof(Bar1), nameof(Bar2), nameof(Bar3),
                     nameof(Bar4), nameof(DeviceGlowOpacity), nameof(DeviceGlyphBrush),
                     nameof(DeviceStateText), nameof(ClockText)
                 })
            OnPropertyChanged(name);
    }
}
