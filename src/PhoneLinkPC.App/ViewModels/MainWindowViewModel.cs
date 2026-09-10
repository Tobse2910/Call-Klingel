using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Models;
using PhoneLinkPC.Core.Platform;
using PhoneLinkPC.Core.Privacy;
using PhoneLinkPC.Infrastructure;
using PhoneLinkPC.Core.Audio;
using PhoneLinkPC.Infrastructure.Contacts;
using PhoneLinkPC.Infrastructure.Settings;
using Serilog;

namespace PhoneLinkPC.App.ViewModels;

/// <summary>
/// Shell view model. It owns the telephony backend and exposes only abstract state, so the
/// views never touch a Windows or Linux API.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ITelephonyService _telephony;
    private readonly IAudioBridge _audio;
    private readonly JsonSettingsStore _settingsStore = new();
    private AppSettings _settings = new();
    private CancellationTokenSource? _watchdog;
    private readonly JsonCallHistoryStore _history = new();

    public MainWindowViewModel()
    {
        _telephony = TelephonyBackendFactory.CreateService();
        var diagnostics = TelephonyBackendFactory.CreateDiagnostics();
        var contacts = new JsonContactStore();

        Start = new StartViewModel(_telephony);
        Diagnostics = new DiagnosticsViewModel(diagnostics, _telephony);
        _audio = TelephonyBackendFactory.CreateAudioBridge();
        Call = new CallViewModel(_telephony, contacts, _audio);

        Audio = new AudioSettingsViewModel(_audio);
        Contacts = new ContactsViewModel(contacts, _telephony);
        History = new HistoryViewModel(_history);
        Update = new UpdateViewModel(new Services.VelopackUpdateService());

        NavItems =
        [
            new NavItem("Start", "Übersicht und Geräte", true, "IconHome"),
            new NavItem("Telefonie-Diagnose", "Was das System wirklich erlaubt", true, "IconActivity"),
            new NavItem("Audio", "Mikrofon und Lautsprecher", true, "IconSpeaker"),
            new NavItem("Kontakte", "Namen statt Rufnummern", true, "IconUser"),
            new NavItem("Verlauf", "Wer wann angerufen hat", true, "IconClock")
        ];

        SelectedNav = NavItems[0];

        PlatformLabel = HostPlatform.DisplayName;
        BackendLabel = _telephony.BackendName;

        _telephony.ConnectionStateChanged += (_, e) =>
            Dispatcher.UIThread.Post(() =>
            {
                ConnectionState = e.State;
                StatusMessage = e.Message;
            });

        // The backend reports its own timing notes on the protocol channel, marked so they
        // can be told apart from what the phone actually sent. Those are always logged.
        //
        // The phone's own AT traffic goes to the log only at debug level. Without it a call
        // that never reaches the screen cannot be told apart from a call the phone never
        // announced - the difference between a parsing bug and a silent phone, and the two
        // need opposite fixes. Numbers are masked on the way in, because the privacy rule
        // does not care which channel a number arrives on.
        _telephony.ProtocolTrace += (_, line) =>
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                Log.Information("Verbindung: {Note}", line[3..]);
            else
                Log.Debug("HFP <- {Line}", PhoneNumberMasker.MaskInText(line));
        };

        _telephony.DeviceConnected += (_, e) =>
            Dispatcher.UIThread.Post(() =>
            {
                PhoneName = e.Device.Name;
                BatteryPercent = e.Device.BatteryPercent;
                SignalStrength = e.Device.SignalStrength;
            });

        _telephony.DeviceDisconnected += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                PhoneName = null;
                BatteryPercent = null;
                SignalStrength = null;
            });

        // Call plumbing: every state change drives the call window.
        _telephony.IncomingCall += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            Log.Information("Eingehender Anruf von {Number}", PhoneNumberMasker.Mask(e.Call.PhoneNumber));
            Call.Update(e.Call);
            CallWindowRequested?.Invoke(this, EventArgs.Empty);
        });

        _telephony.CallStateChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            Call.Update(e.Call.State == CallState.Ended ? null : e.Call);
            if (e.Call.State is CallState.Dialing or CallState.Active)
                CallWindowRequested?.Invoke(this, EventArgs.Empty);
        });

        _telephony.CallerInfoChanged += (_, e) => Dispatcher.UIThread.Post(() => Call.Update(e.Call));

        _telephony.CallEnded += (_, e) => Dispatcher.UIThread.Post(async () =>
        {
            await RecordHistoryAsync(e.Call);
            Call.Update(null);
            CallWindowDismissed?.Invoke(this, EventArgs.Empty);
        });

        // A simulated call closes its window the same way a real one does.
        Call.SimulationEnded += (_, _) =>
            Dispatcher.UIThread.Post(() => CallWindowDismissed?.Invoke(this, EventArgs.Empty));
    }

    public StartViewModel Start { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public CallViewModel Call { get; }
    public AudioSettingsViewModel Audio { get; }
    public ContactsViewModel Contacts { get; }
    public HistoryViewModel History { get; }
    public UpdateViewModel Update { get; }
    public ObservableCollection<NavItem> NavItems { get; }

    public string PlatformLabel { get; }
    public string BackendLabel { get; }

    /// <summary>Raised when a call needs the call window on screen.</summary>
    public event EventHandler? CallWindowRequested;

    /// <summary>Raised when the call is over and the window should close.</summary>
    public event EventHandler? CallWindowDismissed;

    [ObservableProperty] private NavItem _selectedNav;
    [ObservableProperty] private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    [ObservableProperty] private string? _phoneName;
    [ObservableProperty] private int? _batteryPercent;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private int? _signalStrength;

    public bool IsStartSelected => SelectedNav?.Title == "Start";
    public bool IsDiagnosticsSelected => SelectedNav?.Title == "Telefonie-Diagnose";
    public bool IsAudioSelected => SelectedNav?.Title == "Audio";
    public bool IsContactsSelected => SelectedNav?.Title == "Kontakte";
    public bool IsHistorySelected => SelectedNav?.Title == "Verlauf";
    public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;
    public bool HasBattery => BatteryPercent is not null;

    /// <summary>
    /// Deliberately approximate. HFP reports the battery as a level from 0 to 5 and
    /// nothing finer: measured on 2026-09-10 against a Samsung S26 Ultra, AT+CBC is
    /// answered with ERROR, and the Apple (XAPL) and HFP 1.7 (BIND) battery extensions
    /// both run the other way - they let the hands-free unit report its own battery to the
    /// phone. A bare "80 %" claims a precision the protocol cannot deliver; the value is
    /// really one of six steps, so it is shown as the estimate it is.
    /// </summary>
    public string BatteryText => BatteryPercent is { } p ? $"Akku ca. {p} %" : "";

    /// <summary>Spells out the coarse scale, so the estimate is checkable rather than mysterious.</summary>
    public string BatteryTooltip => BatteryPercent is { } p
        ? $"Stufe {(int)Math.Round(p / 20.0)} von 5. Feiner meldet das Telefon den Akkustand über "
          + "das Freisprech-Profil nicht."
        : "";

    public bool HasSignal => SignalStrength is not null;

    /// <summary>HFP reports signal on a 0-5 scale.</summary>
    public string SignalText => SignalStrength is { } s ? $"Empfang {s}/5" : "";

    public string ConnectionLabel => ConnectionState switch
    {
        DeviceConnectionState.Connected => PhoneName ?? "Verbunden",
        DeviceConnectionState.Connecting => "Verbinde ...",
        // "Fehler" tells the user nothing they can act on; name the actual situation.
        DeviceConnectionState.Error => "Telefon nicht erreichbar",
        _ => "Nicht verbunden"
    };

    /// <summary>True while a connection attempt is failing, so the UI can offer help.</summary>
    public bool HasConnectionProblem => ConnectionState == DeviceConnectionState.Error;

    /// <summary>
    /// Called once when the window appears: loads the settings and, if wanted, connects to
    /// the phone without the user having to click anything.
    /// </summary>
    public async Task StartupAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        Audio.Apply(_settings, _settingsStore);
        AutoConnect = _settings.AutoConnect;

        Log.Information("Start: AutoConnect={Auto}, letztes Gerät={Device}",
            _settings.AutoConnect, _settings.LastDeviceName ?? "(keines)");

        if (_settings.AutoConnect) await ConnectPhoneAsync();

        if (_settings.AutoReconnect) StartWatchdog();

        // Deliberately last and deliberately unawaited: connecting to the phone is what the
        // user is waiting for, and an update check that hangs on a slow network must not
        // delay it by a single second.
        _ = Update.CheckOnStartupAsync();
    }

    /// <summary>
    /// Keeps the connection alive. Bluetooth links drop when the phone leaves the room or
    /// the screen sleeps, and reconnecting by hand every time defeats the purpose.
    /// </summary>
    private void StartWatchdog()
    {
        _watchdog?.Cancel();
        _watchdog = new CancellationTokenSource();
        var ct = _watchdog.Token;

        _ = Task.Run(async () =>
        {
            var delaySeconds = 20;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

                    if (ConnectionState == DeviceConnectionState.Connected || IsConnecting) continue;
                    if (_settings.LastDeviceId is null) continue;

                    var result = await _telephony.ConnectAsync(_settings.LastDeviceId, ct);

                    if (result.Success)
                    {
                        Log.Information("Automatisch wieder mit dem Telefon verbunden.");
                        delaySeconds = 20;
                    }
                    else
                    {
                        // Back off when the phone simply is not there: retrying every 20s
                        // fills the log and keeps the radio busy for nothing.
                        delaySeconds = Math.Min(delaySeconds * 2, 120);
                        Log.Debug("Automatischer Versuch fehlgeschlagen, nächster in {Delay}s",
                            delaySeconds);

                        // Keep the announced wait honest as the backoff grows, otherwise the
                        // message promises 20 seconds while the app waits two minutes.
                        var next = delaySeconds;
                        Dispatcher.UIThread.Post(() => StatusMessage =
                            $"Telefon nicht erreichbar. Nächster Versuch in {next} Sekunden.");
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Automatischer Verbindungsversuch fehlgeschlagen");
                }
            }
        }, ct);
    }

    public void StopWatchdog()
    {
        _watchdog?.Cancel();
        _watchdog?.Dispose();
        _watchdog = null;
    }

    partial void OnAutoConnectChanged(bool value)
    {
        _settings.AutoConnect = value;
        _settings.AutoReconnect = value;
        _ = _settingsStore.SaveAsync(_settings);

        if (value) StartWatchdog();
        else StopWatchdog();
    }

    /// <summary>Writes the finished call to the local history.</summary>
    private async Task RecordHistoryAsync(CallInfo call)
    {
        if (Call.IsSimulation) return;

        try
        {
            var answered = call.AnsweredAt is not null;
            var status = call.Direction == CallDirection.Outgoing
                ? CallHistoryStatus.Outgoing
                : answered ? CallHistoryStatus.Completed : CallHistoryStatus.Missed;

            await _history.AddAsync(new CallHistoryEntry
            {
                Id = Guid.NewGuid(),
                PhoneNumber = call.PhoneNumber,
                ContactName = call.ContactName,
                Direction = call.Direction,
                StartTime = call.StartedAt,
                Duration = answered ? DateTimeOffset.Now - call.AnsweredAt!.Value : TimeSpan.Zero,
                Status = status
            });

            await History.LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Verlaufseintrag konnte nicht gespeichert werden");
        }
    }

    /// <summary>
    /// Shows a simulated incoming call. Clearly marked as a simulation - it never touches
    /// the phone, and exists so the call window can be reviewed without waiting to be rung.
    /// </summary>
    [RelayCommand]
    private void SimulateCall()
    {
        Call.StartSimulation("Max Mustermann", "+49 176 12345678");
        CallWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Connects to the first paired phone that offers hands-free.</summary>
    [RelayCommand]
    private async Task ConnectPhoneAsync()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        StatusMessage = null;

        // Connecting is the step users feel, so its cost is measured rather than guessed.
        // The two phases are timed separately because both do Bluetooth service discovery
        // and only the numbers say which one is worth optimising.
        var lookupWatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var devices = await _telephony.GetDevicesAsync();
            var phone = devices.FirstOrDefault(d => d.SupportsHandsFree);
            lookupWatch.Stop();

            if (phone is null)
            {
                StatusMessage = "Kein Telefon mit Freisprech-Profil gekoppelt.";
                Log.Information("Verbinden abgebrochen: kein HFP-Telefon. Suche dauerte {Ms} ms",
                    lookupWatch.ElapsedMilliseconds);
                return;
            }

            var connectWatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await _telephony.ConnectAsync(phone.Id);
            connectWatch.Stop();

            // A failed attempt is not the end of it - the watchdog tries again on its own.
            // Saying so is the difference between "the app is broken" and "wait a moment":
            // on 2026-09-10 a one-off stalled handshake looked like a dead app, because the
            // retry that would have fixed it 20 seconds later was never mentioned.
            StatusMessage = result.Success
                ? null
                : _settings.AutoReconnect
                    ? $"{result.Error} Der nächste Versuch läuft automatisch in etwa 20 Sekunden."
                    : result.Error;

            Log.Information(
                "Verbindungsversuch {Outcome}: Gerätesuche {LookupMs} ms, Verbindungsaufbau {ConnectMs} ms, gesamt {TotalMs} ms",
                result.Success ? "erfolgreich" : "fehlgeschlagen",
                lookupWatch.ElapsedMilliseconds,
                connectWatch.ElapsedMilliseconds,
                lookupWatch.ElapsedMilliseconds + connectWatch.ElapsedMilliseconds);

            if (result.Success)
            {
                Log.Information("Mit Telefon verbunden: {Name}", phone.Name);

                // Remember the phone so the next start can connect without asking.
                _settings.LastDeviceId = phone.Id;
                _settings.LastDeviceName = phone.Name;
                await _settingsStore.SaveAsync(_settings);
            }
            else
            {
                Log.Warning("Automatische Verbindung fehlgeschlagen: {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Log.Error(ex, "Verbindung zum Telefon fehlgeschlagen");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectPhoneAsync()
    {
        try
        {
            // A deliberate disconnect must not be undone by the watchdog two seconds later.
            StopWatchdog();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await _telephony.DisconnectAsync();
            watch.Stop();

            // Logged because the gap between disconnect and reconnect is what decides
            // whether the RFCOMM channel is free again - without the timestamp, a slow
            // reconnect cannot be told apart from a channel that was never released.
            Log.Information("Telefon getrennt in {Ms} ms", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) { Log.Error(ex, "Trennen fehlgeschlagen"); }
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value is { IsEnabled: false })
        {
            SelectedNav = NavItems.First(n => n.IsEnabled);
            return;
        }

        OnPropertyChanged(nameof(IsStartSelected));
        OnPropertyChanged(nameof(IsDiagnosticsSelected));
        OnPropertyChanged(nameof(IsAudioSelected));
        OnPropertyChanged(nameof(IsContactsSelected));
        OnPropertyChanged(nameof(IsHistorySelected));
    }

    partial void OnConnectionStateChanged(DeviceConnectionState value)
    {
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(HasConnectionProblem));
    }

    partial void OnPhoneNameChanged(string? value) => OnPropertyChanged(nameof(ConnectionLabel));

    partial void OnBatteryPercentChanged(int? value)
    {
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(BatteryText));
    }

    partial void OnSignalStrengthChanged(int? value)
    {
        OnPropertyChanged(nameof(HasSignal));
        OnPropertyChanged(nameof(SignalText));
    }
}

/// <summary>
/// A navigation entry. <paramref name="IconKey"/> names a geometry in Icons.axaml; the view
/// resolves it, so the view model stays free of drawing details.
/// </summary>
public sealed record NavItem(string Title, string Subtitle, bool IsEnabled, string IconKey)
{
    /// <summary>Geometry for the view, resolved from the application resources.</summary>
    public Avalonia.Media.Geometry? Icon =>
        Avalonia.Application.Current?.TryGetResource(IconKey, null, out var found) == true
            ? found as Avalonia.Media.Geometry
            : null;
}
