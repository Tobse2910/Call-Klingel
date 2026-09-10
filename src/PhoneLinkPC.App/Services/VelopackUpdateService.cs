using PhoneLinkPC.Core.Abstractions;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace PhoneLinkPC.App.Services;

/// <summary>
/// Updates from GitHub releases.
///
/// Lives in the app project rather than in Infrastructure because Velopack only works for
/// an installed desktop application - putting it in a shared library would suggest the
/// Linux build could use it too, which it cannot.
///
/// Downloading and applying are separate on purpose. Applying restarts the program, and a
/// program that restarts itself during a phone call is worse than one that updates a day
/// later.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    /// <summary>
    /// Where releases are published. Changing this repository is all it takes to move the
    /// update channel somewhere else.
    /// </summary>
    public const string ReleaseUrl = "https://github.com/Tobse2910/Call-Klingel";

    private readonly UpdateManager? _manager;
    private Velopack.UpdateInfo? _pending;

    public VelopackUpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(ReleaseUrl, null, false));
        }
        catch (Exception ex)
        {
            // No update channel is a normal state for a build run from the output folder.
            Log.Debug(ex, "Updatequelle nicht verfügbar");
            _manager = null;
        }
    }

    public bool IsSupported => _manager?.IsInstalled == true;

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? typeof(VelopackUpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "unbekannt";

    public async Task<Core.Abstractions.UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (_manager is null || !_manager.IsInstalled) return null;

        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null) return null;

            var target = _pending.TargetFullRelease;

            Log.Information("Update verfügbar: {Version}", target.Version);

            return new Core.Abstractions.UpdateInfo(
                target.Version.ToString(),
                target.NotesMarkdown,
                target.Size);
        }
        catch (Exception ex)
        {
            // Being offline is not an error worth interrupting anyone over.
            Log.Debug(ex, "Updateprüfung fehlgeschlagen");
            return null;
        }
    }

    public async Task<TelephonyResult> DownloadAsync(
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (_manager is null) return TelephonyResult.Fail("Keine Updatequelle verfügbar.");
        if (_pending is null) return TelephonyResult.Fail("Kein Update zum Herunterladen.");

        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p => progress?.Report(p), cancelToken: ct)
                .ConfigureAwait(false);

            Log.Information("Update {Version} heruntergeladen",
                _pending.TargetFullRelease.Version);

            return TelephonyResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return TelephonyResult.Fail("Download abgebrochen.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update konnte nicht heruntergeladen werden");
            return TelephonyResult.Fail($"Download fehlgeschlagen: {ex.Message}");
        }
    }

    public Task<TelephonyResult> ApplyAndRestartAsync(CancellationToken ct = default)
    {
        if (_manager is null || _pending is null)
            return Task.FromResult(TelephonyResult.Fail("Kein Update bereit."));

        try
        {
            Log.Information("Update wird angewendet, App startet neu");
            _manager.ApplyUpdatesAndRestart(_pending);
            return Task.FromResult(TelephonyResult.Ok());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update konnte nicht angewendet werden");
            return Task.FromResult(TelephonyResult.Fail($"Update fehlgeschlagen: {ex.Message}"));
        }
    }
}
