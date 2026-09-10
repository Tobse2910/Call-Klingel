using PhoneLinkPC.Core.Diagnostics;

namespace PhoneLinkPC.Core.Setup;

/// <summary>How badly a missing requirement hurts.</summary>
public enum SetupSeverity
{
    /// <summary>Nothing works until this is fixed.</summary>
    Blocking,

    /// <summary>The app runs, but the feature the user came for does not.</summary>
    Important
}

/// <summary>
/// One thing the user has to do, phrased as a task rather than as a fault.
/// </summary>
/// <param name="Title">What is missing, in plain words.</param>
/// <param name="Explanation">Why it matters, so the step is not just an order.</param>
/// <param name="Action">The concrete next move. Never "check your settings".</param>
/// <param name="Severity">Blocking or merely important.</param>
/// <param name="OpensSettings">
/// True when the app can put the user in the right place itself. A button beats an
/// instruction: "open Windows settings, then Bluetooth, then..." is a list of chances to
/// get lost.
/// </param>
public sealed record SetupStep(
    string Title,
    string Explanation,
    string Action,
    SetupSeverity Severity,
    bool OpensSettings = false);

/// <summary>
/// Turns a diagnostic report into a list of things the user can actually do.
///
/// The diagnostics view answers "what did the system say"; this answers "what do I do
/// now". They are different questions and deserve different words: nobody who just wants
/// to answer a call should have to work out what "0 HFP-Transportgeräte" means for them.
///
/// Pure logic on a report, so it is testable without any hardware.
/// </summary>
public static class SetupAdvisor
{
    /// <summary>
    /// Reads the report and returns what is missing, worst first. An empty list means the
    /// machine is ready - and then the UI should say nothing at all.
    /// </summary>
    public static IReadOnlyList<SetupStep> Analyze(TelephonyDiagnosticsReport report)
    {
        var steps = new List<SetupStep>();

        var checks = report.Checks;

        DiagnosticCheck? Find(string namePart) =>
            checks.FirstOrDefault(c => c.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase));

        // --- 1. Is there a radio at all -------------------------------------------
        var adapter = Find("Bluetooth-Adapter");
        if (adapter is { Status: DiagnosticStatus.Failed })
        {
            steps.Add(new SetupStep(
                "Kein Bluetooth-Adapter gefunden",
                "Ohne Bluetooth kann dieser PC kein Telefon erreichen. Viele Desktop-PCs "
                + "haben ab Werk keins - Notebooks fast immer.",
                "Einen Bluetooth-USB-Stick anschließen. Er muss Bluetooth Classic können, "
                + "nicht nur Low Energy. Danach die App neu starten.",
                SetupSeverity.Blocking));

            // Everything below needs an adapter; naming it once is enough.
            return steps;
        }

        // --- 2. Is it switched on --------------------------------------------------
        var radio = Find("Bluetooth eingeschaltet");
        if (radio is { Status: DiagnosticStatus.Failed or DiagnosticStatus.Warning })
        {
            steps.Add(new SetupStep(
                "Bluetooth ist ausgeschaltet",
                "Der Adapter ist vorhanden, aber abgeschaltet. Das passiert auch durch den "
                + "Flugmodus.",
                "Bluetooth einschalten.",
                SetupSeverity.Blocking,
                OpensSettings: true));
        }

        // --- 3. Classic, not just LE ----------------------------------------------
        var classic = Find("Classic");
        if (classic is { Status: DiagnosticStatus.Failed })
        {
            steps.Add(new SetupStep(
                "Adapter beherrscht kein Bluetooth Classic",
                "Das Freisprechprofil läuft ausschließlich über Bluetooth Classic. Ein reiner "
                + "Low-Energy-Adapter kann kein Telefonat übertragen - daran lässt sich per "
                + "Software nichts ändern.",
                "Einen Adapter mit Classic-Unterstützung (BR/EDR) verwenden.",
                SetupSeverity.Blocking));
        }

        // --- 4. Is a phone paired --------------------------------------------------
        var paired = Find("Gekoppelte Ger");
        if (paired is { Status: DiagnosticStatus.Warning or DiagnosticStatus.Failed })
        {
            steps.Add(new SetupStep(
                "Noch kein Telefon gekoppelt",
                "Der PC und das Telefon müssen sich einmalig kennenlernen. Das passiert "
                + "im Windows-Kopplungsdialog, weil nur der den PC gleichzeitig sichtbar macht.",
                "Am Telefon die Bluetooth-Seite öffnen und geöffnet lassen, dann hier auf "
                + "\"Telefon koppeln\" klicken.",
                SetupSeverity.Blocking,
                OpensSettings: true));

            return steps;
        }

        // --- 5. Does the paired phone offer hands-free ----------------------------
        var hfp = Find("HFP-Transportger");
        if (hfp is { Status: DiagnosticStatus.Warning or DiagnosticStatus.Failed })
        {
            steps.Add(new SetupStep(
                "Telefonieprofil am Telefon nicht freigegeben",
                "Das Telefon ist gekoppelt, gibt aber seine Anrufe nicht an diesen PC weiter. "
                + "Ohne diese Freigabe meldet es nicht einmal, dass es klingelt.",
                "Am Telefon: Bluetooth öffnen, beim Eintrag dieses PCs auf das Zahnrad tippen "
                + "und \"Anrufe\" einschalten.",
                SetupSeverity.Important));
        }

        return steps;
    }

    /// <summary>
    /// A one-line summary for the places that have room for a sentence, not a list.
    /// </summary>
    public static string Summarize(IReadOnlyList<SetupStep> steps) => steps.Count switch
    {
        0 => "Alles bereit.",
        1 => steps[0].Title,
        _ => $"{steps[0].Title} - und {steps.Count - 1} weitere{(steps.Count == 2 ? "r" : "")} Punkt"
             + (steps.Count > 2 ? "e" : "")
    };
}
