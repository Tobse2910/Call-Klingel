namespace PhoneLinkPC.Core.Models;

/// <summary>A single call as the UI sees it. Purely abstract - no platform types.</summary>
public sealed record CallInfo
{
    public required string CallId { get; init; }
    public CallState State { get; init; } = CallState.Idle;
    public CallDirection Direction { get; init; } = CallDirection.Incoming;

    /// <summary>Raw number as delivered by the phone. May be null if the caller withheld it.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>Resolved from the local address book. Null when unknown.</summary>
    public string? ContactName { get; init; }

    public bool IsMuted { get; init; }
    public AudioRoute AudioRoute { get; init; } = AudioRoute.Unknown;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Set once the call is answered; used to drive the live duration display.</summary>
    public DateTimeOffset? AnsweredAt { get; init; }

    public string DisplayName => ContactName ?? PhoneNumber ?? "Unbekannt";
}
