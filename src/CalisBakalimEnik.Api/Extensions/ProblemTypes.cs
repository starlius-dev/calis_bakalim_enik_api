namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// The error vocabulary — the <c>type</c> URIs in every problem+json document.
/// </summary>
/// <remarks>
/// One table because there are three writers: the exception middleware, the
/// rate-limit middleware, and <see cref="ProblemDetailsFilter"/> for the
/// documents endpoints return by hand. Three copies of this mapping would drift,
/// and the <c>type</c> URI is the part a client is supposed to branch on — a
/// value that means one thing on one route and another elsewhere is worse than
/// no value at all.
///
/// These are identifiers, not URLs to fetch. They do not have to resolve.
/// </remarks>
public static class ProblemTypes
{
    public const string Base = "https://calisbakalimenik.app/errors/";

    public const string Validation = Base + "validation";
    public const string Unauthorized = Base + "unauthorized";
    public const string Forbidden = Base + "forbidden";
    public const string NotFound = Base + "not-found";
    public const string Conflict = Base + "conflict";
    public const string RateLimited = Base + "rate-limited";
    public const string Cancelled = Base + "cancelled";
    public const string Internal = Base + "internal";
    public const string ClientTooOld = Base + "client-too-old";

    /// <summary>
    /// The type for a status code, for documents built from a status rather
    /// than from an exception.
    /// </summary>
    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => Validation,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status429TooManyRequests => RateLimited,
        StatusCodes.Status499ClientClosedRequest => Cancelled,
        _ => Internal,
    };
}
