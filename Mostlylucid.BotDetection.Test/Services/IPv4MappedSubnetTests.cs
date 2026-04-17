using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Tests for IPv4-mapped IPv6 subnet normalization.
///     Regression test for the bug where ::ffff:x.x.x.x addresses were grouped
///     into the ::ffff::/48 subnet, causing ALL IPv4-mapped addresses to share
///     reputation and leading to false positive VerifiedBadBot verdicts.
/// </summary>
public class IPv4MappedSubnetTests
{
    [Theory]
    [InlineData("192.168.1.100", "192.168.1.0/24")]
    [InlineData("10.0.0.1", "10.0.0.0/24")]
    [InlineData("8.8.8.8", "8.8.8.0/24")]
    public void NormalizeIpToRange_PureIPv4_Returns24Subnet(string ip, string expected)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("::ffff:192.168.1.100", "192.168.1.0/24")]
    [InlineData("::ffff:10.0.0.1", "10.0.0.0/24")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.0/24")]
    [InlineData("::ffff:8.8.8.8", "8.8.8.0/24")]
    public void NormalizeIpToRange_IPv4Mapped_ExtractsIPv4AndReturns24Subnet(string ip, string expected)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeIpToRange_IPv4Mapped_DoesNotReturn48Subnet()
    {
        // This was the bug: ::ffff:192.168.0.86 was being normalized to ::ffff::/48
        // which matched ALL IPv4-mapped addresses
        var result = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:192.168.0.86");

        Assert.DoesNotContain("/48", result);
        Assert.DoesNotContain("::ffff::", result);
        Assert.Equal("192.168.0.0/24", result);
    }

    [Fact]
    public void NormalizeIpToRange_DifferentIPv4Mapped_DifferentSubnets()
    {
        // These should be in DIFFERENT subnets, not the same ::ffff::/48
        var local = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:127.0.0.1");
        var lan = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:192.168.0.86");
        var public1 = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:8.8.8.8");

        Assert.NotEqual(local, lan);
        Assert.NotEqual(lan, public1);
        Assert.NotEqual(local, public1);
    }

    [Theory]
    [InlineData("2001:db8:1234::1", "2001:db8:1234::/48")]
    public void NormalizeIpToRange_PureIPv6_Returns48Subnet(string ip, string expected)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(expected, result);
    }
}
