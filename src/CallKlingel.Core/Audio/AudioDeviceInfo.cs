namespace CallKlingel.Core.Audio;

public enum AudioDeviceKind { Playback, Capture }

/// <summary>An audio device the user can pick for call audio.</summary>
public sealed record AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public AudioDeviceKind Kind { get; init; }
    public bool IsSystemDefault { get; init; }

    /// <summary>True for the Bluetooth hands-free endpoints that carry the call itself.</summary>
    public bool IsHandsFree { get; init; }

    public override string ToString() => Name;
}
