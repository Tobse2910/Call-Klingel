using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PhoneLinkPC.Infrastructure.Logging;

/// <summary>
/// Structured logging to logs/phonelink-yyyy-MM-dd.log.
///
/// Privacy rule: phone numbers are never written in full. Callers must pass numbers
/// through <see cref="Core.Privacy.PhoneNumberMasker"/> before logging them.
/// </summary>
public static class AppLog
{
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    public static string LogDirectory { get; private set; } = "logs";

    /// <summary>Toggles debug output at runtime, for the "Debug-Modus" setting.</summary>
    public static bool DebugEnabled
    {
        get => LevelSwitch.MinimumLevel <= LogEventLevel.Debug;
        set => LevelSwitch.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
    }

    /// <summary>
    /// Default log location. It must not be the install directory: under MSIX that lives in
    /// Program Files\WindowsApps and is read-only, so writing there crashes at startup.
    /// LocalApplicationData is writable both packaged and unpackaged.
    /// </summary>
    public static string DefaultBaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneLinkPC");

    public static void Initialize(string? baseDirectory = null, bool debug = false)
    {
        LogDirectory = Path.Combine(baseDirectory ?? DefaultBaseDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);
        DebugEnabled = debug;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "phonelink-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("PhoneLink PC gestartet. Logverzeichnis: {LogDirectory}", LogDirectory);
    }

    public static void Shutdown() => Log.CloseAndFlush();
}
