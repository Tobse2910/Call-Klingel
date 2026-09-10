using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Diagnostics;
using Serilog;

namespace CallKlingel.App.ViewModels;

/// <summary>
/// Milestone 2: the "Telefonie-Diagnose" page. It reports only what was actually probed,
/// including the failing HRESULTs, so no UI element ever claims a capability the OS
/// has not granted.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ITelephonyDiagnostics _diagnostics;

    public DiagnosticsViewModel(ITelephonyDiagnostics diagnostics, ITelephonyService telephony)
    {
        _diagnostics = diagnostics;
        Backend = diagnostics.Backend;

        // Live view of the traffic between PC and phone. Without it, "nothing happened" and
        // "nothing arrived" look exactly the same.
        telephony.ProtocolTrace += (_, line) => Dispatcher.UIThread.Post(() => Trace($"<< {line}"));
        telephony.CallStateChanged += (_, e) =>
            Dispatcher.UIThread.Post(() => Trace($"** Zustand: {e.PreviousState} -> {e.State}"));
        telephony.IncomingCall += (_, _) =>
            Dispatcher.UIThread.Post(() => Trace("** EINGEHENDER ANRUF"));
        telephony.ConnectionStateChanged += (_, e) =>
            Dispatcher.UIThread.Post(() => Trace($"** Verbindung: {e.State}"));
    }

    /// <summary>Live protocol lines, newest first, capped so it cannot grow forever.</summary>
    public ObservableCollection<string> ProtocolLines { get; } = [];

    public bool HasTrace => ProtocolLines.Count > 0;

    private void Trace(string line)
    {
        ProtocolLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (ProtocolLines.Count > 200) ProtocolLines.RemoveAt(ProtocolLines.Count - 1);
        OnPropertyChanged(nameof(HasTrace));
    }

    [RelayCommand]
    private void ClearTrace()
    {
        ProtocolLines.Clear();
        OnPropertyChanged(nameof(HasTrace));
    }

    public string Backend { get; }

    public ObservableCollection<DiagnosticGroup> Groups { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = "Noch keine Diagnose ausgeführt.";
    [ObservableProperty] private string? _lastReportText;

    [RelayCommand] private Task RunFullAsync() => RunAsync(_diagnostics.RunFullAsync, "Vollständige Diagnose");
    [RelayCommand] private Task TestConnectionAsync() => RunAsync(_diagnostics.TestConnectionAsync, "Test Verbindung");
    [RelayCommand] private Task CheckCapabilitiesAsync() => RunAsync(_diagnostics.CheckCapabilitiesAsync, "Capabilities prüfen");
    [RelayCommand] private Task FindPhoneLinesAsync() => RunAsync(_diagnostics.FindPhoneLinesAsync, "Telefonleitungen suchen");

    private async Task RunAsync(Func<CancellationToken, Task<TelephonyDiagnosticsReport>> run, string label)
    {
        if (IsRunning) return;

        IsRunning = true;
        Summary = $"{label} läuft ...";

        try
        {
            var report = await run(CancellationToken.None);

            Groups.Clear();
            foreach (var group in report.Checks.GroupBy(c => c.Category))
                Groups.Add(new DiagnosticGroup(group.Key, [.. group]));

            LastReportText = report.ToPlainText();

            Summary = $"{label}: {report.CountOf(DiagnosticStatus.Ok)} OK, "
                    + $"{report.CountOf(DiagnosticStatus.Warning)} Warnung, "
                    + $"{report.CountOf(DiagnosticStatus.Failed)} Fehler, "
                    + $"{report.CountOf(DiagnosticStatus.Skipped)} übersprungen.";

            Log.Information("Diagnose {Label} abgeschlossen: {Summary}", label, Summary);
            Log.Debug("Diagnosebericht:\n{Report}", LastReportText);
        }
        catch (Exception ex)
        {
            Summary = $"{label} fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Diagnose {Label} fehlgeschlagen", label);
        }
        finally
        {
            IsRunning = false;
        }
    }
}

/// <summary>A category of checks, so the view can render them under one heading.</summary>
public sealed class DiagnosticGroup(string name, IReadOnlyList<DiagnosticCheck> checks)
{
    public string Name { get; } = name;
    public IReadOnlyList<DiagnosticCheck> Checks { get; } = checks;
}
