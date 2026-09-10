using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Audio;
using PhoneLinkPC.Core.Platform;

namespace PhoneLinkPC.Infrastructure;

/// <summary>
/// Fallback for platforms without an audio bridge yet. It reports the situation instead of
/// silently routing nothing, so the UI can tell the user why call audio stays on the phone.
/// </summary>
internal sealed class UnsupportedAudioBridge : IAudioBridge
{
    private static string Reason =>
        $"Für diese Plattform ({HostPlatform.DisplayName}) gibt es noch keine Audio-Weiterleitung.";

    public bool IsRunning => false;

    public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);

    public Task<TelephonyResult> StartAsync(string? playbackDeviceId, string? captureDeviceId,
                                            CancellationToken ct = default) =>
        Task.FromResult(TelephonyResult.Fail(Reason));

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<TelephonyResult> PlayTestToneAsync(string? playbackDeviceId, CancellationToken ct = default) =>
        Task.FromResult(TelephonyResult.Fail(Reason));

    public Task<TelephonyResult> TestMicrophoneAsync(string? captureDeviceId, TimeSpan duration,
                                                     IProgress<double> level, CancellationToken ct = default) =>
        Task.FromResult(TelephonyResult.Fail(Reason));

    public bool IsRecording => false;

    public void ConfigureRecording(RecordingOptions options) { }

#pragma warning disable CS0067 // Never raised: this bridge records nothing.
    public event EventHandler<RecordingInfo>? RecordingFinished;
#pragma warning restore CS0067

    public DateTimeOffset? RecordingStartedAt => null;

    public Task<TelephonyResult> StartRecordingAsync(string? phoneNumber, CancellationToken ct = default) =>
        Task.FromResult(TelephonyResult.Fail(Reason));

    public Task<RecordingInfo?> StopRecordingAsync(CancellationToken ct = default) =>
        Task.FromResult<RecordingInfo?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
