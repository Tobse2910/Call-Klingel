using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PhoneLinkPC.Core.Audio;

namespace PhoneLinkPC.Platform.Windows;

/// <summary>
/// Writes both sides of a call into one WAV file.
///
/// The two streams arrive in different formats and at different rates, so each is resampled
/// to a common mono format before mixing. Recording uses its own buffers: reading from the
/// playback buffers would consume the samples and the call would go silent.
/// </summary>
public sealed class CallRecorder : IDisposable
{
    /// <summary>HFP audio is narrowband; 16 kHz mono keeps quality without wasting space.</summary>
    private static readonly WaveFormat Target = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);

    private readonly object _sync = new();

    private BufferedWaveProvider? _farEnd;
    private BufferedWaveProvider? _nearEnd;
    private WaveFileWriter? _writer;
    private CancellationTokenSource? _pump;
    private Task? _pumpTask;

    public bool IsRecording { get; private set; }
    public string? FilePath { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>
    /// Starts a recording. The formats of both capture streams have to be known up front so
    /// the resamplers can be built.
    /// </summary>
    public void Start(string filePath, WaveFormat farEndFormat, WaveFormat nearEndFormat)
    {
        Stop();

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        lock (_sync)
        {
            _farEnd = new BufferedWaveProvider(farEndFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5)
            };
            _nearEnd = new BufferedWaveProvider(nearEndFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5)
            };

            _writer = new WaveFileWriter(filePath, Target);
            FilePath = filePath;
            StartedAt = DateTimeOffset.Now;
            IsRecording = true;
        }

        var mixer = new MixingSampleProvider(Target) { ReadFully = true };
        mixer.AddMixerInput(ToTarget(_farEnd!));
        mixer.AddMixerInput(ToTarget(_nearEnd!));

        _pump = new CancellationTokenSource();
        _pumpTask = PumpAsync(mixer, _pump.Token);
    }

    /// <summary>Feeds audio coming from the phone, i.e. the other party.</summary>
    public void WriteFarEnd(byte[] buffer, int count)
    {
        lock (_sync) _farEnd?.AddSamples(buffer, 0, count);
    }

    /// <summary>Feeds audio from the local microphone.</summary>
    public void WriteNearEnd(byte[] buffer, int count)
    {
        lock (_sync) _nearEnd?.AddSamples(buffer, 0, count);
    }

    public RecordingInfo? Stop()
    {
        if (!IsRecording) return null;

        IsRecording = false;

        try { _pump?.Cancel(); } catch { /* already gone */ }
        try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }

        RecordingInfo? info = null;

        lock (_sync)
        {
            var path = FilePath;

            try { _writer?.Flush(); } catch { /* nothing more to write */ }
            try { _writer?.Dispose(); } catch { /* already closed */ }
            _writer = null;
            _farEnd = null;
            _nearEnd = null;

            if (path is not null && File.Exists(path))
            {
                var file = new FileInfo(path);

                // A file with only a header means nothing was captured; do not leave it behind.
                if (file.Length <= 64)
                {
                    try { file.Delete(); } catch { /* leave it */ }
                }
                else
                {
                    info = new RecordingInfo
                    {
                        FilePath = path,
                        StartedAt = StartedAt,
                        Duration = DateTimeOffset.Now - StartedAt,
                        SizeBytes = file.Length
                    };
                }
            }
        }

        _pump?.Dispose();
        _pump = null;
        _pumpTask = null;
        FilePath = null;

        return info;
    }

    private async Task PumpAsync(ISampleProvider mixer, CancellationToken ct)
    {
        var buffer = new float[Target.SampleRate / 10];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var read = mixer.Read(buffer, 0, buffer.Length);

                lock (_sync)
                {
                    if (_writer is null) return;
                    if (read > 0) _writer.WriteSamples(buffer, 0, read);
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A hiccup in one buffer must not end the recording.
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Brings any capture format to the common mono target format.</summary>
    private static ISampleProvider ToTarget(IWaveProvider source)
    {
        ISampleProvider samples = source.ToSampleProvider();

        if (samples.WaveFormat.Channels > 1)
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };

        if (samples.WaveFormat.SampleRate != Target.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, Target.SampleRate);

        return samples;
    }

    /// <summary>Builds a file name that sorts by time and optionally hides the number.</summary>
    public static string BuildFileName(DateTimeOffset when, string? number, bool mask)
    {
        var stamp = when.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss");

        if (string.IsNullOrWhiteSpace(number)) return $"Anruf_{stamp}.wav";

        if (mask)
        {
            var digits = new string(number.Where(char.IsDigit).ToArray());
            var tail = digits.Length >= 4 ? digits[^4..] : "xxxx";
            return $"Anruf_{stamp}_x{tail}.wav";
        }

        var safe = new string(number.Where(c => char.IsLetterOrDigit(c) || c == '+').ToArray());
        return $"Anruf_{stamp}_{safe}.wav";
    }

    public void Dispose() => Stop();
}
