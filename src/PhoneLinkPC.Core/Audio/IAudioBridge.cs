using PhoneLinkPC.Core.Abstractions;

namespace PhoneLinkPC.Core.Audio;

/// <summary>
/// Moves call audio between the phone and the PC devices the user picked.
///
/// With the Hands-Free Profile the phone's audio arrives on a Bluetooth capture endpoint
/// and the microphone signal has to be written back to a Bluetooth render endpoint. Without
/// this bridge the call would stay on the phone's own earpiece, which is exactly what the
/// project set out to avoid.
/// </summary>
public interface IAudioBridge : IAsyncDisposable
{
    bool IsRunning { get; }

    Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts routing. Passing null for a device id means "use the system default".
    /// </summary>
    Task<TelephonyResult> StartAsync(string? playbackDeviceId, string? captureDeviceId,
                                     CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Plays a short test tone so the user can hear which device is selected before a call
    /// arrives - not during one.
    /// </summary>
    Task<TelephonyResult> PlayTestToneAsync(string? playbackDeviceId, CancellationToken ct = default);

    /// <summary>
    /// Listens on the microphone and reports the input level from 0 to 1, so the user can
    /// see that the right microphone is picked and that it actually hears something.
    /// </summary>
    Task<TelephonyResult> TestMicrophoneAsync(string? captureDeviceId, TimeSpan duration,
                                              IProgress<double> level, CancellationToken ct = default);

    /// <summary>True while a call is being recorded.</summary>
    bool IsRecording { get; }

    /// <summary>
    /// Applies the recording settings. Takes effect on the next call that starts; an
    /// ongoing recording is never changed underneath the user.
    /// </summary>
    void ConfigureRecording(RecordingOptions options);

    /// <summary>Raised when a recording has been written, so the UI can list it.</summary>
    event EventHandler<RecordingInfo>? RecordingFinished;

    /// <summary>
    /// Starts recording the call that is already running. This is the normal way to record:
    /// ask the other party first, then press record.
    /// </summary>
    Task<TelephonyResult> StartRecordingAsync(string? phoneNumber, CancellationToken ct = default);

    /// <summary>Stops the recording and returns what was written.</summary>
    Task<RecordingInfo?> StopRecordingAsync(CancellationToken ct = default);

    /// <summary>When the current recording began, for the elapsed time display.</summary>
    DateTimeOffset? RecordingStartedAt { get; }
}
