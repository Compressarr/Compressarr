using System.Net;
using Compressarr.Core.Auth;

namespace Compressarr.Core.Tests.Auth;

/// <summary>What is "the local network" (Sonarr's ranges, from the TCP peer only) and which requests are free of credentials.</summary>
public class LocalNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.0", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.16.0.0", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.0", false)]
    [InlineData("192.167.255.255", false)]
    [InlineData("192.168.0.0", true)]
    [InlineData("192.168.10.91", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.169.0.0", false)]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.253.255.255", false)]
    [InlineData("169.255.0.0", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("100.64.0.1", false)]            // carrier-grade NAT is not "local" on its own
    [InlineData("0.0.0.0", false)]
    [InlineData("fe80::1", true)]                // link-local
    [InlineData("fd12:3456:789a::1", true)]      // unique-local
    [InlineData("fc00::1", true)]
    [InlineData("fec0::1", true)]                // site-local
    [InlineData("2001:db8::1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("::ffff:192.168.1.5", true)]     // an IPv4 address wrapped in IPv6 is unwrapped first
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("::ffff:172.32.0.1", false)]
    public void IsLocalAddress_FollowsTheRanges(string address, bool local) =>
        Assert.Equal(local, LocalNetwork.IsLocalAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.100.50.1", true)]            // a Tailscale address
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.0", false)]
    [InlineData("101.64.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::ffff:100.64.0.1", true)]
    [InlineData("fd7a:115c:a1e0::1", false)]      // Tailscale's IPv6 range is unique-local, which IsLocalAddress already covers
    public void IsCgnatAddress_IsTheSharedAddressSpace(string address, bool cgnat) =>
        Assert.Equal(cgnat, LocalNetwork.IsCgnatAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("10.0.0.1", false)]
    public void IsLoopback_IsOnlyThisComputer(string address, bool loopback) =>
        Assert.Equal(loopback, LocalNetwork.IsLoopback(IPAddress.Parse(address)));
}

public class WebAuthPolicyTests
{
    private static AuthSettings Settings(AuthMethod method = AuthMethod.Forms, AuthRequired required = AuthRequired.DisabledForLocalAddresses, bool cgnat = false) =>
        new(method, required, cgnat, "user", true);

    private static IPAddress Ip(string address) => IPAddress.Parse(address);

    [Fact]
    public void TheDefault_IsFormsWithTheLocalNetworkFree_AndNothingSetUp()
    {
        var d = AuthSettings.Default;

        Assert.Equal((AuthMethod.Forms, AuthRequired.DisabledForLocalAddresses, false, null, false), (d.Method, d.Required, d.TrustCgnat, d.Username, d.HasPassword));
        Assert.False(d.CredentialsSet);
    }

    [Fact]
    public void NoAuthentication_FreesEveryone_EvenAnUnknownAddress()
    {
        Assert.True(WebAuthPolicy.IsFree(Settings(AuthMethod.None, AuthRequired.Enabled), Ip("8.8.8.8"), hasForwardingHeader: true));
        Assert.True(WebAuthPolicy.IsFree(Settings(AuthMethod.None), null, hasForwardingHeader: false));
    }

    [Theory]
    [InlineData("192.168.10.5", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.9", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1234", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.9", false)]
    [InlineData("2001:db8::1", false)]
    public void DisabledForLocalAddresses_FreesTheLocalNetwork_Only(string address, bool free)
    {
        Assert.Equal(free, WebAuthPolicy.IsFree(Settings(AuthMethod.Forms), Ip(address), false));
        Assert.Equal(free, WebAuthPolicy.IsFree(Settings(AuthMethod.Basic), Ip(address), false));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.10.5", false)]
    [InlineData("10.1.2.3", false)]
    public void DisabledForLocalhost_FreesOnlyThisComputer(string address, bool free) =>
        Assert.Equal(free, WebAuthPolicy.IsFree(Settings(required: AuthRequired.DisabledForLocalhost), Ip(address), false));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.10.5")]
    [InlineData("8.8.8.8")]
    public void Enabled_FreesNobody(string address) =>
        Assert.False(WebAuthPolicy.IsFree(Settings(required: AuthRequired.Enabled), Ip(address), false));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.10.5")]
    [InlineData("10.0.0.1")]
    public void AnyForwardingHeader_MakesALocalAddressNotLocal(string address)
    {
        Assert.False(WebAuthPolicy.IsFree(Settings(), Ip(address), hasForwardingHeader: true));
        Assert.False(WebAuthPolicy.IsFree(Settings(required: AuthRequired.DisabledForLocalhost), Ip(address), hasForwardingHeader: true));
    }

    [Fact]
    public void AnUnknownPeerAddress_IsNeverLocal() =>
        Assert.False(WebAuthPolicy.IsFree(Settings(), null, false));

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("x-forwarded-for")]
    [InlineData("Forwarded")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Forwarded-Port")]
    [InlineData("X-Client-IP")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("True-Client-IP")]
    public void EveryProxyHeader_IsRecognised_InAnyCase(string header)
    {
        Assert.True(WebAuthPolicy.HasForwardingHeader(new[] { "Accept", header, "Host" }));
    }

    [Fact]
    public void OrdinaryHeaders_AreNotForwardingHeaders() =>
        Assert.False(WebAuthPolicy.HasForwardingHeader(new[] { "Accept", "Host", "User-Agent", "Cookie", "X-Api-Key", "Origin" }));

    [Fact]
    public void CarrierGradeNat_IsLocalOnlyWhenTheBoxIsTicked()
    {
        Assert.False(WebAuthPolicy.IsFree(Settings(), Ip("100.101.102.103"), false));
        Assert.True(WebAuthPolicy.IsFree(Settings(cgnat: true), Ip("100.101.102.103"), false));
        Assert.False(WebAuthPolicy.IsFree(Settings(cgnat: true), Ip("100.101.102.103"), true));   // still not through a proxy header
        Assert.False(WebAuthPolicy.IsFree(Settings(required: AuthRequired.Enabled, cgnat: true), Ip("100.101.102.103"), false));
        Assert.False(WebAuthPolicy.IsFree(Settings(required: AuthRequired.DisabledForLocalhost, cgnat: true), Ip("100.101.102.103"), false));
    }
}
