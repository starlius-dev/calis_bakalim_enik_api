using System.Net;

// Disambiguates from System.Net.IPNetwork, which ForwardedHeaders will not accept.
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// Cloudflare's published edge ranges (https://www.cloudflare.com/ips/).
///
/// These are the ONLY networks allowed to set CF-Connecting-IP. With an empty
/// trust list any caller could spoof the header, reset another user's rate-limit
/// bucket, or frame them for a lockout. See docs/SECURITY.md §1.
///
/// Embedded rather than fetched at startup: a network hiccup must not silently
/// leave the list empty, and the ranges change rarely. Refresh them deliberately
/// and note the date below.
/// </summary>
public static class CloudflareRanges
{
    /// <summary>Last refreshed 2026-09-20.</summary>
    public static readonly string[] Published =
    [
        // IPv4
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        // IPv6
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32",
        "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
    ];

    /// <summary>
    /// Parses a CIDR list into HttpOverrides' IPNetwork — deliberately NOT
    /// System.Net.IPNetwork, which is a different type that ForwardedHeaders
    /// will not accept. Entries the runtime cannot parse are
    /// skipped rather than throwing — one malformed line must not take the API
    /// down, and the count is reported so a silent truncation is noticeable.
    /// </summary>
    public static List<IPNetwork> Parse(IEnumerable<string> cidrs)
    {
        var networks = new List<IPNetwork>();

        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) continue;
            if (!IPAddress.TryParse(parts[0], out var prefix)) continue;
            if (!int.TryParse(parts[1], out var length)) continue;

            networks.Add(new IPNetwork(prefix, length));
        }

        return networks;
    }

    /// <summary>
    /// The networks allowed to set <c>CF-Connecting-IP</c> for this deployment.
    /// </summary>
    /// <remarks>
    /// <para><b>Never empty, and that is the whole point.</b>
    /// <c>ForwardedHeadersMiddleware</c> only checks who sent the header when
    /// <c>KnownNetworks</c> or <c>KnownProxies</c> has something in it — with
    /// both empty it skips the check and accepts the header from <i>any</i>
    /// caller. Program.cs asserted the opposite for two phases ("leaves the
    /// trust list EMPTY — ForwardedHeaders then ignores the header entirely,
    /// which is the safe failure mode"), and the list really was empty outside
    /// Production. It was the unsafe failure mode, stated as the safe one.</para>
    ///
    /// <para><b>Loopback is always trusted.</b> <c>cloudflared</c> runs on this
    /// host and reaches Kestrel over <c>127.0.0.1</c>, which is not in
    /// Cloudflare's published ranges. Without it the tunnel's header is ignored
    /// on every real request, every user collapses into the <c>127.0.0.1</c>
    /// rate-limit bucket, and one person signing in locks out the rest — the
    /// same outage the header exists to prevent, reached from the other side.
    /// Kestrel binds loopback only, so the peer is the tunnel or something
    /// already inside the machine.</para>
    /// </remarks>
    public static List<IPNetwork> TrustList(IEnumerable<string> configured, bool isProduction)
    {
        var explicitRanges = Parse(configured);

        var trusted = explicitRanges.Count > 0
            ? explicitRanges
            : isProduction ? Parse(Published) : [];

        trusted.Add(new IPNetwork(IPAddress.Loopback, 32));
        trusted.Add(new IPNetwork(IPAddress.IPv6Loopback, 128));

        return trusted;
    }
}
