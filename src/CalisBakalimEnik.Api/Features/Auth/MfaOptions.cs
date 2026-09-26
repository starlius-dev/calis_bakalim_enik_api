using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Api.Features.Auth;

/// <summary>
/// Whether the second factor exists at all in this deployment.
/// </summary>
/// <remarks>
/// <para><b>Defaults to on.</b> A security control that switches itself off when
/// a setting is missing or misspelled is not a control. Turning it off has to be
/// something somebody wrote down.</para>
///
/// <para>Off means two things together, and they have to move together: login
/// never issues a challenge, and the whole <c>/auth/mfa</c> surface answers 404.
/// Doing only the first would leave an account able to enrol a factor that is
/// then never asked for — a user who believes they are protected and is not,
/// which is worse than having no second factor at all.</para>
///
/// <para>An account that already enrolled keeps its factor in the database. It
/// is skipped, not deleted, so turning the flag back on restores it rather than
/// locking that person out of an authenticator they still have.</para>
///
/// <para><b>This is a real reduction in security.</b> It is reasonable for an
/// internal test team who would otherwise spend the first hour scanning QR
/// codes, and it is not reasonable for a deployment with customers in it. The
/// client carries a matching flag so the account screen does not offer a
/// feature the server refuses, and <c>GET /api/version</c> reports the server's
/// value so a mismatch between the two is visible rather than mysterious.</para>
/// </remarks>
public sealed class MfaOptions
{
    public const string SectionName = "Mfa";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Answers 404 for the whole MFA surface when the feature is off.
/// </summary>
/// <remarks>
/// 404 rather than 403: a disabled feature has no endpoint, and 403 would say
/// "this exists and you may not use it", which is a different and wrong claim.
/// </remarks>
public sealed class MfaEnabledFilter(IOptions<MfaOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.Value.Enabled
            ? next(context)
            : ValueTask.FromResult<object?>(Results.NotFound());
}
