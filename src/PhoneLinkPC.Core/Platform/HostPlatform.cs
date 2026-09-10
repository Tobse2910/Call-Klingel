using System.Runtime.InteropServices;

namespace PhoneLinkPC.Core.Platform;

public enum HostOs { Unknown, Windows, Linux, MacOs }

/// <summary>Platform detection used to pick a telephony backend.</summary>
public static class HostPlatform
{
    public static HostOs Current =>
        OperatingSystem.IsWindows() ? HostOs.Windows :
        OperatingSystem.IsLinux()   ? HostOs.Linux   :
        OperatingSystem.IsMacOS()   ? HostOs.MacOs   : HostOs.Unknown;

    public static string DisplayName => Current switch
    {
        HostOs.Windows => $"Windows {Environment.OSVersion.Version}",
        HostOs.Linux   => $"Linux {Environment.OSVersion.Version}",
        HostOs.MacOs   => $"macOS {Environment.OSVersion.Version}",
        _              => RuntimeInformation.OSDescription
    };

    public static string Architecture => RuntimeInformation.OSArchitecture.ToString();
    public static string RuntimeVersion => RuntimeInformation.FrameworkDescription;

    /// <summary>Windows 11 is build 22000 or higher.</summary>
    public static bool IsWindows11OrGreater =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000;
}
