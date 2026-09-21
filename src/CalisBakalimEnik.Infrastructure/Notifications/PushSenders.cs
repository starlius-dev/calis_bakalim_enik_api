using CalisBakalimEnik.Application.Common.Interfaces;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Infrastructure.Notifications;

public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary><c>Fcm</c> sends; anything else logs.</summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>
    /// Path to the Firebase service-account JSON. Never committed; on the
    /// server it lives beside the app with 600 permissions.
    /// </summary>
    public string CredentialsPath { get; set; } = string.Empty;

    public bool UsesFcm =>
        string.Equals(Provider, "Fcm", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CredentialsPath)
        && File.Exists(CredentialsPath);
}

/// <summary>
/// Development stand-in. Logs that a push would have gone out, including the
/// route, so the deep-link contract can be checked without Firebase.
/// </summary>
public sealed class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public Task<PushResult> SendAsync(PushMessage message, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Push (not sent — no FCM configured). Title={Title} Route={Route} Token={Token}",
            message.Title,
            message.Data.TryGetValue("route", out var route) ? route : "-",
            Mask(message.Token));

        return Task.FromResult(new PushResult(true, MessageId: "logged"));
    }

    /// <summary>An FCM token is a credential for delivery; logs keep a stub.</summary>
    private static string Mask(string token) =>
        token.Length <= 12 ? "***" : $"{token[..6]}…{token[^4..]}";
}

/// <summary>
/// Sends through Firebase Cloud Messaging.
/// </summary>
/// <remarks>
/// Every message carries BOTH a notification block and a data payload: iOS will
/// not display a pure data message when the app is terminated, and Android
/// needs the block for the tray in that state. See docs/NOTIFICATIONS.md §4.
/// </remarks>
public sealed class FcmPushSender : IPushSender
{
    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FcmPushSender> _logger;

    public FcmPushSender(IOptions<PushOptions> options, ILogger<FcmPushSender> logger)
    {
        _logger = logger;

        // CredentialFactory rather than GoogleCredential.FromFile/FromStream,
        // both of which are deprecated. Naming the expected type is the point
        // of the replacement: a user credential or an impersonation config
        // dropped in by mistake is refused here rather than failing later with
        // a permission error that looks like a Firebase problem.
        var credential = CredentialFactory.FromFile(
            options.Value.CredentialsPath,
            JsonCredentialParameters.ServiceAccountCredentialType);

        // FirebaseApp is a process-wide singleton; creating a second one throws.
        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            Credential = credential,
        });

        _messaging = FirebaseMessaging.GetMessaging(app);
    }

    public async Task<PushResult> SendAsync(
        PushMessage message, CancellationToken ct = default)
    {
        // FirebaseAdmin 3.6 marks Token obsolete with "use Fid instead", but an
        // FID is a Firebase INSTALLATION id, not a registration token — a
        // different addressing mode. The client sends us the registration token
        // from getToken(), so Token is the correct field and switching would
        // silently stop delivering.
#pragma warning disable CS0618
        var payload = new Message
        {
            Token = message.Token,
#pragma warning restore CS0618
            Notification = new FirebaseAdmin.Messaging.Notification
            {
                Title = message.Title,
                Body = message.Body,
            },
            Data = new Dictionary<string, string>(message.Data),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    // Fixed at channel CREATION on the device: a channel made
                    // with the wrong importance needs a reinstall to correct.
                    ChannelId = message.AndroidChannelId,
                },
            },
            Apns = new ApnsConfig
            {
                Aps = new Aps
                {
                    Sound = "default",
                    Badge = message.Badge,
                    ContentAvailable = true,
                },
            },
            Webpush = message.Data.TryGetValue("route", out var route)
                ? new WebpushConfig
                {
                    FcmOptions = new WebpushFcmOptions
                    {
                        Link = $"https://calisbakalimenik.app/#{route}",
                    },
                }
                : null,
        };

        try
        {
            var id = await _messaging.SendAsync(payload, ct);
            return new PushResult(true, id);
        }
        catch (FirebaseMessagingException e)
            when (e.MessagingErrorCode is MessagingErrorCode.Unregistered
                      or MessagingErrorCode.InvalidArgument)
        {
            // The app was uninstalled, its data cleared, or the token was
            // rotated. The caller prunes it — this is the only signal that a
            // token is dead, and ignoring it means retrying it forever.
            _logger.LogInformation(
                "FCM rejected a token as {Code}", e.MessagingErrorCode);

            return new PushResult(false, TokenInvalid: true, Error: e.MessagingErrorCode.ToString());
        }
        catch (FirebaseMessagingException e)
        {
            // Transient: the outbox retries with backoff.
            _logger.LogWarning(e, "FCM send failed");
            return new PushResult(false, Error: e.Message);
        }
    }
}
