namespace PhoneLinkPC.Core.Privacy;

/// <summary>
/// Masks phone numbers for log output. Logs must never contain a full number by default:
/// "+49 176 12345678" becomes "+49 176 **** 5678".
/// </summary>
public static class PhoneNumberMasker
{
    public static string Mask(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return "(unbekannt)";

        var digits = new string(number.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return "(keine Ziffern)";
        // Too short to mask meaningfully - hide it entirely rather than leak it.
        if (digits.Length <= 4) return new string('*', digits.Length);

        var prefix = number.StartsWith('+') ? "+" : "";
        var lead = digits[..Math.Min(5, digits.Length - 4)];
        var tail = digits[^4..];
        return $"{prefix}{lead} **** {tail}";
    }

    /// <summary>
    /// Masks every number inside a longer text, so raw protocol traffic can be logged.
    ///
    /// An AT line such as <c>+CLIP: "+4917612345678",145</c> carries the caller's number in
    /// the clear. Logging the traffic verbatim is what makes a failed call diagnosable, but
    /// the privacy rule holds regardless of how the number reaches the log, so the digits
    /// are masked on the way in rather than trusted not to appear.
    /// </summary>
    public static string MaskInText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // Five digits is the shortest run worth hiding: shorter ones are protocol values -
        // +CIND indicator lists, +BRSF feature bitmaps, +VGM gain levels - and masking
        // those would destroy exactly the information the log is being kept for.
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"\+?\d[\d ]{4,}\d",
            m => Mask(m.Value.Replace(" ", "")),
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(100));
    }
}
