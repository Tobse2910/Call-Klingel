namespace CallKlingel.Core.Bluetooth;

/// <summary>
/// The radio a Bluetooth association endpoint belongs to. A phone advertises both, and
/// the two are bonded independently - which is exactly what makes the difference easy to
/// overlook and hard to debug.
/// </summary>
public enum BluetoothTransport
{
    /// <summary>BR/EDR. The only transport that carries RFCOMM and therefore HFP.</summary>
    Classic,

    /// <summary>Bluetooth Low Energy. Carries no HFP, no matter how it looks in Settings.</summary>
    LowEnergy
}

/// <summary>
/// One Bluetooth association endpoint as the operating system reports it. Platform
/// neutral on purpose: Windows fills these from device enumeration, Linux later from
/// BlueZ, and <see cref="BondAnalyzer"/> works the same on both.
/// </summary>
public sealed record BluetoothEndpoint
{
    public required string Address { get; init; }
    public required string Name { get; init; }
    public required BluetoothTransport Transport { get; init; }
    public required bool IsPaired { get; init; }

    public bool IsConnected { get; init; }

    /// <summary>The operating system's identifier, needed to remove a stale bond.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// True when the operating system classifies this device as a phone. Platforms set it
    /// where they know - on Windows from the Class of Device - so that a paired headset is
    /// never mistaken for a working telephony bond.
    /// </summary>
    public bool IsPhone { get; init; }

    /// <summary>
    /// Addresses arrive in different spellings - "aa:bb:cc:dd:ee:ff" from an association
    /// endpoint, "aabbccddeeff" from a device node. Comparing them raw makes one physical
    /// phone look like two devices, which hides a broken bond.
    /// </summary>
    public string NormalizedAddress => Normalize(Address);

    public static string Normalize(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;

        Span<char> buffer = stackalloc char[address.Length];
        var length = 0;

        foreach (var c in address)
            if (char.IsAsciiLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);

        return new string(buffer[..length]);
    }
}
