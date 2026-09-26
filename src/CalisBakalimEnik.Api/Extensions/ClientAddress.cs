namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// Who the request actually came from.
/// </summary>
/// <remarks>
/// <para><b>This reads the connection, never the header.</b> By the time any
/// handler runs, <c>UseForwardedHeaders</c> has already replaced
/// <see cref="ConnectionInfo.RemoteIpAddress"/> with the value from
/// <c>CF-Connecting-IP</c> — but <i>only</i> when the immediate peer is one of
/// the networks configured in <c>Cloudflare:TrustedNetworks</c>. That check is
/// the entire security of the thing.</para>
///
/// <para>Five copies of this used to read the header directly instead:
/// <c>Headers["CF-Connecting-IP"] ?? RemoteIpAddress</c>, in the rate limiter,
/// the brute-force partition, the security-event writer, the admin audit and
/// the request log. The trust list was configured and then bypassed, so
/// <b>any caller could pick their own IP</b> by setting one header. That
/// defeats the per-IP login limit (a fresh bucket per request), the
/// brute-force lockout, and it lets an attacker write whatever address they
/// like into the audit trail — the one record whose job is to say where
/// something came from.</para>
///
/// <para>It survived review because the fallback made it look careful and
/// because behind a real tunnel the header is nearly always genuine. Only
/// testing it as an outsider shows the hole.</para>
///
/// <para><b>Loopback has to be trusted for this deployment.</b>
/// <c>cloudflared</c> runs on the same host and connects to Kestrel over
/// <c>127.0.0.1</c>, which is not in Cloudflare's published ranges. Without
/// loopback in the trust list the header is ignored and every user on earth
/// shares one bucket — which is the same outage the header was added to
/// prevent, arrived at from the other direction. See <c>Program.cs</c>.</para>
/// </remarks>
public static class ClientAddress
{
    /// <summary>The caller's address, or null when there is none to report.</summary>
    public static string? Of(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString();

    /// <summary>For log fields, which want a value rather than a null.</summary>
    public static string OrUnknown(HttpContext http) => Of(http) ?? "unknown";
}
