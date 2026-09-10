namespace CallKlingel.Core.Diagnostics;

public enum DiagnosticStatus
{
    /// <summary>Not probed yet.</summary>
    Unknown,
    /// <summary>Probed, works.</summary>
    Ok,
    /// <summary>Probed, works but with a limitation worth showing.</summary>
    Warning,
    /// <summary>Probed, does not work.</summary>
    Failed,
    /// <summary>Could not be probed because a precondition was missing.</summary>
    Skipped,
    /// <summary>Informational value, not a pass/fail check.</summary>
    Info
}

/// <summary>
/// One probed fact about the host. Every check records what was actually attempted
/// (<see cref="Probe"/>) so a red row can be reproduced by hand.
/// </summary>
public sealed record DiagnosticCheck
{
    public required string Name { get; init; }
    public DiagnosticStatus Status { get; init; } = DiagnosticStatus.Unknown;

    /// <summary>Short result shown in the value column.</summary>
    public string Value { get; init; } = "-";

    /// <summary>Full detail: HRESULT, exception text, or the reason a check was skipped.</summary>
    public string? Detail { get; init; }

    /// <summary>The concrete API call or command this row represents.</summary>
    public string? Probe { get; init; }

    public string Category { get; init; } = "Allgemein";
}
