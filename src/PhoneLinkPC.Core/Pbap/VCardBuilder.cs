using System.Text;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Pbap;

/// <summary>
/// Writes a contact as a vCard 2.1 card for sending to the phone over Object Push.
///
/// Version 2.1 rather than 3.0 or 4.0 because that is what Android's own Bluetooth
/// importer handles most reliably, and it is the version the phone itself uses when it
/// hands out its phonebook - observed on 2026-09-10.
/// </summary>
public static class VCardBuilder
{
    public static string Build(Contact contact)
    {
        if (string.IsNullOrWhiteSpace(contact.Name))
            throw new ArgumentException("Ein Kontakt ohne Namen kann nicht gesendet werden.", nameof(contact));

        if (string.IsNullOrWhiteSpace(contact.PhoneNumber))
            throw new ArgumentException("Ein Kontakt ohne Rufnummer kann nicht gesendet werden.", nameof(contact));

        var name = contact.Name.Trim();
        var number = contact.PhoneNumber.Trim();
        var (family, given) = SplitName(name);

        // Only declare the charset when it is actually needed - some older importers trip
        // over the parameter on plain ASCII cards.
        var charset = name.Any(c => c > 127) ? ";CHARSET=UTF-8" : string.Empty;

        var card = new StringBuilder();
        card.Append("BEGIN:VCARD\r\n");
        card.Append("VERSION:2.1\r\n");
        card.Append($"N{charset}:{Escape(family)};{Escape(given)};;;\r\n");
        card.Append($"FN{charset}:{Escape(name)}\r\n");
        card.Append($"TEL;CELL:{number}\r\n");
        card.Append("END:VCARD\r\n");

        return card.ToString();
    }

    /// <summary>
    /// Splits "Anna Maria Schmidt" into family "Schmidt" and given "Anna Maria". A single
    /// word becomes the family name, which is how phones file businesses and nicknames.
    /// </summary>
    private static (string Family, string Given) SplitName(string name)
    {
        var space = name.LastIndexOf(' ');
        return space <= 0
            ? (name, string.Empty)
            : (name[(space + 1)..].Trim(), name[..space].Trim());
    }

    /// <summary>A raw semicolon or comma would end the field and shift every value after it.</summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,");
}
