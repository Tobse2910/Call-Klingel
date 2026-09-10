using CallKlingel.Core.Hfp;
using Xunit;

namespace CallKlingel.Core.Tests;

public class HfpIndicatorTests
{
    /// <summary>The real reply captured from a Samsung S26 Ultra on 2026-09-10.</summary>
    private const string RealNames =
        "(\"call\",(0,1)),(\"callsetup\",(0-3)),(\"service\",(0-1)),(\"signal\",(0-5))," +
        "(\"roam\",(0,1)),(\"battchg\",(0-5)),(\"callheld\",(0-2))";

    private static HfpIndicators Parsed(string values)
    {
        var ind = new HfpIndicators();
        ind.SetNames(RealNames);
        ind.SetValues(values);
        return ind;
    }

    [Fact]
    public void Names_are_mapped_to_their_slots()
    {
        var ind = Parsed("0,0,1,3,0,4,0");

        Assert.Equal(0, ind.Call);
        Assert.Equal(0, ind.CallSetup);
        Assert.True(ind.HasService);
        Assert.Equal(3, ind.SignalStrength);
        Assert.Equal(4, ind.BatteryLevel);
    }

    [Fact]
    public void Battery_is_converted_from_the_five_step_scale()
    {
        Assert.Equal(80, Parsed("0,0,1,3,0,4,0").BatteryPercent);
        Assert.Equal(100, Parsed("0,0,1,3,0,5,0").BatteryPercent);
        Assert.Equal(0, Parsed("0,0,1,3,0,0,0").BatteryPercent);
    }

    [Fact]
    public void Incoming_call_sets_callsetup_to_one()
    {
        var ind = Parsed("0,0,1,3,0,4,0");
        ind.Update(2, 1); // callsetup is the second indicator

        Assert.Equal(1, ind.CallSetup);
        Assert.Equal(0, ind.Call);
    }

    [Fact]
    public void Answering_sets_call_and_clears_callsetup()
    {
        var ind = Parsed("0,1,1,3,0,4,0");
        ind.Update(1, 1);
        ind.Update(2, 0);

        Assert.Equal(1, ind.Call);
        Assert.Equal(0, ind.CallSetup);
    }

    [Fact]
    public void Unknown_indicator_is_reported_as_null_not_zero()
    {
        var ind = new HfpIndicators();
        ind.SetNames("(\"call\",(0,1))");
        ind.SetValues("0");

        Assert.Null(ind.BatteryLevel);
        Assert.Null(ind.BatteryPercent);
    }

    [Fact]
    public void Volume_commands_stay_inside_the_protocol_range()
    {
        Assert.Equal("AT+VGS=0", HfpProtocol.SetSpeakerVolume(-5));
        Assert.Equal("AT+VGS=15", HfpProtocol.SetSpeakerVolume(99));
        Assert.Equal("AT+VGM=0", HfpProtocol.SetMicrophoneVolume(0));
    }
}
