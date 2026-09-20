using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Services;

/// <summary>
/// Development stand-in until SMTP is configured. It logs the SUBJECT and
/// recipient but never the body, because confirmation and reset bodies carry
/// single-use tokens and logs are not a secret store.
///
/// The body is appended to a local outbox file instead, so a developer can
/// complete the flow. Production replaces this with a real SMTP sender; startup
/// refuses to use it outside Development.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    private const string OutboxPath = "mail-outbox.log";

    public async Task SendAsync(
        string to, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("Email queued. To={To} Subject={Subject}", to, subject);

        var entry =
            $"--- {DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
            $"To: {to}{Environment.NewLine}" +
            $"Subject: {subject}{Environment.NewLine}" +
            $"{body}{Environment.NewLine}{Environment.NewLine}";

        await File.AppendAllTextAsync(OutboxPath, entry, ct);
    }
}
