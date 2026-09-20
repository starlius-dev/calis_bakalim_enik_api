using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Services;

/// <summary>
/// Development stand-in. Logs that a message was queued and writes the body to a
/// local outbox — never to the log, because the body is a one-time code.
/// </summary>
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger) : ISmsSender
{
    private const string OutboxPath = "sms-outbox.log";

    public async Task SendAsync(
        string phoneNumber, string message, CancellationToken ct = default)
    {
        var masked = phoneNumber.Length < 4
            ? "***"
            : new string('*', phoneNumber.Length - 4) + phoneNumber[^4..];

        logger.LogInformation("SMS queued. To={MaskedNumber}", masked);

        var entry =
            $"--- {DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
            $"To: {phoneNumber}{Environment.NewLine}" +
            $"{message}{Environment.NewLine}{Environment.NewLine}";

        await File.AppendAllTextAsync(OutboxPath, entry, ct);
    }
}
