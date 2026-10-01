using System.Globalization;
using CalisBakalimEnik.Application.Common.Localization;
using Microsoft.Net.Http.Headers;

namespace CalisBakalimEnik.Api.Middleware;

/// <summary>
/// Sets the request's language from Accept-Language (J80), for the messages
/// this API writes: problem titles and details, validation errors.
/// </summary>
/// <remarks>
/// Only the UI culture. ASP.NET's own request localization sets the
/// formatting culture too, and a Turkish CurrentCulture turns "i".ToUpper()
/// into "İ" in every string comparison, e-mail normalisation and key in the
/// code base. Formatting stays invariant; only the words change.
///
/// The first language in the header that the API speaks wins, by quality;
/// Turkish when none does or there is no header.
/// </remarks>
public sealed class LanguageMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Pick(context.Request.Headers.AcceptLanguage));
        return next(context);
    }

    public static string Pick(IEnumerable<string?> header)
    {
        if (!StringWithQualityHeaderValue.TryParseList(header.OfType<string>().ToList(), out var values))
            return Texts.Source;

        foreach (var value in values.OrderByDescending(v => v.Quality ?? 1))
        {
            var code = value.Value.Value;
            if (code is null) continue;
            var two = code.Length >= 2 ? code[..2].ToLowerInvariant() : code;
            if (Texts.Languages.Contains(two)) return two;
        }

        return Texts.Source;
    }
}
