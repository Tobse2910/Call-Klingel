using PhoneLinkPC.Core.Models;
using PhoneLinkPC.Core.Pbap;
using Xunit;

namespace PhoneLinkPC.Core.Tests;

public class ContactSearchTests
{
    private static Contact Make(string name, string number) =>
        new() { Id = Guid.NewGuid(), Name = name, PhoneNumber = number };

    [Fact]
    public void An_empty_query_matches_everything()
    {
        var contact = Make("Jule Welzheim", "+4915778828692");

        Assert.True(ContactSearch.Matches(contact, ""));
        Assert.True(ContactSearch.Matches(contact, "   "));
        Assert.True(ContactSearch.Matches(contact, null));
    }

    [Fact]
    public void Finds_by_any_part_of_the_name()
    {
        var contact = Make("Jule Welzheim", "+4915778828692");

        Assert.True(ContactSearch.Matches(contact, "Jule"));
        Assert.True(ContactSearch.Matches(contact, "Welz"));
        Assert.False(ContactSearch.Matches(contact, "Berta"));
    }

    [Fact]
    public void Ignores_capitalisation()
    {
        Assert.True(ContactSearch.Matches(Make("Jule Welzheim", "+49"), "jULE"));
    }

    [Fact]
    public void Finds_umlauts_typed_plainly()
    {
        // Nobody reaches for the umlaut key while searching in a hurry.
        var contact = Make("Schwäbisch Hall", "+491718223931");

        Assert.True(ContactSearch.Matches(contact, "Schwaebisch"));
        Assert.True(ContactSearch.Matches(contact, "Schwabisch"));
        Assert.True(ContactSearch.Matches(contact, "Schwäbisch"));
    }

    [Fact]
    public void Finds_by_number_regardless_of_spacing()
    {
        var contact = Make("Jule Welzheim", "+49 157 788 28692");

        Assert.True(ContactSearch.Matches(contact, "15778828692"));
        Assert.True(ContactSearch.Matches(contact, "157 788"));
        Assert.True(ContactSearch.Matches(contact, "88286"));
    }

    [Fact]
    public void A_national_number_finds_the_international_one()
    {
        // The list holds +49157..., people type 0157... - the same phone either way.
        var contact = Make("Jule Welzheim", "+4915778828692");

        Assert.True(ContactSearch.Matches(contact, "015778828692"));
        Assert.True(ContactSearch.Matches(contact, "0157788"));
    }

    [Fact]
    public void An_international_query_finds_a_nationally_stored_number()
    {
        var contact = Make("Jule Welzheim", "015778828692");

        Assert.True(ContactSearch.Matches(contact, "+4915778828692"));
    }

    [Fact]
    public void A_different_number_does_not_match()
    {
        var contact = Make("Jule Welzheim", "+4915778828692");

        Assert.False(ContactSearch.Matches(contact, "99999999"));
    }

    [Fact]
    public void Digits_in_a_query_can_still_match_a_name()
    {
        // Contacts like "Werkstatt 24" exist, so a digit must not switch off name matching.
        var contact = Make("Werkstatt 24", "+491234567890");

        Assert.True(ContactSearch.Matches(contact, "Werkstatt 24"));
        Assert.True(ContactSearch.Matches(contact, "24"));
    }
}
