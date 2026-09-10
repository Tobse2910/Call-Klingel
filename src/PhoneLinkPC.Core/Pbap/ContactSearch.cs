using System.Globalization;
using System.Text;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Pbap;

/// <summary>
/// Filters the address book as the user types.
///
/// A phonebook imported from a phone is large - 575 entries on 2026-09-10 - and the
/// entries are not spelled the way anyone searches for them: numbers are stored
/// internationally while people type them nationally, and names carry umlauts that nobody
/// reaches for mid-search.
/// </summary>
public static class ContactSearch
{
    /// <summary>The German country code, so 0157... and +49157... find each other.</summary>
    private const string CountryCode = "49";

    public static bool Matches(Contact contact, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        var needle = query.Trim();

        // "Schwäbisch" is searched for as "Schwäbisch" and as "Schwabisch", so both
        // spellings are generated for each side and any pairing counts as a hit.
        var haystack = Variants(contact.Name);
        var needles = Variants(needle);

        if (haystack.Any(h => needles.Any(n => h.Contains(n, StringComparison.Ordinal))))
            return true;

        // Names win first; a query without digits can never match a number anyway.
        var digits = Digits(needle);
        if (digits.Length == 0) return false;

        var number = Digits(contact.PhoneNumber);
        return number.Length > 0 &&
               (number.Contains(digits, StringComparison.Ordinal) ||
                National(number).Contains(National(digits), StringComparison.Ordinal));
    }

    /// <summary>
    /// Reduces a number to a form that compares across notations: the country code and any
    /// trunk zero are dropped, so 0157..., +49157... and 49157... all end up the same.
    /// </summary>
    private static string National(string digits)
    {
        var value = digits;

        if (value.StartsWith(CountryCode, StringComparison.Ordinal) && value.Length > CountryCode.Length)
            value = value[CountryCode.Length..];

        return value.TrimStart('0');
    }

    private static string Digits(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
            if (char.IsAsciiDigit(c))
                builder.Append(c);

        return builder.ToString();
    }

    /// <summary>
    /// The two ways a German speaker writes a word with umlauts on a keyboard: spelled out
    /// ("schwäbisch") and simply unaccented ("schwabisch"). Both are produced so a search
    /// hits regardless of which one the user reached for.
    /// </summary>
    private static string[] Variants(string value)
    {
        var lower = value.ToLowerInvariant();

        var spelled = new StringBuilder(lower.Length);
        foreach (var c in lower)
            spelled.Append(c switch
            {
                'ä' => "ae",
                'ö' => "oe",
                'ü' => "ue",
                'ß' => "ss",
                _ => c.ToString()
            });

        var stripped = StripAccents(lower);
        var expanded = spelled.ToString();

        return expanded == stripped ? [expanded] : [expanded, stripped];
    }

    /// <summary>Decomposes, then drops the accent marks: "ä" becomes "a", "é" becomes "e".</summary>
    private static string StripAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(c);

        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
