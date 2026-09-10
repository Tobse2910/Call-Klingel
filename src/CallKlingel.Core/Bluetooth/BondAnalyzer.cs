namespace CallKlingel.Core.Bluetooth;

/// <summary>Why telephony cannot start, expressed in terms the user can act on.</summary>
public enum BondProblem
{
    /// <summary>A Classic bond exists. Nothing here blocks HFP.</summary>
    None,

    /// <summary>No phone is known to this PC at all.</summary>
    NoDeviceKnown,

    /// <summary>
    /// The phone is bonded over Bluetooth LE but not over Classic. Windows shows it as a
    /// paired device, so this looks like success - but HFP is a Classic profile and can
    /// never run over an LE bond. Measured on 2026-09-10, see docs/WINDOWS_TELEPHONY.md.
    /// </summary>
    LowEnergyOnlyBond,

    /// <summary>The phone is in range but has not been paired yet.</summary>
    NotPairedYet
}

/// <summary>The result of looking at every endpoint of every nearby or known device.</summary>
public sealed record BondDiagnosis
{
    public required BondProblem Problem { get; init; }
    public required string Summary { get; init; }
    public required string Remedy { get; init; }

    public string? DeviceName { get; init; }
    public string? Address { get; init; }

    /// <summary>
    /// Set for <see cref="BondProblem.LowEnergyOnlyBond"/>: the endpoint that has to be
    /// unpaired before a Classic pairing can succeed.
    /// </summary>
    public string? StaleLowEnergyEndpointId { get; init; }

    public bool BlocksTelephony => Problem != BondProblem.None;
}

/// <summary>
/// Decides what is actually wrong with the Bluetooth bond.
///
/// This exists because "no paired device" was a misleading answer. On 2026-09-10 the phone
/// was paired - Windows Settings listed it, and the app still reported nothing, because the
/// bond was LE-only and every telephony API on Windows filters for Classic. Distinguishing
/// the two states is the difference between an actionable message and a dead end.
/// </summary>
public static class BondAnalyzer
{
    public static BondDiagnosis Analyze(IEnumerable<BluetoothEndpoint> endpoints)
    {
        var all = endpoints as IReadOnlyList<BluetoothEndpoint> ?? endpoints.ToList();

        // Bonds are per address and per transport. Judging the list as a whole is what made
        // an earlier version call a paired headset a working telephony bond while the phone
        // next to it was bonded over LE only.
        var classicBonds = all
            .Where(e => e.Transport == BluetoothTransport.Classic && e.IsPaired)
            .ToList();

        var classicAddresses = classicBonds
            .Select(e => e.NormalizedAddress)
            .ToHashSet(StringComparer.Ordinal);

        // A Classic bond on a device the system calls a phone is the healthy case, and it
        // outranks any stale LE bond left behind by some other device.
        var bondedPhone = classicBonds.FirstOrDefault(e => e.IsPhone);
        if (bondedPhone is not null) return Healthy(bondedPhone);

        // An LE bond with no Classic bond at the same address is the trap. It survives even
        // when the Classic endpoint is currently invisible - Classic endpoints only appear
        // during an inquiry, and the diagnosis must not depend on radio timing.
        var stale = all
            .Where(e => e.Transport == BluetoothTransport.LowEnergy
                        && e.IsPaired
                        && !classicAddresses.Contains(e.NormalizedAddress))
            .OrderByDescending(e => e.IsPhone)
            .FirstOrDefault();

        if (stale is not null)
        {
            return new BondDiagnosis
            {
                Problem = BondProblem.LowEnergyOnlyBond,
                DeviceName = stale.Name,
                Address = stale.Address,
                StaleLowEnergyEndpointId = stale.Id,
                Summary =
                    $"{stale.Name} ist nur über Bluetooth LE gekoppelt, nicht über Bluetooth "
                    + "Classic. Windows zeigt das Telefon deshalb als gekoppelt an, obwohl das "
                    + "Freisprech-Profil (HFP) fehlt - HFP läuft ausschliesslich über Classic.",
                Remedy =
                    "Die alte Kopplung auf beiden Seiten entfernen und neu koppeln: "
                    + "1. Am Telefon unter Bluetooth den PC \"Entkoppeln\" bzw. \"Vergessen\". "
                    + "2. Am PC dieselbe Kopplung entfernen. "
                    + "3. Neu koppeln und am Telefon die Nachfrage nach Zugriff auf Anrufe und "
                    + "Kontakte bestätigen."
            };
        }

        // No stale LE bond anywhere: a Classic bond, whatever the device class, is as good
        // an answer as this analysis can give.
        if (classicBonds.Count > 0) return Healthy(classicBonds[0]);

        var inRange = all.FirstOrDefault(e => e.Transport == BluetoothTransport.Classic);
        if (inRange is not null)
        {
            return new BondDiagnosis
            {
                Problem = BondProblem.NotPairedYet,
                DeviceName = inRange.Name,
                Address = inRange.Address,
                Summary = $"{inRange.Name} ist in Reichweite, aber nicht gekoppelt.",
                Remedy =
                    "Telefon koppeln und am Telefon die Nachfrage nach Zugriff auf Anrufe und "
                    + "Kontakte bestätigen."
            };
        }

        return new BondDiagnosis
        {
            Problem = BondProblem.NoDeviceKnown,
            Summary = "Diesem PC ist kein Telefon bekannt.",
            Remedy =
                "Am Telefon Bluetooth einschalten, die Bluetooth-Seite geöffnet lassen und "
                + "die Gerätesuche starten."
        };
    }

    private static BondDiagnosis Healthy(BluetoothEndpoint bonded) => new()
    {
        Problem = BondProblem.None,
        DeviceName = bonded.Name,
        Address = bonded.Address,
        Summary = $"{bonded.Name} ist über Bluetooth Classic gekoppelt.",
        Remedy = "Nichts zu tun."
    };
}
