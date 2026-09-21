namespace CalisBakalimEnik.Application.Common.Interfaces;

/// <summary>
/// One push to one device token.
/// </summary>
/// <param name="Notification">
/// Present on purpose. iOS will not display a pure data message when the app is
/// terminated, and Android needs it for the tray in that state — a data-only
/// message is best-effort and throttled. See docs/NOTIFICATIONS.md §4.
/// </param>
/// <param name="Data">
/// Carries <c>route</c>: the server decides where a tap lands, the client just
/// navigates there.
/// </param>
public sealed record PushMessage(
    string Token,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    string AndroidChannelId = "reminders",
    int? Badge = null)
{
    public bool Notification => true;
}

/// <param name="TokenInvalid">
/// The provider says this token no longer exists. The caller clears it from the
/// device row — retrying it forever is what keeps a dead token alive in a
/// queue.
/// </param>
public sealed record PushResult(
    bool Succeeded,
    string? MessageId = null,
    bool TokenInvalid = false,
    string? Error = null);

public interface IPushSender
{
    Task<PushResult> SendAsync(PushMessage message, CancellationToken ct = default);
}
