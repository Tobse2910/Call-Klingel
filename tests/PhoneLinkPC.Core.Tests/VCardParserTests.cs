using PhoneLinkPC.Core.Pbap;
using Xunit;

namespace PhoneLinkPC.Core.Tests;

/// <summary>
/// The phone answers a phonebook request with vCard 2.1 text. These tests use the exact
/// shapes a Samsung S26 Ultra sent on 2026-09-10, including the quirks: CHARSET
/// parameters, quoted-printable umlauts, entries without a number, and several numbers
/// under one name.
/// </summary>
public class VCardParserTests
{
    [Fact]
    public void Reads_name_and_number()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            VERSION:2.1
            N:;Jule Welzheim;;;
            FN:Jule Welzheim
            TEL;CELL:+4915778828692
            END:VCARD
            """);

        var contact = Assert.Single(contacts);
        Assert.Equal("Jule Welzheim", contact.Name);
        Assert.Equal("+4915778828692", contact.PhoneNumber);
    }

    [Fact]
    public void Reads_every_card_in_the_stream()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            VERSION:2.1
            FN:Erste
            TEL;CELL:+491111111111
            END:VCARD
            BEGIN:VCARD
            VERSION:2.1
            FN:Zweite
            TEL;CELL:+492222222222
            END:VCARD
            """);

        Assert.Equal(2, contacts.Count);
        Assert.Equal(["Erste", "Zweite"], contacts.Select(c => c.Name));
    }

    [Fact]
    public void Charset_parameter_does_not_end_up_in_the_name()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN;CHARSET=UTF-8:Schwäbisch Hall
            TEL;CELL:+491718223931
            END:VCARD
            """);

        Assert.Equal("Schwäbisch Hall", Assert.Single(contacts).Name);
    }

    [Fact]
    public void Falls_back_to_the_structured_name_when_there_is_no_formatted_one()
    {
        // N is "family;given;middle;prefix;suffix" - the readable order is given then family.
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            N:Mustermann;Max;;;
            TEL;CELL:+491234567890
            END:VCARD
            """);

        Assert.Equal("Max Mustermann", Assert.Single(contacts).Name);
    }

    [Fact]
    public void An_entry_with_several_numbers_becomes_several_contacts()
    {
        // Otherwise only one of the numbers would ever resolve to the name during a call.
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN:Doppelt
            TEL;CELL:+491111111111
            TEL;HOME:+492222222222
            END:VCARD
            """);

        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, c => Assert.Equal("Doppelt", c.Name));
        Assert.Equal(["+491111111111", "+492222222222"], contacts.Select(c => c.PhoneNumber));
    }

    [Fact]
    public void Cards_without_a_number_are_skipped()
    {
        // A contact with no number can never match an incoming call, so keeping it would
        // only pad the list.
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN:Nur ein Name
            END:VCARD
            """);

        Assert.Empty(contacts);
    }

    [Fact]
    public void Cards_without_a_name_are_skipped()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            TEL;CELL:+491234567890
            END:VCARD
            """);

        Assert.Empty(contacts);
    }

    [Fact]
    public void Quoted_printable_umlauts_are_decoded()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:K=C3=B6ln
            TEL;CELL:+49221934708
            END:VCARD
            """);

        Assert.Equal("Köln", Assert.Single(contacts).Name);
    }

    [Fact]
    public void Folded_lines_are_joined()
    {
        // vCard 2.1 continues a long line by starting the next one with a space.
        var contacts = VCardParser.Parse(
            "BEGIN:VCARD\r\nFN:Ein sehr langer\r\n  Name\r\nTEL;CELL:+491234567890\r\nEND:VCARD\r\n");

        Assert.Equal("Ein sehr langer Name", Assert.Single(contacts).Name);
    }

    [Fact]
    public void Whitespace_inside_numbers_is_removed()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN:Mit Leerzeichen
            TEL;CELL:+49 176 1234 5678
            END:VCARD
            """);

        Assert.Equal("+4917612345678", Assert.Single(contacts).PhoneNumber);
    }

    [Fact]
    public void The_same_name_and_number_is_not_imported_twice()
    {
        // Phones commonly return one card per account, so the same person arrives more
        // than once. Duplicates would show up as repeated rows in the list.
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN:Doppelgaenger
            TEL;CELL:+491234567890
            END:VCARD
            BEGIN:VCARD
            FN:Doppelgaenger
            TEL;CELL:+49 123 4567890
            END:VCARD
            """);

        Assert.Single(contacts);
    }

    [Fact]
    public void Empty_input_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(VCardParser.Parse(""));
        Assert.Empty(VCardParser.Parse("   \r\n  "));
    }

    [Fact]
    public void Every_contact_gets_its_own_identity()
    {
        var contacts = VCardParser.Parse(
            """
            BEGIN:VCARD
            FN:Erste
            TEL;CELL:+491111111111
            END:VCARD
            BEGIN:VCARD
            FN:Zweite
            TEL;CELL:+492222222222
            END:VCARD
            """);

        Assert.Equal(2, contacts.Select(c => c.Id).Distinct().Count());
        Assert.DoesNotContain(contacts, c => c.Id == Guid.Empty);
    }
}
