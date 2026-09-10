using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Audio;

namespace PhoneLinkPC.Platform.Windows;

/// <summary>
/// Bridges call audio between the Bluetooth hands-free endpoints and the PC devices.
///
/// Two streams run in opposite directions:
///   phone  -> hands-free capture  -> chosen speaker or headset
///   chosen microphone -> hands-free render -> phone -> the other party
///
/// Windows creates the hands-free endpoints only while a call carries audio, so the bridge
/// is started when a call becomes active and torn down when it ends.
/// </summary>
public sealed class WasapiAudioBridge : IAudioBridge
{
    private readonly MMDeviceEnumerator _enumerator = new();

    private WasapiCapture? _fromPhone;
    private WasapiOut? _toSpeaker;
    private WasapiCapture? _fromMicrophone;
    private WasapiOut? _toPhone;
    private BufferedWaveProvider? _speakerBuffer;
    private BufferedWaveProvider? _phoneBuffer;
    private readonly CallRecorder _recorder = new();
    private RecordingOptions _recording = new() { Folder = RecordingOptions.DefaultFolder };
    private string? _pendingNumber;

    public bool IsRunning { get; private set; }
    public bool IsRecording => _recorder.IsRecording;

    public event EventHandler<RecordingInfo>? RecordingFinished;

    public void ConfigureRecording(RecordingOptions options) => _recording = options;

    /// <summary>The number of the current call, used only for the file name.</summary>
    public void SetCurrentNumber(string? number) => _pendingNumber = number;

    public DateTimeOffset? RecordingStartedAt => _recorder.IsRecording ? _recorder.StartedAt : null;

    /// <summary>Starts recording while a call is already running.</summary>
    public Task<TelephonyResult> StartRecordingAsync(string? phoneNumber, CancellationToken ct = default)
    {
        if (!IsRunning)
            return Task.FromResult(TelephonyResult.Fail(
                "Aufnahme nicht möglich: es läuft gerade kein Gespräch über den PC."));

        if (_recorder.IsRecording) return Task.FromResult(TelephonyResult.Ok());

        _pendingNumber = phoneNumber ?? _pendingNumber;
        StartRecording();

        return Task.FromResult(_recorder.IsRecording
            ? TelephonyResult.Ok()
            : TelephonyResult.Fail("Aufnahme konnte nicht gestartet werden."));
    }

