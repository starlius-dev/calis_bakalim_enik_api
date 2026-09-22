using CalisBakalimEnik.Api.Extensions;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// Revoking an access token that belongs to somebody else — docs/SECURITY.md §4.
/// </summary>
/// <remarks>
/// The denylist can only revoke a token the caller is holding. Disabling an
/// account, demoting an admin and signing out every device all need to revoke
/// tokens nobody in the request is holding, and nothing records their jti — so
/// the decision is made by comparing each token's <c>nbf</c> against a cutoff
/// stored per user.
/// </remarks>
public class SessionCutoffTests
{
    private static readonly DateTimeOffset Cutoff =
        new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static string Nbf(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString();

    [Fact]
    public void With_no_cutoff_nothing_is_revoked()
    {
        // The overwhelmingly common case: one Redis miss and the request
        // carries on.
        JwtDenylistMiddleware.IsRevoked(null, Nbf(Cutoff.AddHours(-5)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_token_issued_before_the_cutoff_is_revoked()
    {
        JwtDenylistMiddleware.IsRevoked(Cutoff, Nbf(Cutoff.AddSeconds(-1)))
            .Should().BeTrue();
    }

    [Fact]
    public void A_token_issued_after_the_cutoff_survives()
    {
        // The user signing in again immediately after being signed out
        // everywhere must get a session that works.
        JwtDenylistMiddleware.IsRevoked(Cutoff, Nbf(Cutoff.AddSeconds(1)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_token_issued_in_the_SAME_SECOND_as_the_cutoff_is_revoked()
    {
        // This test asserted the opposite first, on the reasoning that a token
        // minted in the same second must be the replacement. It is not: `nbf`
        // has one-second resolution, so the common case is a token issued just
        // BEFORE the revocation and rounded onto the boundary — and letting
        // that one through keeps a revoked session alive for a full fifteen
        // minutes. A live test caught it; this one did not.
        JwtDenylistMiddleware.IsRevoked(Cutoff, Nbf(Cutoff))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("١٧٦٤٠٠٠٠٠٠")]   // Arabic-Indic digits: parseable under some cultures
    public void A_token_with_no_readable_nbf_is_revoked(string? notBefore)
    {
        // Fails CLOSED on purpose. Reading an unparseable nbf as "exempt"
        // would let a token opt out of revocation by omitting a claim, and the
        // culture-specific case is why the parse is pinned to the invariant
        // culture rather than the ambient one.
        JwtDenylistMiddleware.IsRevoked(Cutoff, notBefore).Should().BeTrue();
    }
}
