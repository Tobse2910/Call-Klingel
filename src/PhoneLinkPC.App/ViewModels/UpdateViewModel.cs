using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneLinkPC.Core.Abstractions;
using Serilog;

namespace PhoneLinkPC.App.ViewModels;

/// <summary>
/// The update section in the sidebar.
///
/// It stays invisible until there is genuinely something to say. An app that permanently
/// shows "Du bist auf dem neuesten Stand" spends attention on a non-event; this one only
/// appears when a new version is waiting, or when the user asked and deserves an answer.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;

    public UpdateViewModel(IUpdateService updates)
    {
        _updates = updates;
        CurrentVersion = updates.CurrentVersion;
    }

    public string CurrentVersion { get; }

    public string VersionText => $"Version {CurrentVersion}";

    /// <summary>Hides the whole section for a build that cannot update itself.</summary>
    public bool IsSupported => _updates.IsSupported;

    [ObservableProperty] private UpdateInfo? _available;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string? _message;

    public bool HasUpdate => Available is not null && !IsReady;
    public bool IsBusy => IsChecking || IsDownloading;

    public string UpdateTitle => Available is null
        ? ""
        : $"Version {Available.Version} verfügbar";

    public string UpdateSubtitle => Available is null
        ? ""
        : string.IsNullOrWhiteSpace(Available.SizeText)
            ? "Jetzt herunterladen."
            : $"{Available.SizeText} herunterladen.";

    /// <summary>
    /// Runs on startup. Silent by design: no update is the normal case, and it should cost
    /// the user nothing - not a message, not a spinner, not a moment of their attention.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_updates.IsSupported) return;

        try
        {
            var info = await _updates.CheckAsync();
            if (info is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                Available = info;
                Refresh();
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Automatische Updateprüfung fehlgeschlagen");
        }
    }

    /// <summary>The user asked, so this one answers either way.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy) return;

        IsChecking = true;
        Message = null;
        Refresh();

        try
        {
            Available = await _updates.CheckAsync();
            Message = Available is null ? "Du hast die neueste Version." : null;
        }
        catch (Exception ex)
        {
            Message = "Updateprüfung nicht möglich. Besteht eine Internetverbindung?";
            Log.Warning(ex, "Updateprüfung fehlgeschlagen");
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy || Available is null) return;

        IsDownloading = true;
        Progress = 0;
        Message = null;
        Refresh();

        try
        {
            var reported = new Progress<int>(p =>
                Dispatcher.UIThread.Post(() => Progress = p));

            var result = await _updates.DownloadAsync(reported);

            if (result.Success)
            {
                IsReady = true;
                Message = null;
            }
            else
            {
                Message = result.Error;
            }
        }
        finally
        {
            IsDownloading = false;
            Refresh();
        }
    }

    /// <summary>
    /// Restarts into the new version. Separated from the download so the user picks the
    /// moment - the app closes here, and it must never do that during a call.
    /// </summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        var result = await _updates.ApplyAndRestartAsync();
        if (!result.Success)
        {
            Message = result.Error;
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(HasUpdate), nameof(IsBusy), nameof(UpdateTitle),
                     nameof(UpdateSubtitle), nameof(IsSupported)
                 })
            OnPropertyChanged(name);
    }
}
