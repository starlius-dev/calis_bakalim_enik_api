using System.Net;
using CalisBakalimEnik.Api.Extensions;
using FluentAssertions;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// Who is allowed to tell this API where a request came from.
/// </summary>
/// <remarks>
/// The rate limiter, the brute-force lockout and the audit trail all partition
/// on the client address, so the trust list is what stands between those and a
/// caller who picks their own identity.
/// </remarks>
public class TrustListTests
{
    private static bool Contains(IEnumerable<IPNetwork> list, string address) =>
        list.Any(n => n.Contains(IPAddress.Parse(address)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_list_is_never_empty(bool isProduction)
    {
        // This is the one that matters. ForwardedHeadersMiddleware only checks
        // the peer when KnownNetworks or KnownProxies has an entry — with both
        // empty it SKIPS the check and takes the header from anyone. An empty
        // list is not "ignore the header", it is "believe everybody", and the
        // comment this replaced said the opposite.
        CloudflareRanges.TrustList([], isProduction).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loopback_is_always_trusted(bool isProduction)
    {
        // cloudflared runs on the same host and connects over 127.0.0.1, which
        // is not one of Cloudflare's published ranges. Without this the
        // tunnel's header is ignored and every user on earth shares the
        // 127.0.0.1 bucket.
        var list = CloudflareRanges.TrustList([], isProduction);

        Contains(list, "127.0.0.1").Should().BeTrue();
        Contains(list, "::1").Should().BeTrue();
    }

    [Fact]
    public void Production_trusts_Cloudflares_published_ranges()
    {
        var list = CloudflareRanges.TrustList([], isProduction: true);

        Contains(list, "104.16.0.1").Should().BeTrue();
        Contains(list, "172.64.0.1").Should().BeTrue();
    }

    [Fact]
    public void Outside_production_only_loopback_is_trusted()
    {
        // A developer machine has no tunnel in front of it, so nothing but the
        // machine itself may name the client.
        var list = CloudflareRanges.TrustList([], isProduction: false);

        Contains(list, "127.0.0.1").Should().BeTrue();
        Contains(list, "104.16.0.1").Should().BeFalse();
        Contains(list, "8.8.8.8").Should().BeFalse();
    }

    [Fact]
    public void Configured_ranges_replace_the_published_ones()
    {
        // An operator who names their own proxies means it — the published
        // Cloudflare list is the fallback, not an addition.
        var list = CloudflareRanges.TrustList(["203.0.113.0/24"], isProduction: true);

        Contains(list, "203.0.113.7").Should().BeTrue();
        Contains(list, "104.16.0.1").Should().BeFalse();
        Contains(list, "127.0.0.1").Should().BeTrue("the tunnel is still local");
    }

    [Fact]
    public void A_malformed_range_does_not_empty_the_list()
    {
        // One bad line in configuration must not silently turn the check off
        // and hand the header to anybody.
        var list = CloudflareRanges.TrustList(["not-a-cidr", "203.0.113.0/24"], isProduction: false);

        Contains(list, "203.0.113.7").Should().BeTrue();
        Contains(list, "127.0.0.1").Should().BeTrue();
        Contains(list, "8.8.8.8").Should().BeFalse();
    }
}
