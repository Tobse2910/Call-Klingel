namespace CallKlingel.Core.Diagnostics;

public sealed record TelephonyDiagnosticsReport
{
    public required string Backend { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<DiagnosticCheck> Checks { get; init; } = [];

    public int CountOf(DiagnosticStatus s) => Checks.Count(c => c.Status == s);

    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Call Klingel - Telefonie-Diagnose ({Backend})");
        sb.AppendLine($"Erstellt: {GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 72));
        foreach (var group in Checks.GroupBy(c => c.Category))
        {
            sb.AppendLine();
            sb.AppendLine($"[{group.Key}]");
            foreach (var c in group)
            {
                sb.AppendLine($"  {c.Status,-8} {c.Name,-34} {c.Value}");
                if (!string.IsNullOrWhiteSpace(c.Probe)) sb.AppendLine($"           probe : {c.Probe}");
                if (!string.IsNullOrWhiteSpace(c.Detail)) sb.AppendLine($"           detail: {c.Detail}");
            }
        }
        return sb.ToString();
    }
}
