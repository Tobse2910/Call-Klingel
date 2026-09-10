using PhoneLinkPC.Core.Bluetooth;
using Xunit;

namespace PhoneLinkPC.Core.Tests;

/// <summary>
/// These tests encode the failure that blocked the project on 2026-09-10: the phone was
/// bonded over Bluetooth LE only, so Windows listed it as paired while no Classic bond -
/// and therefore no Hands-Free service - existed. The app reported "no device paired",
/// which sent the user looking in entirely the wrong place.
/// </summary>
public class BondAnalyzerTests
{
    private static BluetoothEndpoint Classic(string address, bool paired, string name = "S26 Ultra") =>
        new() { Address = address, Name = name, Transport = BluetoothTransport.Classic, IsPaired = paired };

    private static BluetoothEndpoint Le(string address, bool paired, string name = "S26 Ultra") =>
        new() { Address = address, Name = name, Transport = BluetoothTransport.LowEnergy, IsPaired = paired };

    private static BluetoothEndpoint Phone(BluetoothEndpoint endpoint) => endpoint with { IsPhone = true };

    [Fact]
    public void A_classic_bond_is_the_healthy_case()
    {
        var result = BondAnalyzer.Analyze([Classic("aa:bb:cc:dd:ee:ff", paired: true)]);

        Assert.Equal(BondProblem.None, result.Problem);
    }

    [Fact]
    public void Le_paired_while_classic_is_not_is_reported_as_an_le_only_bond()
    {
        // Exactly the state measured on 2026-09-10.
        var result = BondAnalyzer.Analyze([
            Classic("aa:bb:cc:dd:ee:ff", paired: false),
            Le("aa:bb:cc:dd:ee:ff", paired: true)
        ]);

        Assert.Equal(BondProblem.LowEnergyOnlyBond, result.Problem);
        Assert.Equal("S26 Ultra", result.DeviceName);
    }

    [Fact]
    public void An_le_only_bond_is_found_even_when_no_classic_endpoint_is_in_range()
    {
        // The Classic endpoint only appears during an inquiry. Its absence must not hide
        // the stale LE bond, otherwise the diagnosis flips depending on radio timing.
        var result = BondAnalyzer.Analyze([Le("aa:bb:cc:dd:ee:ff", paired: true)]);

        Assert.Equal(BondProblem.LowEnergyOnlyBond, result.Problem);
    }

    [Fact]
    public void Address_formatting_does_not_hide_the_pairing()
    {
        // Windows spells the address with colons on the AEP and without on the device node.
        var result = BondAnalyzer.Analyze([
            Classic("AA:BB:CC:DD:EE:FF", paired: true),
            Le("aabbccddeeff", paired: true)
        ]);

        Assert.Equal(BondProblem.None, result.Problem);
    }

    [Fact]
    public void A_classic_bond_on_another_device_does_not_excuse_the_phones_le_only_bond()
    {
        // A paired headset must not make the phone's broken bond look healthy. The two
        // addresses have to differ - that is the whole point of the test, and giving both
        // devices the same one turns it into a single device with two endpoints.
        var result = BondAnalyzer.Analyze([
            Classic("11:22:33:44:55:66", paired: true, name: "Kopfhoerer"),
            Le("aa:bb:cc:dd:ee:ff", paired: true)
        ]);

        Assert.Equal(BondProblem.LowEnergyOnlyBond, result.Problem);
        Assert.Equal("S26 Ultra", result.DeviceName);
    }

    [Fact]
    public void An_unpaired_classic_device_in_range_is_simply_not_paired_yet()
    {
        var result = BondAnalyzer.Analyze([Classic("aa:bb:cc:dd:ee:ff", paired: false)]);

        Assert.Equal(BondProblem.NotPairedYet, result.Problem);
    }

    [Fact]
    public void Nothing_known_is_reported_as_such()
    {
        var result = BondAnalyzer.Analyze([]);

        Assert.Equal(BondProblem.NoDeviceKnown, result.Problem);
    }

    [Fact]
    public void The_le_only_diagnosis_names_the_endpoint_so_the_stale_bond_can_be_removed()
    {
        var le = Le("aa:bb:cc:dd:ee:ff", paired: true) with { Id = "BluetoothLE#...-aa:bb:cc:dd:ee:ff" };

        var result = BondAnalyzer.Analyze([le]);

        Assert.Equal("BluetoothLE#...-aa:bb:cc:dd:ee:ff", result.StaleLowEnergyEndpointId);
    }

    [Fact]
    public void Every_problem_carries_a_remedy_the_user_can_act_on()
    {
        foreach (var endpoints in new[]
                 {
                     new[] { Le("aa:bb:cc:dd:ee:ff", true) },
                     [Classic("aa:bb:cc:dd:ee:ff", false)],
                     []
                 })
        {
            var result = BondAnalyzer.Analyze(endpoints);
            Assert.False(string.IsNullOrWhiteSpace(result.Summary));
            Assert.False(string.IsNullOrWhiteSpace(result.Remedy));
        }
    }

    [Fact]
    public void A_properly_bonded_phone_is_not_disturbed_by_an_le_only_smartwatch()
    {
        // The watch is bonded over LE only and always will be - that is normal for LE
        // accessories and must never be reported as the reason telephony does not work.
        var result = BondAnalyzer.Analyze([
            Phone(Classic("aa:bb:cc:dd:ee:ff", paired: true)),
            Le("11:22:33:44:55:66", paired: true, name: "Galaxy Watch")
        ]);

        Assert.Equal(BondProblem.None, result.Problem);
        Assert.Equal("S26 Ultra", result.DeviceName);
    }

    [Fact]
    public void The_phone_is_preferred_over_another_device_with_a_stale_le_bond()
    {
        var result = BondAnalyzer.Analyze([
            Le("11:22:33:44:55:66", paired: true, name: "Irgendein Sensor"),
            Phone(Le("aa:bb:cc:dd:ee:ff", paired: true))
        ]);

        Assert.Equal(BondProblem.LowEnergyOnlyBond, result.Problem);
        Assert.Equal("S26 Ultra", result.DeviceName);
    }

    [Fact]
    public void Both_transports_bonded_on_the_same_phone_is_healthy()
    {
        // The normal, working state: Android bonds Classic and derives the LE key from it.
        var result = BondAnalyzer.Analyze([
            Phone(Classic("aa:bb:cc:dd:ee:ff", paired: true)),
            Le("aa:bb:cc:dd:ee:ff", paired: true)
        ]);

        Assert.Equal(BondProblem.None, result.Problem);
    }
}
