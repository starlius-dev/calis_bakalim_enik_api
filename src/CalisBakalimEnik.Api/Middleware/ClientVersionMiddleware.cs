using System.Text.Json;
using CalisBakalimEnik.Api.Extensions;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Api.Middleware;

/// <summary>
/// The minimum client version the API will serve, and where to get a newer one.
/// </summary>
/// <remarks>
/// A class with settable properties rather than a positional record, because
/// <c>IOptions&lt;T&gt;</c> constructs it with <c>Activator.CreateInstance</c>
/// and needs a genuine parameterless constructor. A record whose primary
/// constructor has all-optional parameters does not have one, and the API
/// refuses to start.
/// </remarks>
public sealed class ClientOptions
{
    public const string SectionName = "Client";

    /// <summary>
    /// <c>major.minor.patch</c>. <b>Empty means no gate</b>, which is the
    /// default: a version floor that switches itself on is a way to lock every
    /// user out of a working app on a routine deploy. It has to be set
    /// deliberately, by someone who has decided an old client genuinely cannot
    /// be served.
    /// </summary>
    public string? MinimumVersion { get; set; }

    /// <summary>
    /// Where the update screen sends people. Optional — the screen still works
    /// without it, it just cannot offer the button.
    /// </summary>
    public string? StoreUrl { get; set; }
}

/// <summary>
/// Refuses a client older than <see cref="ClientOptions.MinimumVersion"/> with
/// <b>426 Upgrade Required</b>.
/// </summary>
/// <remarks>
/// <para>The Flutter client has been ready for this since it was written: an
/// interceptor watches for 426, reads <c>minimumVersion</c> and
/// <c>storeUrl</c> out of the body, and routes to a blocking update screen at
/// <c>/guncelle</c>. Nothing ever sent it. The header was read in one place —
/// a Serilog enrichment property — and compared against nothing, so the whole
/// path was unreachable code on one side of the wire and a missing feature on
/// the other.</para>
///
/// <para><b>An unknown version is served, not refused.</b> Anything without a
/// readable <c>X-Client-Version</c> — curl, a monitoring probe, a health check,
/// an integration — is not a stale app and blocking it would take out
/// everything that is not the phone. The gate exists to stop a client that
/// identifies itself as too old, and that is all it does.</para>
///
/// <para>Placed before authentication so an out-of-date client is told to
/// update rather than told its token is bad. It sits after the correlation id
/// so the refusal carries one like every other failure.</para>
/// </remarks>
public sealed class ClientVersionMiddleware(RequestDelegate next, IOptions<ClientOptions> options)
{
    private readonly Version? _minimum = Parse(options.Value.MinimumVersion);
    private readonly string? _storeUrl = options.Value.StoreUrl;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_minimum is null || !IsTooOld(context.Request.Headers["X-Client-Version"]))
        {
            await next(context);
            return;
        }

        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName] as string;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.ContentType = "application/problem+json";

        // minimumVersion and storeUrl sit at the TOP level, which is where the
        // client's interceptor reads them. The rest is the ordinary problem
        // document so this failure looks like every other one.
        await context.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = ProblemTypes.ClientTooOld,
            ["title"] = "Güncelleme gerekli",
            ["status"] = StatusCodes.Status426UpgradeRequired,
            ["detail"] = "Bu sürüm artık desteklenmiyor. Uygulamayı güncelle.",
            ["instance"] = context.Request.Path.Value,
            ["correlationId"] = correlationId,
            ["minimumVersion"] = _minimum.ToString(3),
            ["storeUrl"] = _storeUrl,
        }));
    }

    private bool IsTooOld(string? header) =>
        Parse(header) is { } client && client < _minimum;

    /// <summary>
    /// The <c>major.minor.patch</c> out of a version string, ignoring anything
    /// after a <c>+</c> or <c>-</c>.
    /// </summary>
    /// <remarks>
    /// The client sends pubspec's format, <c>1.4.2+318</c>, where the build
    /// number after the <c>+</c> moves on every build and says nothing about
    /// compatibility. An unreadable value returns null and is then SERVED — see
    /// the class remarks. Failing closed here would mean a typo in a version
    /// string locking out every user who has that build.
    /// </remarks>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var core = value.Split('+', '-')[0].Trim();

        return Version.TryParse(core, out var parsed) && parsed.Major >= 0
            ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0))
            : null;
    }
}
