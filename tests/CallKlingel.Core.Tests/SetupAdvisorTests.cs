using CallKlingel.Core.Diagnostics;
using CallKlingel.Core.Setup;
using Xunit;

namespace CallKlingel.Core.Tests;

/// <summary>
/// The advisor turns a diagnostic report into instructions. Its job is to be right about
/// what is missing and silent when nothing is - both are tested here, without hardware.
/// </summary>
public class SetupAdvisorTests
{
    private static DiagnosticCheck Check(string name, DiagnosticStatus status) =>
        new() { Name = name, Status = status, Value = "-" };

    private static TelephonyDiagnosticsReport Report(params DiagnosticCheck[] checks) =>
        new() { Backend = "Test", Checks = checks };

    /// <summary>A ready machine gets no advice at all.</summary>
    [Fact]
    public void Healthy_system_produces_no_steps()
    {
        var report = Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Ok),
            Check("Bluetooth eingeschaltet", DiagnosticStatus.Ok),
            Check("Classic (BR/EDR) unterstützt", DiagnosticStatus.Ok),
            Check("Gekoppelte Geräte", DiagnosticStatus.Ok),
            Check("HFP-Transportgeräte", DiagnosticStatus.Ok));

        Assert.Empty(SetupAdvisor.Analyze(report));
    }

    [Fact]
    public void Missing_adapter_is_blocking_and_hides_the_rest()
    {
        // Without a radio, every later check fails as a consequence. Listing them all would
        // bury the one thing the user can act on under four things they cannot.
        var report = Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Failed),
            Check("Gekoppelte Geräte", DiagnosticStatus.Warning),
            Check("HFP-Transportgeräte", DiagnosticStatus.Warning));

        var steps = SetupAdvisor.Analyze(report);

        Assert.Single(steps);
        Assert.Equal(SetupSeverity.Blocking, steps[0].Severity);
        Assert.Contains("Adapter", steps[0].Title);
    }

    [Fact]
    public void Radio_switched_off_offers_to_open_the_settings()
    {
        var report = Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Ok),
            Check("Bluetooth eingeschaltet", DiagnosticStatus.Failed),
            Check("Gekoppelte Geräte", DiagnosticStatus.Ok),
            Check("HFP-Transportgeräte", DiagnosticStatus.Ok));

        var steps = SetupAdvisor.Analyze(report);

        Assert.Single(steps);
        Assert.True(steps[0].OpensSettings);
    }

    [Fact]
    public void Low_energy_only_adapter_is_reported_as_unfixable_by_software()
    {
        var report = Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Ok),
            Check("Bluetooth eingeschaltet", DiagnosticStatus.Ok),
            Check("Classic (BR/EDR) unterstützt", DiagnosticStatus.Failed),
            Check("Gekoppelte Geräte", DiagnosticStatus.Ok),
            Check("HFP-Transportgeräte", DiagnosticStatus.Ok));

        var steps = SetupAdvisor.Analyze(report);

        Assert.Single(steps);
        Assert.Equal(SetupSeverity.Blocking, steps[0].Severity);
        // The honest part: no button fixes this one.
        Assert.False(steps[0].OpensSettings);
    }

    [Fact]
    public void No_paired_phone_stops_before_complaining_about_the_profile()
    {
        // A phone that is not paired obviously offers no hands-free profile. Saying both
        // makes the second one look like a separate problem.
        var report = Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Ok),
            Check("Bluetooth eingeschaltet", DiagnosticStatus.Ok),
            Check("Gekoppelte Geräte", DiagnosticStatus.Warning),
            Check("HFP-Transportgeräte", DiagnosticStatus.Warning));

        var steps = SetupAdvisor.Analyze(report);

        Assert.Single(steps);
        Assert.Contains("gekoppelt", steps[0].Title);
    }

    [Fact]
    public void Paired_phone_without_call_permission_is_named_precisely()
    {
        // The exact case that cost an evening: paired, connected, and silent because the
        // phone's "Anrufe" switch was off.
        var report = Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Ok),
            Check("Bluetooth eingeschaltet", DiagnosticStatus.Ok),
            Check("Gekoppelte Geräte", DiagnosticStatus.Ok),
            Check("HFP-Transportgeräte", DiagnosticStatus.Warning));

        var steps = SetupAdvisor.Analyze(report);

        Assert.Single(steps);
        Assert.Equal(SetupSeverity.Important, steps[0].Severity);
        Assert.Contains("Anrufe", steps[0].Action);
    }

    [Fact]
    public void Summary_names_the_first_problem_rather_than_counting_them()
    {
        var steps = SetupAdvisor.Analyze(Report(
            Check("Bluetooth-Adapter", DiagnosticStatus.Ok),
            Check("Bluetooth eingeschaltet", DiagnosticStatus.Failed),
            Check("Gekoppelte Geräte", DiagnosticStatus.Ok),
            Check("HFP-Transportgeräte", DiagnosticStatus.Warning)));

        var summary = SetupAdvisor.Summarize(steps);

        Assert.Contains("ausgeschaltet", summary);
        Assert.Equal("Alles bereit.", SetupAdvisor.Summarize([]));
    }
}
