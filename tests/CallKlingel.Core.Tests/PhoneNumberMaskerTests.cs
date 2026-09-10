using CallKlingel.Core.Privacy;
using Xunit;

namespace CallKlingel.Core.Tests;

public class PhoneNumberMaskerTests
{
    [Fact]
    public void Mask_keeps_country_prefix_and_last_four_digits()
    {
        var masked = PhoneNumberMasker.Mask("+49 176 12345678");

        Assert.StartsWith("+", masked);
        Assert.EndsWith("5678", masked);
        Assert.Contains("****", masked);
    }

    [Fact]
    public void Mask_never_contains_the_full_number()
    {
        Assert.DoesNotContain("12345678", PhoneNumberMasker.Mask("+49 176 12345678"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mask_handles_missing_numbers(string? input)
    {
        Assert.Equal("(unbekannt)", PhoneNumberMasker.Mask(input));
    }

    [Fact]
    public void Mask_hides_short_numbers_entirely()
    {
        Assert.Equal("***", PhoneNumberMasker.Mask("110"));
    }

    [Fact]
    public void MaskInText_hides_the_caller_number_in_a_clip_line()
    {
        var masked = PhoneNumberMasker.MaskInText("+CLIP: \"+4917612345678\",145");

        Assert.DoesNotContain("4917612345678", masked);
        Assert.Contains("****", masked);
        Assert.Contains("+CLIP", masked);
    }

    [Fact]
    public void MaskInText_keeps_short_protocol_values_readable()
    {
        // +CIND and +CIEV carry indicator values, not numbers. Masking them would remove
        // exactly what the trace is kept for.
        Assert.Equal("+CIEV: 2,1", PhoneNumberMasker.MaskInText("+CIEV: 2,1"));
        Assert.Equal("+BRSF: 871", PhoneNumberMasker.MaskInText("+BRSF: 871"));
    }

    [Fact]
    public void MaskInText_leaves_plain_responses_untouched()
    {
        Assert.Equal("RING", PhoneNumberMasker.MaskInText("RING"));
        Assert.Equal("OK", PhoneNumberMasker.MaskInText("OK"));
    }
}
