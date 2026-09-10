namespace CallKlingel.Core.Audio;

/// <summary>
/// Settings for recording a call.
///
/// Recording is off unless the user turns it on, and the app shows plainly while it runs.
/// In Germany recording a call without the other party's knowledge is a criminal offence
/// under section 201 StGB, so a hidden recorder would be a trap for its own user.
/// </summary>
public sealed record RecordingOptions
{
    public bool Enabled { get; init; }

    /// <summary>Folder the WAV files are written to.</summary>
    public required string Folder { get; init; }

    /// <summary>Play a short tone at the start so both sides notice the recording.</summary>
    public bool PlayStartTone { get; init; } = true;

    /// <summary>
    /// Keep the number out of the file name. The file is still identifiable by its
    /// timestamp, but the folder listing does not expose who was called.
    /// </summary>
    public bool MaskNumberInFileName { get; init; } = true;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Call Klingel");
}

/// <summary>A finished recording on disk.</summary>
public sealed record RecordingInfo
{
    public required string FilePath { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public long SizeBytes { get; init; }
    public string? PhoneNumber { get; init; }
    public string? ContactName { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public string SizeText => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024.0:F0} KB"
        : $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
}
