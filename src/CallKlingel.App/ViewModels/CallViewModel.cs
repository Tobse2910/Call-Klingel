using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Audio;
using CallKlingel.Core.Models;
using CallKlingel.Core.Privacy;
using CallKlingel.Infrastructure.Contacts;
using Serilog;

namespace CallKlingel.App.ViewModels;

/// <summary>
/// Drives the call window. It reacts only to the abstract <see cref="CallState"/>, so the
/// same view model will serve the Linux backend unchanged.
///
/// Every button reports the real result. If the phone refuses to answer a call, the user
/// sees that instead of a UI that pretends the call was taken.
/// </summary>
public sealed partial class CallViewModel : ObservableObject
{
    private readonly ITelephonyService _telephony;
    private readonly JsonContactStore _contacts;
    private readonly IAudioBridge _audio;
    private readonly DispatcherTimer _timer;

    public CallViewModel(ITelephonyService telephony, JsonContactStore contacts, IAudioBridge audio)
    {
        _telephony = telephony;
        _contacts = contacts;
        _audio = audio;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(RecordingDurationText));
        };
    }

    [ObservableProperty] private CallInfo? _call;
    [ObservableProperty] private string? _actionError;

    /// <summary>
    /// True while a simulated call is shown. A simulation must be unmistakable: the buttons
    /// never touch the telephony backend, and the window says so plainly. This exists so the
    /// call window can be reviewed without waiting for someone to ring.
    /// </summary>
    [ObservableProperty] private bool _isSimulation;

    /// <summary>True while this call is being recorded.</summary>
    [ObservableProperty] private bool _isRecording;

    [ObservableProperty] private string? _recordingNotice;

    public bool HasCall => Call is not null;
    public bool IsRinging => Call?.State == CallState.Ringing;
    public bool IsActive => Call?.State is CallState.Active or CallState.Held;
    public bool IsDialing => Call?.State == CallState.Dialing;
    public bool IsMuted => Call?.IsMuted == true;

    public string Title => Call?.State switch
    {
        CallState.Ringing => "Eingehender Anruf",
        CallState.Dialing => "Wird gewählt",
        CallState.Active => "Im Gespräch",
        CallState.Held => "Gehalten",
        _ => "Anruf"
    };

    public string DisplayName => Call?.ContactName
                                 ?? Call?.PhoneNumber
                                 ?? "Unbekannter Anrufer";

    /// <summary>Only shown when a name is known, so the number is never duplicated.</summary>
    public string? SubTitle => Call?.ContactName is not null ? Call.PhoneNumber : null;

    /// <summary>First letter of the caller, used as an avatar placeholder.</summary>
    public string Initial =>
        string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[..1].ToUpperInvariant();

    public string DurationText
    {
        get
        {
            if (Call?.AnsweredAt is not { } start) return "00:00";
            var d = DateTimeOffset.Now - start;
            return d.TotalHours >= 1
                ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
                : $"{d.Minutes:00}:{d.Seconds:00}";
        }
    }

    /// <summary>Called by the shell whenever the backend reports a new call state.</summary>
    public void Update(CallInfo? call)
    {
        Call = call;

        if (call?.State == CallState.Active) _timer.Start();
        else if (call is null || call.State is CallState.Ended or CallState.Idle)
        {
            _timer.Stop();

            // A recording must never outlive the call it belongs to.
            if (IsRecording && !IsSimulation) _ = StopRecordingOnHangupAsync();
            IsRecording = false;
        }

        foreach (var name in new[]
                 {
                     nameof(HasCall), nameof(IsRinging), nameof(IsActive), nameof(IsDialing),
                     nameof(IsMuted), nameof(Title), nameof(DisplayName), nameof(SubTitle),
                     nameof(Initial), nameof(DurationText)
                 })
            OnPropertyChanged(name);

        if (!IsSimulation && call?.PhoneNumber is not null && call.ContactName is null)
            _ = ResolveContactAsync(call);
    }

    private async Task StopRecordingOnHangupAsync()
    {
        try
        {
            var info = await _audio.StopRecordingAsync();
            if (info is not null) Log.Information("Aufnahme automatisch beendet: {File}", info.FileName);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Aufnahme konnte nicht beendet werden");
        }
    }

    private async Task ResolveContactAsync(CallInfo call)
    {
        try
        {
            var contact = await _contacts.ResolveAsync(call.PhoneNumber);
            if (contact is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (Call?.CallId != call.CallId) return;
                Call = Call with { ContactName = contact.Name };
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(SubTitle));
                OnPropertyChanged(nameof(Initial));
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kontaktauflösung fehlgeschlagen");
        }
    }

    [RelayCommand]
    private Task AnswerAsync()
    {
        if (IsSimulation)
        {
            Update(Call! with { State = CallState.Active, AnsweredAt = DateTimeOffset.Now });
            return Task.CompletedTask;
        }

        return RunAsync(() => _telephony.AnswerCallAsync(), "Annehmen");
    }

    [RelayCommand]
    private Task RejectAsync()
    {
        if (IsSimulation) { EndSimulation(); return Task.CompletedTask; }
        return RunAsync(() => _telephony.RejectCallAsync(), "Ablehnen");
    }

    [RelayCommand]
    private Task HangupAsync()
    {
        if (IsSimulation) { EndSimulation(); return Task.CompletedTask; }
        return RunAsync(() => _telephony.HangupAsync(), "Auflegen");
    }

    [RelayCommand]
    private Task ToggleMuteAsync()
    {
        if (IsSimulation)
        {
            Update(Call! with { IsMuted = !IsMuted });
            return Task.CompletedTask;
        }

        return RunAsync(() => _telephony.SetMutedAsync(!IsMuted), "Stummschalten");
    }

    /// <summary>Shows a simulated incoming call so the window can be reviewed.</summary>
    public void StartSimulation(string name, string number)
    {
        IsSimulation = true;
        Update(new CallInfo
        {
            CallId = "simulation",
            State = CallState.Ringing,
            Direction = CallDirection.Incoming,
            PhoneNumber = number,
            ContactName = string.IsNullOrWhiteSpace(name) ? null : name
        });
    }

    private void EndSimulation()
    {
        IsSimulation = false;
        Update(null);
        SimulationEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when a simulated call is dismissed, so the window can close.</summary>
    public event EventHandler? SimulationEnded;

    public string RecordingDurationText
    {
        get
        {
            if (_audio.RecordingStartedAt is not { } start) return string.Empty;
            var d = DateTimeOffset.Now - start;
            return $"{d.Minutes:00}:{d.Seconds:00}";
        }
    }

    /// <summary>
    /// Starts or stops recording in the middle of a call. Deliberately manual: the user
    /// asks the other party first, then presses record.
    /// </summary>
    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsSimulation)
        {
            IsRecording = !IsRecording;
            RecordingNotice = IsRecording
                ? "Simulation: hier würde die Aufnahme laufen."
                : null;
            return;
        }

        try
        {
            if (IsRecording)
            {
                var info = await _audio.StopRecordingAsync();
                IsRecording = false;
                RecordingNotice = info is null
                    ? "Aufnahme beendet."
                    : $"Gespeichert: {info.FileName}";
                Log.Information("Aufnahme beendet: {File}", info?.FileName ?? "(leer)");
                return;
            }

            var result = await _audio.StartRecordingAsync(Call?.PhoneNumber);
            if (result.Success)
            {
                IsRecording = true;
                RecordingNotice = "Aufnahme läuft.";
                _timer.Start();
                Log.Information("Aufnahme gestartet für {Number}",
                    PhoneNumberMasker.Mask(Call?.PhoneNumber));
            }
            else
            {
                RecordingNotice = result.Error;
            }
        }
        catch (Exception ex)
        {
            RecordingNotice = $"Aufnahme fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Aufnahme fehlgeschlagen");
        }
    }

    private async Task RunAsync(Func<Task<TelephonyResult>> action, string what)
    {
        ActionError = null;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                ActionError = result.Error;
                Log.Warning("{Action} fehlgeschlagen: {Error}", what, result.Error);
                return;
            }

            Log.Information("{Action} ausgeführt für {Number}", what,
                PhoneNumberMasker.Mask(Call?.PhoneNumber));
        }
        catch (Exception ex)
        {
            ActionError = ex.Message;
            Log.Error(ex, "{Action} fehlgeschlagen", what);
        }
    }
}
