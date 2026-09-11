namespace CallKlingel.Platform.Linux.BlueZ;

/// <summary>
/// One device as BlueZ describes it, flattened out of the D-Bus property dictionary.
/// </summary>
internal sealed record BlueZDevice
{
    /// <summary>D-Bus object path, for example /org/bluez/hci0/dev_AA_BB_CC_DD_EE_FF.</summary>
    public required string Path { get; init; }

    /// <summary>The MAC address. This is what the rest of the app treats as the device id.</summary>
    public required string Address { get; init; }

    public string Name { get; init; } = string.Empty;
    public bool Paired { get; init; }
    public bool Connected { get; init; }
    public bool Trusted { get; init; }

    /// <summary>Services the device advertises. Empty until it has been paired once.</summary>
    public IReadOnlyList<string> Uuids { get; init; } = [];

    /// <summary>BlueZ's own guess at the device class, for example "phone".</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Signal strength while scanning. Null for devices that are only remembered.</summary>
    public short? Rssi { get; init; }

    /// <summary>
    /// True when the device offers the Audio Gateway role, meaning it can actually route a
    /// call to us. A phone that is paired but does not advertise this cannot be used for
    /// telephony, and saying so early is kinder than a connection that silently does nothing.
    /// </summary>
    public bool SupportsHandsFree =>
        Uuids.Any(u => string.Equals(u, BlueZUuids.HandsFreeAudioGateway, StringComparison.OrdinalIgnoreCase));

    public bool SupportsPhonebook =>
        Uuids.Any(u => string.Equals(u, BlueZUuids.PhonebookAccessServer, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Service identifiers as the phone advertises them, which is the opposite side of the ones
/// this app registers. Kept apart from <see cref="BlueZNames"/> for exactly that reason.
/// </summary>
internal static class BlueZUuids
{
    /// <summary>The role the phone plays: it owns the call and hands us the audio.</summary>
    public const string HandsFreeAudioGateway = "0000111f-0000-1000-8000-00805f9b34fb";

    /// <summary>The phone's phonebook server, read by PBAP.</summary>
    public const string PhonebookAccessServer = "0000112f-0000-1000-8000-00805f9b34fb";

    public const string ObjectPush = "00001105-0000-1000-8000-00805f9b34fb";
}
