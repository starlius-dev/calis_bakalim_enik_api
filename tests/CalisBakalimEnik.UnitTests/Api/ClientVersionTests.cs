using CalisBakalimEnik.Api.Middleware;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// The version floor behind the 426 update gate.
/// </summary>
/// <remarks>
/// Every case here decides whether a real person can use the app they already
/// have installed, so the bias throughout is toward SERVING: an unreadable
/// version is served, and the gate does nothing at all until someone sets a
/// minimum on purpose.
/// </remarks>
public class ClientVersionTests
{
    [Theory]
    [InlineData("1.4.2", 1, 4, 2)]
    // pubspec's own format. The build number moves on every build and says
    // nothing about compatibility, so it is dropped rather than compared.
    [InlineData("1.4.2+318", 1, 4, 2)]
    [InlineData("2.0.0-beta.3", 2, 0, 0)]
    [InlineData("  1.4.2+7  ", 1, 4, 2)]
    // A two-part version is a real thing people write; treat the patch as zero
    // rather than refusing to read it.
    [InlineData("1.4", 1, 4, 0)]
    public void A_version_is_read_down_to_major_minor_patch(
        string header, int major, int minor, int patch)
    {
        ClientVersionMiddleware.Parse(header)
            .Should().Be(new Version(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sürüm yok")]
    [InlineData("v1.4.2")]        // the leading v is not a number
    [InlineData("1")]             // Version needs at least two components
    [InlineData("+318")]
    public void An_unreadable_version_is_no_version_at_all(string? header)
    {
        // And a null here means SERVED, not refused — curl, a monitoring probe
        // and an integration are not stale phones, and a typo in a version
        // string must not lock out everyone running that build.
        ClientVersionMiddleware.Parse(header).Should().BeNull();
    }

    [Fact]
    public void Ordering_is_numeric_not_lexicographic()
    {
        // "1.10.0" sorts BEFORE "1.9.0" as text. A floor that compared strings
        // would start refusing the newest clients the moment a minor version
        // reached double digits.
        var ten = ClientVersionMiddleware.Parse("1.10.0")!;
        var nine = ClientVersionMiddleware.Parse("1.9.0")!;

        ten.Should().BeGreaterThan(nine);
    }

    [Fact]
    public void A_build_number_never_decides_it()
    {
        // Same release, different builds: neither is older than the other, so a
        // floor of 1.4.2 admits both.
        var low = ClientVersionMiddleware.Parse("1.4.2+1")!;
        var high = ClientVersionMiddleware.Parse("1.4.2+9999")!;

        low.Should().Be(high);
        low.Should().BeGreaterThanOrEqualTo(ClientVersionMiddleware.Parse("1.4.2")!);
    }
}
