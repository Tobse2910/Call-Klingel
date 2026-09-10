namespace PhoneLinkPC.Core.Models;

/// <summary>
/// A Bluetooth device discovered by a platform backend. <see cref="SupportsHandsFree"/>
/// is the property that decides whether telephony is possible at all.
/// </summary>
public sealed record PhoneDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>True when the device advertises the Hands-Free Audio Gateway service (UUID 0x111F).</summary>
    public bool SupportsHandsFree { get; init; }

    public bool IsPaired { get; init; }

    /// <summary>
    /// True when this application holds a hands-free connection to the device - not merely
    /// when a Bluetooth radio link exists.
    ///
    /// The distinction matters: Windows reports a phone as connected as soon as any profile
    /// is up, so mixing the two made the device list show "Verbunden" while the app itself
    /// was still dialling in and the headline said "Verbinde ..." - two answers to the same
    /// question on one screen, seen on 2026-09-10.
    /// </summary>
    public bool IsConnected { get; init; }

    /// <summary>Battery percentage, when the platform reports one. Null otherwise.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>Signal strength on the 0-5 scale HFP uses. Null when unknown.</summary>
    public int? SignalStrength { get; init; }

    public string? Manufacturer { get; init; }

    public override string ToString() => $"{Name} ({Id})";
}
