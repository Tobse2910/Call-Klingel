namespace PhoneLinkPC.Core.Abstractions;

/// <summary>
/// Result of a telephony action. Actions never throw for expected failures - they report
/// them, so the UI can show the truth instead of pretending an action worked.
/// </summary>
public sealed record TelephonyResult(bool Success, string? Error = null)
{
    public static TelephonyResult Ok() => new(true);
    public static TelephonyResult Fail(string error) => new(false, error);

    /// <summary>The backend exists but the platform does not (yet) allow this operation.</summary>
    public static TelephonyResult NotSupported(string what) =>
        new(false, $"Nicht unterstützt: {what}");
}
