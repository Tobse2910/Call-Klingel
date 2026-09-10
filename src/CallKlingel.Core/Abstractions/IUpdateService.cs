namespace CallKlingel.Core.Abstractions;

/// <summary>What the app knows about a newer version.</summary>
/// <param name="Version">Version number of the waiting release, for example "0.4.1".</param>
/// <param name="ReleaseNotes">What changed, as published with the release. May be empty.</param>
/// <param name="SizeBytes">Download size, so the UI can say what it is about to fetch.</param>
public sealed record UpdateInfo(string Version, string? ReleaseNotes, long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):0.0} MB"
    };
}

/// <summary>
/// Checking for and applying updates.
///
/// An interface rather than a direct call into the updater, for the same reason telephony
/// has one: the UI must not depend on how updates arrive. A build that is not installed -
/// running straight from the build output, or from a zip - has no update channel at all,
/// and says so instead of failing.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// False when this copy was not installed and therefore cannot update itself. The UI
    /// hides the whole update section rather than offering a button that cannot work.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>The version running right now.</summary>
    string CurrentVersion { get; }

    /// <summary>Asks the release channel whether something newer exists. Null means no.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the waiting update. Progress is reported as a percentage. This does not
    /// restart anything - downloading and applying are deliberately separate, so an update
    /// never interrupts a call.
    /// </summary>
    Task<TelephonyResult> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Applies the downloaded update and restarts the app. Only call this when the user
    /// asked for it: it closes the running program.
    /// </summary>
    Task<TelephonyResult> ApplyAndRestartAsync(CancellationToken ct = default);
}
