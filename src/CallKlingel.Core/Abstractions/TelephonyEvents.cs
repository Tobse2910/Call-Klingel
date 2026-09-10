using CallKlingel.Core.Models;

namespace CallKlingel.Core.Abstractions;

public sealed class DeviceEventArgs(PhoneDevice device) : EventArgs
{
    public PhoneDevice Device { get; } = device;
}

public sealed class CallEventArgs(CallInfo call) : EventArgs
{
    public CallInfo Call { get; } = call;
}

public sealed class CallStateEventArgs(CallInfo call, CallState previousState) : EventArgs
{
    public CallInfo Call { get; } = call;
    public CallState PreviousState { get; } = previousState;
    public CallState State => Call.State;
}

public sealed class CallerInfoEventArgs(CallInfo call) : EventArgs
{
    public CallInfo Call { get; } = call;
}

public sealed class AudioRouteEventArgs(AudioRoute route) : EventArgs
{
    public AudioRoute Route { get; } = route;
}

public sealed class ConnectionStateEventArgs(DeviceConnectionState state, string? message = null) : EventArgs
{
    public DeviceConnectionState State { get; } = state;
    public string? Message { get; } = message;
}
