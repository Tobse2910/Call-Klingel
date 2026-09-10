using System.Text;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Pbap;

/// <summary>
/// Turns the vCard 2.1 text a phone returns for its phonebook into contacts.
///
/// Written against what a Samsung S26 Ultra actually sent on 2026-09-10 rather than
/// against the specification alone: parameters like CHARSET sit between the property name
/// and the colon, umlauts may arrive quoted-printable, long lines are folded, and the same
/// person shows up once per account on the phone.
///
/// Platform neutral on purpose - the Linux backend will hand the same text to the same
/// parser.
/// </summary>
public static class VCardParser
{
    public static IReadOnlyList<Contact> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var contacts = new List<Contact>();

        // A number is matched by its last digits during a call, so the same person stored
        // once per account must not become several identical rows.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in SplitCards(text))
        {
            var (name, numbers) = ReadCard(card);
            if (string.IsNullOrWhiteSpace(name) || numbers.Count == 0) continue;

            foreach (var number in numbers)
            {
                if (!seen.Add($"{name}|{number}")) continue;

                contacts.Add(new Contact
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    PhoneNumber = number
                });
            }
        }

        return contacts;
    }

    private static IEnumerable<string> SplitCards(string text)
    {
        const string begin = "BEGIN:VCARD";
        const string end = "END:VCARD";

        var index = 0;
        while (true)
        {
            var start = text.IndexOf(begin, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0) yield break;

            var stop = text.IndexOf(end, start, StringComparison.OrdinalIgnoreCase);

            // A truncated final card still carries usable entries, so it is parsed as is.
            yield return stop < 0 ? text[start..] : text[start..stop];
            if (stop < 0) yield break;

            index = stop + end.Length;
        }
    }

    private static (string? Name, List<string> Numbers) ReadCard(string card)
    {
        string? formatted = null;
        string? structured = null;
        var numbers = new List<string>();

        foreach (var line in Unfold(card))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var head = line[..colon];
            var value = line[(colon + 1)..];

            // "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE" - the property is the first part.
            var parts = head.Split(';');
            var property = parts[0].Trim().ToUpperInvariant();

            var quotedPrintable = parts.Any(p =>
                p.Trim().Equals("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase));

            if (quotedPrintable) value = DecodeQuotedPrintable(value);

            switch (property)
            {
                case "FN":
                    formatted = value.Trim();
                    break;

                case "N":
                    structured = FormatStructuredName(value);
                    break;

                case "TEL":
                    var number = Compact(value);
                    if (number.Length > 0) numbers.Add(number);
                    break;
            }
        }

        var name = !string.IsNullOrWhiteSpace(formatted) ? formatted : structured;
        return (name, numbers);
    }

    /// <summary>vCard 2.1 folds a long line by starting the continuation with whitespace.</summary>
    private static IEnumerable<string> Unfold(string card)
    {
        var current = new StringBuilder();

        foreach (var raw in card.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                if (current.Length > 0) current.Append(' ').Append(raw.Trim());
                continue;
            }

            if (current.Length > 0) yield return current.ToString();
            current.Clear().Append(raw.Trim());
        }

        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>N is "family;given;middle;prefix;suffix"; people read given name first.</summary>
    private static string? FormatStructuredName(string value)
    {
        var fields = value.Split(';');
        var family = fields.Length > 0 ? fields[0].Trim() : string.Empty;
        var given = fields.Length > 1 ? fields[1].Trim() : string.Empty;

        var name = string.Join(' ', new[] { given, family }.Where(p => p.Length > 0));
        return name.Length > 0 ? name : null;
    }

    /// <summary>Strips the spacing people put into numbers so comparisons stay reliable.</summary>
    private static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
            if (!char.IsWhiteSpace(c))
                builder.Append(c);

        return builder.ToString();
    }

    /// <summary>
    /// Decodes "=C3=B6" style escapes. The bytes are UTF-8 in practice, so they are
    /// collected and decoded together rather than one at a time - a single umlaut spans
    /// two escapes and would otherwise come out as two broken characters.
    /// </summary>
    private static string DecodeQuotedPrintable(string value)
    {
        var bytes = new List<byte>(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length &&
                Convert.ToInt32(value.Substring(i + 1, 2), 16) is var b and >= 0)
            {
                bytes.Add((byte)b);
                i += 2;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
