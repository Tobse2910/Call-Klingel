using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Audio;
using CallKlingel.Core.Platform;

namespace CallKlingel.Infrastructure;

/// <summary>
/// Chooses the telephony backend for the current host. This is the only place in the
/// application that knows which operating system it is running on; everything above it
/// works against <see cref="ITelephonyService"/>.
/// </summary>
public static class TelephonyBackendFactory
{
    /// <summary>
    /// Backends are resolved by reflection so that the neutral projects do not need a
    /// compile-time reference to a platform assembly that may not exist on this host.
    /// </summary>
    private static readonly (HostOs Os, string Service, string Diagnostics)[] Backends =
    [
        (HostOs.Windows,
            "CallKlingel.Platform.Windows.WindowsTelephonyService, CallKlingel.Platform.Windows",
            "CallKlingel.Platform.Windows.WindowsTelephonyDiagnostics, CallKlingel.Platform.Windows"),
        (HostOs.Linux,
            "CallKlingel.Platform.Linux.LinuxTelephonyService, CallKlingel.Platform.Linux",
            "CallKlingel.Platform.Linux.LinuxTelephonyDiagnostics, CallKlingel.Platform.Linux")
    ];

    public static ITelephonyService CreateService() =>
        Create<ITelephonyService>(b => b.Service) ?? new UnsupportedTelephonyService();

    public static ITelephonyDiagnostics CreateDiagnostics() =>
        Create<ITelephonyDiagnostics>(b => b.Diagnostics) ?? new UnsupportedTelephonyDiagnostics();

    /// <summary>
    /// Audio bridging is platform specific too: WASAPI on Windows, PipeWire on Linux later.
    /// </summary>
    public static IAudioBridge CreateAudioBridge()
    {
        var typeName = HostPlatform.Current switch
        {
            HostOs.Windows => "CallKlingel.Platform.Windows.WasapiAudioBridge, CallKlingel.Platform.Windows",
            _ => null
        };

        if (typeName is null) return new UnsupportedAudioBridge();

        var type = Type.GetType(typeName, throwOnError: false);
        return type is null
            ? new UnsupportedAudioBridge()
            : Activator.CreateInstance(type) as IAudioBridge ?? new UnsupportedAudioBridge();
    }

    private static T? Create<T>(Func<(HostOs Os, string Service, string Diagnostics), string> pick) where T : class
    {
        var backend = Backends.FirstOrDefault(b => b.Os == HostPlatform.Current);
        if (backend.Service is null) return null;

        var type = Type.GetType(pick(backend), throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type) as T;
    }
}
