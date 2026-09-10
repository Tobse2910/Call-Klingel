namespace PhoneLinkPC.Core.Models;

/// <summary>Connection state of the paired phone, as the UI understands it.</summary>
public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// Abstract call state. The UI binds to this and never to a Windows- or Linux-specific enum.
/// </summary>
public enum CallState
{
    Idle,
    Ringing,
    Dialing,
    Active,
    Held,
    Ended,
    Error
}

public enum CallDirection
{
    Incoming,
    Outgoing
}

public enum CallHistoryStatus
{
    Incoming,
    Outgoing,
    Missed,
    Rejected,
    Completed
}

/// <summary>Where the call audio is currently being played and captured.</summary>
public enum AudioRoute
{
    Unknown,
    Phone,
    LocalDevice
}
