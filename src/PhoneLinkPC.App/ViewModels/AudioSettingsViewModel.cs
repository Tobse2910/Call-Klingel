using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneLinkPC.Core.Audio;
using PhoneLinkPC.Infrastructure.Settings;
using System.IO;
using Serilog;

namespace PhoneLinkPC.App.ViewModels;

/// <summary>
/// Audio settings: pick the speaker and microphone for calls, and check both before a call
/// arrives rather than discovering a wrong device while someone is on the line.
/// </summary>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    private readonly IAudioBridge _audio;
    private CancellationTokenSource? _micTest;
    private AppSettings? _settings;
    private JsonSettingsStore? _store;

    public AudioSettingsViewModel(IAudioBridge audio) => _audio = audio;

    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    [ObservableProperty] private AudioDeviceInfo? _selectedPlayback;
    [ObservableProperty] private AudioDeviceInfo? _selectedCapture;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isTestingTone;
    [ObservableProperty] private bool _isTestingMicrophone;
    [ObservableProperty] private double _microphoneLevel;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Highest level seen during the test, so a short word still leaves a mark.</summary>
    [ObservableProperty] private double _microphonePeak;

    public bool MicrophoneHeardSomething => MicrophonePeak > 0.02;

    // --- Gesprächsaufzeichnung ---
    [ObservableProperty] private bool _recordCalls;
    [ObservableProperty] private string _recordingFolder = RecordingOptions.DefaultFolder;
    [ObservableProperty] private bool _recordingStartTone = true;
    [ObservableProperty] private bool _recordingMaskNumber = true;
    [ObservableProperty] private string? _recordingMessage;

    public ObservableCollection<RecordingInfo> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    /// <summary>Hands the view model the stored preferences so choices survive a restart.</summary>
    public void Apply(AppSettings settings, JsonSettingsStore store)
    {
        _settings = settings;
        _store = store;

        RecordCalls = settings.RecordCalls;
        RecordingFolder = settings.RecordingFolder ?? RecordingOptions.DefaultFolder;
        RecordingStartTone = settings.RecordingStartTone;
        RecordingMaskNumber = settings.RecordingMaskNumber;

        PushRecordingOptions();
        _audio.RecordingFinished += (_, info) =>
            Dispatcher.UIThread.Post(() =>
            {
                Recordings.Insert(0, info);
                OnPropertyChanged(nameof(HasRecordings));
                RecordingMessage = $"Aufnahme gespeichert: {info.FileName} ({info.SizeText})";
            });

        LoadExistingRecordings();
    }

    /// <summary>Hands the current recording settings to the audio bridge.</summary>
    private void PushRecordingOptions()
    {
        _audio.ConfigureRecording(new RecordingOptions
        {
            Enabled = RecordCalls,
            Folder = string.IsNullOrWhiteSpace(RecordingFolder)
                ? RecordingOptions.DefaultFolder
                : RecordingFolder,
            PlayStartTone = RecordingStartTone,
            MaskNumberInFileName = RecordingMaskNumber
        });
    }

    private void Save()
    {
        if (_settings is null || _store is null) return;
        _settings.RecordCalls = RecordCalls;
        _settings.RecordingFolder = RecordingFolder;
        _settings.RecordingStartTone = RecordingStartTone;
        _settings.RecordingMaskNumber = RecordingMaskNumber;
        _ = _store.SaveAsync(_settings);
        PushRecordingOptions();
    }

    partial void OnRecordCallsChanged(bool value) => Save();
    partial void OnRecordingFolderChanged(string value) => Save();
    partial void OnRecordingStartToneChanged(bool value) => Save();
    partial void OnRecordingMaskNumberChanged(bool value) => Save();

    /// <summary>Lists the files already in the folder, so the page is not empty on restart.</summary>
    [RelayCommand]
    private void LoadExistingRecordings()
    {
        try
        {
            Recordings.Clear();
            if (!Directory.Exists(RecordingFolder)) return;

            foreach (var file in new DirectoryInfo(RecordingFolder)
                         .GetFiles("*.wav")
                         .OrderByDescending(f => f.LastWriteTime)
                         .Take(50))
            {
                Recordings.Add(new RecordingInfo
                {
                    FilePath = file.FullName,
                    StartedAt = file.CreationTime,
                    SizeBytes = file.Length
                });
            }

            OnPropertyChanged(nameof(HasRecordings));
        }
        catch (Exception ex)
        {
            RecordingMessage = $"Ordner konnte nicht gelesen werden: {ex.Message}";
        }
    }

    /// <summary>Opens the recording folder in the file manager.</summary>
    [RelayCommand]
    private void OpenRecordingFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RecordingFolder)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            RecordingMessage = $"Ordner konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    partial void OnSelectedPlaybackChanged(AudioDeviceInfo? value)
    {
        if (_settings is null || _store is null || value is null) return;
        _settings.PlaybackDeviceId = value.Id;
        _ = _store.SaveAsync(_settings);
    }

    partial void OnSelectedCaptureChanged(AudioDeviceInfo? value)
    {
        if (_settings is null || _store is null || value is null) return;
        _settings.CaptureDeviceId = value.Id;
        _ = _store.SaveAsync(_settings);
    }

    [RelayCommand]
    public async Task LoadDevicesAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var devices = await _audio.GetDevicesAsync();

            var playbackId = SelectedPlayback?.Id;
            var captureId = SelectedCapture?.Id;

            PlaybackDevices.Clear();
            CaptureDevices.Clear();

            foreach (var d in devices.Where(d => d.Kind == AudioDeviceKind.Playback))
                PlaybackDevices.Add(d);
            foreach (var d in devices.Where(d => d.Kind == AudioDeviceKind.Capture))
                CaptureDevices.Add(d);

            SelectedPlayback = PlaybackDevices.FirstOrDefault(d => d.Id == playbackId)
                               ?? PlaybackDevices.FirstOrDefault(d => d.Id == _settings?.PlaybackDeviceId)
                               ?? PlaybackDevices.FirstOrDefault(d => d.IsSystemDefault)
                               ?? PlaybackDevices.FirstOrDefault();

            SelectedCapture = CaptureDevices.FirstOrDefault(d => d.Id == captureId)
                              ?? CaptureDevices.FirstOrDefault(d => d.Id == _settings?.CaptureDeviceId)
                              ?? CaptureDevices.FirstOrDefault(d => d.IsSystemDefault)
                              ?? CaptureDevices.FirstOrDefault();

            Message = $"{PlaybackDevices.Count} Wiedergabegeräte, {CaptureDevices.Count} Mikrofone.";
        }
        catch (Exception ex)
        {
            Message = $"Audiogeräte konnten nicht gelesen werden: {ex.Message}";
            Log.Error(ex, "Audiogeräte konnten nicht gelesen werden");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task TestToneAsync()
    {
        if (IsTestingTone) return;
        IsTestingTone = true;
        Message = "Testton läuft ...";

        try
        {
            var result = await _audio.PlayTestToneAsync(SelectedPlayback?.Id);
            Message = result.Success
                ? $"Testton auf '{SelectedPlayback?.Name}' abgespielt. Gehört?"
                : result.Error;
        }
        catch (Exception ex)
        {
            Message = $"Testton fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsTestingTone = false;
        }
    }

    [RelayCommand]
    private async Task TestMicrophoneAsync()
    {
        if (IsTestingMicrophone)
        {
            await _micTest!.CancelAsync();
            return;
        }

        IsTestingMicrophone = true;
        MicrophonePeak = 0;
        MicrophoneLevel = 0;
        Message = "Sprich jetzt - der Balken sollte ausschlagen.";
        _micTest = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(level =>
                Dispatcher.UIThread.Post(() =>
                {
                    MicrophoneLevel = level;
                    if (level > MicrophonePeak) MicrophonePeak = level;
                    OnPropertyChanged(nameof(MicrophoneHeardSomething));
                }));

            var result = await _audio.TestMicrophoneAsync(
                SelectedCapture?.Id, TimeSpan.FromSeconds(8), progress, _micTest.Token);

            Message = !result.Success
                ? result.Error
                : MicrophoneHeardSomething
                    ? $"Mikrofon '{SelectedCapture?.Name}' funktioniert. Höchster Pegel: {MicrophonePeak:P0}."
                    : "Kein Signal erkannt. Anderes Mikrofon wählen oder die Stummschaltung prüfen.";
        }
        catch (Exception ex)
        {
            Message = $"Mikrofontest fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsTestingMicrophone = false;
            MicrophoneLevel = 0;
            _micTest?.Dispose();
            _micTest = null;
        }
    }
}
