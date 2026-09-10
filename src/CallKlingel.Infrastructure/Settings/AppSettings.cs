using System.Text.Json;

namespace CallKlingel.Infrastructure.Settings;

/// <summary>Everything the app remembers between sessions. Nothing sensitive belongs here.</summary>
public sealed record AppSettings
{
    /// <summary>Connect to the last used phone as soon as the app starts.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Reconnect on its own when the phone comes back into range.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Bluetooth id of the phone to reconnect to.</summary>
    public string? LastDeviceId { get; set; }

    public string? LastDeviceName { get; set; }

    /// <summary>Audio endpoints chosen for calls. Null means the system default.</summary>
    public string? PlaybackDeviceId { get; set; }
    public string? CaptureDeviceId { get; set; }

    public bool ShowCallPopup { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool DebugLogging { get; set; }

    // --- Gesprächsaufzeichnung ---
    // Standardmäßig aus. In Deutschland ist das Aufzeichnen eines Telefonats ohne
    // Wissen des Gegenübers nach Paragraf 201 StGB strafbar.
    public bool RecordCalls { get; set; }
    public string? RecordingFolder { get; set; }
    public bool RecordingStartTone { get; set; } = true;
    public bool RecordingMaskNumber { get; set; } = true;
}

/// <summary>
/// Stores settings as JSON in LocalApplicationData, which is writable both packaged and
/// unpackaged. Failures never throw at the caller: losing a preference must not stop the
/// app from taking calls.
/// </summary>
public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _cached;

    public JsonSettingsStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CallKlingel");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;

            if (File.Exists(_path))
            {
                try
                {
                    await using var stream = File.OpenRead(_path);
                    _cached = await JsonSerializer
                        .DeserializeAsync<AppSettings>(stream, Options, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A damaged settings file falls back to the defaults.
                }
            }

            return _cached ??= new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        _cached = settings;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, settings, Options, ct).ConfigureAwait(false);
        }
        catch
        {
            // Not being able to persist a preference is not worth interrupting the user.
        }
        finally
        {
            _gate.Release();
        }
    }
}
