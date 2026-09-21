using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Infrastructure.Services;

/// <summary>
/// Sends through Resend's HTTP API.
///
/// Callers hand over plain text, as they did to the development sender, and
/// this renders the HTML part. Keeping the interface text-only means the flows
/// never grow markup, and every message still has a text/plain alternative —
/// which is what a mail client without HTML, and most spam filters, look at.
///
/// <b>The body is never logged.</b> Confirmation and reset bodies carry
/// single-use tokens; only the recipient, the subject and Resend's message id
/// go to the log.
/// </summary>
public sealed partial class ResendEmailSender(
    HttpClient http,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    /// <summary>
    /// Escapes markup but leaves non-ASCII alone.
    /// </summary>
    /// <remarks>
    /// <c>HtmlEncoder.Default</c> turns every Turkish character into a numeric
    /// entity — "Doğrula" becomes "Do&amp;#x11F;rula" — which renders correctly
    /// and reads like mojibake in any client that shows the source, and inflates
    /// a Turkish mail considerably. The document declares UTF-8, so the only
    /// characters that need escaping are the markup ones.
    /// </remarks>
    private static readonly HtmlEncoder Encoder =
        HtmlEncoder.Create(UnicodeRanges.All);

    public async Task SendAsync(
        string to, string subject, string body, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["from"] = _options.From,
            ["to"] = new[] { to },
            ["subject"] = subject,
            ["text"] = body,
            ["html"] = RenderHtml(subject, body),
        };

        if (!string.IsNullOrWhiteSpace(_options.ReplyTo))
            payload["reply_to"] = _options.ReplyTo;

        using var response = await http.PostAsJsonAsync("emails", payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Resend explains itself in the body — an unverified sending domain
            // and an expired key look identical without it. It contains no
            // secret: the key travels in the request header, not the response.
            var detail = await response.Content.ReadAsStringAsync(ct);

            logger.LogError(
                "Resend refused the message. Status={Status} To={To} Subject={Subject} Detail={Detail}",
                (int)response.StatusCode, to, subject, detail);

            throw new EmailDeliveryException(
                $"Resend returned {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<ResendResult>(ct);

        logger.LogInformation(
            "Email sent. To={To} Subject={Subject} MessageId={MessageId}",
            to, subject, result?.Id);
    }

    /// <summary>
    /// Wraps the plain-text body in a minimal HTML document.
    /// </summary>
    /// <remarks>
    /// Everything is escaped first and only then are URLs linked, so a subject
    /// or a display name can never inject markup into the mail.
    /// </remarks>
    internal static string RenderHtml(string subject, string body)
    {
        var paragraphs = new StringBuilder();

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            paragraphs
                .Append("<p style=\"margin:0 0 16px;font-size:15px;line-height:1.6;color:#1f1b2b\">")
                .Append(LinkUrls(Encoder.Encode(trimmed)))
                .Append("</p>");
        }

        return $$"""
            <!doctype html>
            <html lang="tr"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{Encoder.Encode(subject)}}</title></head>
            <body style="margin:0;padding:24px;background:#f4f2f7">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
              style="max-width:560px;margin:0 auto;background:#ffffff;border-radius:14px">
            <tr><td style="padding:28px 28px 8px">
            <p style="margin:0 0 20px;font-size:13px;letter-spacing:.14em;color:#6b5f86;
              font-family:ui-monospace,SFMono-Regular,Menlo,monospace">ÇALIŞ BAKALIM ENİK</p>
            </td></tr>
            <tr><td style="padding:0 28px 8px;font-family:-apple-system,Segoe UI,Roboto,sans-serif">
            {{paragraphs}}
            </td></tr>
            <tr><td style="padding:8px 28px 28px">
            <p style="margin:0;font-size:12px;line-height:1.6;color:#8a8298;
              font-family:-apple-system,Segoe UI,Roboto,sans-serif">
            Bu e-postayı sen istemediysen görmezden gelebilirsin.</p>
            </td></tr></table></body></html>
            """;
    }

    /// <summary>Turns an already-escaped URL into an anchor.</summary>
    private static string LinkUrls(string escaped) => UrlPattern().Replace(
        escaped,
        m => $"<a href=\"{m.Value}\" style=\"color:#5b3fb8;word-break:break-all\">{m.Value}</a>");

    // Runs over HTML-ENCODED text, so `&amp;` stands where `&` was: the class
    // has to allow the entity, or a link dies at its first query separator.
    [GeneratedRegex(@"https?://[^\s<""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private sealed record ResendResult(string? Id);
}

public sealed class EmailDeliveryException(string message) : Exception(message);
