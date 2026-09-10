using CallKlingel.Core.Models;
using CallKlingel.Core.Pbap;
using Xunit;

namespace CallKlingel.Core.Tests;

/// <summary>
/// The card produced here is handed to the phone over Object Push, and the phone's own
/// importer has to accept it. Anything it dislikes is silently dropped, so the shape is
/// pinned down by tests rather than by hope.
/// </summary>
public class VCardBuilderTests
{
    private static Contact Make(string name, string number) =>
        new() { Id = Guid.NewGuid(), Name = name, PhoneNumber = number };

    [Fact]
    public void Produces_a_complete_card()
    {
        var card = VCardBuilder.Build(Make("Max Mustermann", "+491761234567"));

        Assert.StartsWith("BEGIN:VCARD", card);
        Assert.EndsWith("END:VCARD\r\n", card);
        Assert.Contains("VERSION:2.1", card);
        Assert.Contains("FN:Max Mustermann", card);
        Assert.Contains("TEL;CELL:+491761234567", card);
    }

    [Fact]
    public void Lines_end_with_carriage_return_and_newline()
    {
        // vCard requires CRLF; a bare newline makes some importers reject the whole card.
        var card = VCardBuilder.Build(Make("Max", "+49176"));

        Assert.DoesNotContain(card.Replace("\r\n", ""), "\n");
    }

    [Fact]
    public void Splits_a_name_into_its_structured_form()
    {
        // N is "family;given;..." - phones sort their address book by it.
        var card = VCardBuilder.Build(Make("Max Mustermann", "+49176"));

        Assert.Contains("N:Mustermann;Max;;;", card);
    }

    [Fact]
    public void A_single_word_name_becomes_the_family_name()
    {
        var card = VCardBuilder.Build(Make("Werkstatt", "+49176"));

        Assert.Contains("N:Werkstatt;;;;", card);
    }

    [Fact]
    public void A_name_with_several_parts_keeps_the_last_as_family_name()
    {
        var card = VCardBuilder.Build(Make("Anna Maria Schmidt", "+49176"));

        Assert.Contains("N:Schmidt;Anna Maria;;;", card);
    }

    [Fact]
    public void Umlauts_are_declared_as_utf8()
    {
        // Without the charset the phone shows mojibake instead of the name.
        var card = VCardBuilder.Build(Make("Jörg Müller", "+49176"));

        Assert.Contains("CHARSET=UTF-8", card);
        Assert.Contains("Jörg Müller", card);
    }

    [Fact]
    public void Plain_names_carry_no_needless_charset()
    {
        var card = VCardBuilder.Build(Make("Max Mustermann", "+49176"));

        Assert.DoesNotContain("CHARSET", card);
    }

    [Fact]
    public void Semicolons_in_a_name_are_escaped()
    {
        // An unescaped semicolon would end the field and shift everything after it.
        var card = VCardBuilder.Build(Make("Meier; Sohn", "+49176"));

        Assert.Contains("\\;", card);
    }

    [Fact]
    public void A_contact_without_a_number_is_refused()
    {
        Assert.Throws<ArgumentException>(() => VCardBuilder.Build(Make("Ohne Nummer", "  ")));
    }

    [Fact]
    public void A_contact_without_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => VCardBuilder.Build(Make("  ", "+49176")));
    }
}
