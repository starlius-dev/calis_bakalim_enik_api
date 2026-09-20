namespace CalisBakalimEnik.Application.Common.Interfaces;

/// <summary>
/// Behind an interface so the provider is swappable (Netgsm / Twilio) and so
/// tests never send a real message — SMS costs money per send.
/// </summary>
public interface ISmsSender
{
    Task SendAsync(string phoneNumber, string message, CancellationToken ct = default);
}