    public Task<RecordingInfo?> StopRecordingAsync(CancellationToken ct = default)
    {
        var info = _recorder.Stop();
        if (info is not null)
            RecordingFinished?.Invoke(this, info with { PhoneNumber = _pendingNumber });

        return Task.FromResult(info);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<AudioDeviceInfo>();

        foreach (var (flow, kind) in new[]
                 {
                     (DataFlow.Render, AudioDeviceKind.Playback),
                     (DataFlow.Capture, AudioDeviceKind.Capture)
                 })
        {
            string? defaultId = null;
            try { defaultId = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; }
            catch { /* no default device configured */ }

            foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    Kind = kind,
                    IsSystemDefault = device.ID == defaultId,
                    IsHandsFree = IsHandsFree(device.FriendlyName)
                });
            }
        }

        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public async Task<TelephonyResult> StartAsync(string? playbackDeviceId, string? captureDeviceId,
                                                  CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);

        MMDevice? handsFreeIn = null;
        MMDevice? handsFreeOut = null;

        try
        {
            handsFreeIn = FindHandsFree(DataFlow.Capture);
            handsFreeOut = FindHandsFree(DataFlow.Render);
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Audiogeräte konnten nicht gelesen werden: {ex.Message}");
        }

        if (handsFreeIn is null || handsFreeOut is null)
            return TelephonyResult.Fail(
                "Windows stellt noch keine Freisprech-Audiogeräte bereit. Sie erscheinen erst, "
                + "wenn ein Gespräch aktiv ist und das Telefon die Audioverbindung aufgebaut hat.");

        try
        {
            var speaker = Resolve(playbackDeviceId, DataFlow.Render);
            var microphone = Resolve(captureDeviceId, DataFlow.Capture);

            if (speaker is null) return TelephonyResult.Fail("Kein Wiedergabegerät gefunden.");
            if (microphone is null) return TelephonyResult.Fail("Kein Mikrofon gefunden.");

            // --- phone -> speaker ---
            _fromPhone = new WasapiCapture(handsFreeIn);
            _speakerBuffer = new BufferedWaveProvider(_fromPhone.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(300)
            };
            _fromPhone.DataAvailable += (_, e) =>
            {
                _speakerBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                if (_recorder.IsRecording) _recorder.WriteFarEnd(e.Buffer, e.BytesRecorded);
            };

            _toSpeaker = new WasapiOut(speaker, AudioClientShareMode.Shared, true, 60);
            _toSpeaker.Init(_speakerBuffer);

            // --- microphone -> phone ---
            _fromMicrophone = new WasapiCapture(microphone);
            _phoneBuffer = new BufferedWaveProvider(_fromMicrophone.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(300)
            };
            _fromMicrophone.DataAvailable += (_, e) =>
            {
                _phoneBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                if (_recorder.IsRecording) _recorder.WriteNearEnd(e.Buffer, e.BytesRecorded);
            };

            _toPhone = new WasapiOut(handsFreeOut, AudioClientShareMode.Shared, true, 60);
            _toPhone.Init(_phoneBuffer);

            _fromPhone.StartRecording();
            _toSpeaker.Play();
            _fromMicrophone.StartRecording();
            _toPhone.Play();

            IsRunning = true;

            if (_recording.Enabled) StartRecording();

            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            await StopAsync(ct).ConfigureAwait(false);
            return TelephonyResult.Fail($"Audio-Weiterleitung fehlgeschlagen: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false;

        var finished = _recorder.Stop();
        if (finished is not null)
            RecordingFinished?.Invoke(this, finished with { PhoneNumber = _pendingNumber });

        Silence(() => _fromPhone?.StopRecording());
        Silence(() => _fromMicrophone?.StopRecording());
        Silence(() => _toSpeaker?.Stop());
        Silence(() => _toPhone?.Stop());

        Silence(() => _fromPhone?.Dispose());
        Silence(() => _fromMicrophone?.Dispose());
        Silence(() => _toSpeaker?.Dispose());
        Silence(() => _toPhone?.Dispose());

        _fromPhone = null;
        _fromMicrophone = null;
        _toSpeaker = null;
        _toPhone = null;
        _speakerBuffer = null;
        _phoneBuffer = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Begins recording. Both capture formats are known at this point, which the recorder
    /// needs in order to resample the two directions into one file.
    /// </summary>
    private void StartRecording()
    {
        if (_fromPhone is null || _fromMicrophone is null) return;

        try
        {
            var name = CallRecorder.BuildFileName(
                DateTimeOffset.Now, _pendingNumber, _recording.MaskNumberInFileName);
            var path = Path.Combine(_recording.Folder, name);

            _recorder.Start(path, _fromPhone.WaveFormat, _fromMicrophone.WaveFormat);

            // An audible marker so the recording is never a secret to either side.
            if (_recording.PlayStartTone) _ = PlayRecordingToneAsync();
        }
        catch
        {
            // Failing to record must never take the call down with it.
        }
    }

    private async Task PlayRecordingToneAsync()
    {
        try
        {
            var tone = new SignalGenerator(44100, 1)
            {
                Gain = 0.15,
                Frequency = 880,
                Type = SignalGeneratorType.Sin
            }.Take(TimeSpan.FromMilliseconds(350));

            using var output = new WasapiOut(AudioClientShareMode.Shared, 60);
            output.Init(tone);
            output.Play();
            await Task.Delay(500).ConfigureAwait(false);
        }
        catch
        {
            // The tone is a courtesy, not a requirement.
        }
    }

    /// <summary>Plays a short, gentle tone on the chosen device.</summary>
    public async Task<TelephonyResult> PlayTestToneAsync(string? playbackDeviceId,
                                                         CancellationToken ct = default)
    {
        var device = Resolve(playbackDeviceId, DataFlow.Render);
        if (device is null) return TelephonyResult.Fail("Kein Wiedergabegerät gefunden.");

        WasapiOut? output = null;
        try
        {
            // 660 Hz for a second, at a comfortable level - loud enough to hear, not to startle.
            var tone = new SignalGenerator(44100, 1)
            {
                Gain = 0.2,
                Frequency = 660,
                Type = SignalGeneratorType.Sin
            }.Take(TimeSpan.FromSeconds(1));

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
            output.Init(tone);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
                await Task.Delay(50, ct).ConfigureAwait(false);

            return TelephonyResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Testton fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            Silence(() => output?.Stop());
            Silence(() => output?.Dispose());
        }
    }

    /// <summary>Reports the microphone level while the user speaks.</summary>
    public async Task<TelephonyResult> TestMicrophoneAsync(string? captureDeviceId, TimeSpan duration,
                                                           IProgress<double> level,
                                                           CancellationToken ct = default)
    {
        var device = Resolve(captureDeviceId, DataFlow.Capture);
        if (device is null) return TelephonyResult.Fail("Kein Mikrofon gefunden.");

        WasapiCapture? capture = null;
        try
        {
            capture = new WasapiCapture(device);
            var format = capture.WaveFormat;

            capture.DataAvailable += (_, e) =>
            {
                var peak = Peak(e.Buffer, e.BytesRecorded, format);
                level.Report(peak);
            };

            capture.StartRecording();

            var end = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
                await Task.Delay(50, ct).ConfigureAwait(false);

            return TelephonyResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return TelephonyResult.Ok();
        }
        catch (Exception ex)
        {
            return TelephonyResult.Fail($"Mikrofontest fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            Silence(() => capture?.StopRecording());
            Silence(() => capture?.Dispose());
            level.Report(0);
        }
    }

    /// <summary>Highest absolute sample in the buffer, normalised to 0..1.</summary>
    private static double Peak(byte[] buffer, int count, WaveFormat format)
    {
        if (count <= 0) return 0;

        var max = 0.0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < count; i += 4)
            {
                var sample = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (sample > max) max = sample;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 1 < count; i += 2)
            {
                var sample = Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768.0;
                if (sample > max) max = sample;
            }
        }

        return max > 1 ? 1 : max;
    }

    private MMDevice? Resolve(string? id, DataFlow flow)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                var device = _enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return device;
            }
            catch
            {
                // The stored device is gone; fall back to the default below.
            }
        }

        try { return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    private MMDevice? FindHandsFree(DataFlow flow) =>
        _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                   .FirstOrDefault(d => IsHandsFree(d.FriendlyName));

    private static bool IsHandsFree(string name) =>
        name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Freisprech", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Headset", StringComparison.OrdinalIgnoreCase);

    private static void Silence(Action action)
    {
        try { action(); } catch { /* teardown must never throw */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _recorder.Dispose();
        _enumerator.Dispose();
    }
}
