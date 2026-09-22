using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Identity;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Identity;

/// <summary>
/// Endpoint rate limits — docs/SECURITY.md §5, layer 3.
/// </summary>
public class RateLimitGuardTests
{
    private static readonly RateLimitPolicy Policy =
        new("test", 3, TimeSpan.FromMinutes(1));

    /// <summary>A counter with a TTL, which is all the guard asks Redis for.</summary>
    private sealed class FakeCache : ICacheStore
    {
        private readonly Dictionary<string, long> _counts = [];
        public bool Throws { get; set; }
        public int Increments { get; private set; }

        public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
        {
            if (Throws) throw new InvalidOperationException("redis is down");

            Increments++;
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            return Task.FromResult(_counts[key]);
        }

        public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(17));

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task RemoveAsync(string key, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Requests_up_to_the_limit_are_allowed()
    {
        var guard = new RateLimitGuard(new FakeCache());

        for (var i = 1; i <= 3; i++)
        {
            var verdict = await guard.CheckAsync(Policy, "1.2.3.4");
            verdict.Allowed.Should().BeTrue($"request {i} is within the budget");
        }
    }

    [Fact]
    public async Task The_request_after_the_limit_is_refused_with_a_retry_after()
    {
        var guard = new RateLimitGuard(new FakeCache());

        for (var i = 0; i < 3; i++) await guard.CheckAsync(Policy, "1.2.3.4");

        var verdict = await guard.CheckAsync(Policy, "1.2.3.4");

        verdict.Allowed.Should().BeFalse();
        // The REMAINING window, not the whole of it: a client told to wait a
        // full minute after 43 seconds have passed backs off for nothing.
        verdict.RetryAfter.Should().Be(TimeSpan.FromSeconds(17));
    }

    [Fact]
    public async Task Partitions_do_not_share_a_budget()
    {
        var guard = new RateLimitGuard(new FakeCache());

        for (var i = 0; i < 3; i++) await guard.CheckAsync(Policy, "1.2.3.4");

        // One address exhausting its budget must not lock out everyone else.
        var other = await guard.CheckAsync(Policy, "5.6.7.8");
        other.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Remaining_never_goes_negative()
    {
        var guard = new RateLimitGuard(new FakeCache());

        for (var i = 0; i < 10; i++) await guard.CheckAsync(Policy, "1.2.3.4");

        var verdict = await guard.CheckAsync(Policy, "1.2.3.4");
        verdict.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task A_null_partition_is_not_one_shared_bucket()
    {
        var cache = new FakeCache();
        var guard = new RateLimitGuard(cache);

        // Every anonymous caller would otherwise count against one counter and
        // be rate-limited as though they were a single client.
        for (var i = 0; i < 10; i++)
        {
            var verdict = await guard.CheckAsync(Policy, null);
            verdict.Allowed.Should().BeTrue();
        }

        cache.Increments.Should().Be(0);
    }

    [Fact]
    public async Task It_fails_OPEN_when_the_cache_is_unreachable()
    {
        var guard = new RateLimitGuard(new FakeCache { Throws = true });

        // Deliberate: these limits are abuse control, not authorisation. The
        // real account protection is BruteForceGuard and the auth checks, and
        // failing closed would turn a cache outage into a total outage.
        var verdict = await guard.CheckAsync(Policy, "1.2.3.4");
        verdict.Allowed.Should().BeTrue();
    }

    [Fact]
    public void The_shipped_policies_match_the_security_doc()
    {
        RateLimitGuard.Policies.Login.Limit.Should().Be(10);
        RateLimitGuard.Policies.Login.Window.Should().Be(TimeSpan.FromMinutes(1));

        RateLimitGuard.Policies.Refresh.Limit.Should().Be(30);
        RateLimitGuard.Policies.Register.Limit.Should().Be(3);
        RateLimitGuard.Policies.Register.Window.Should().Be(TimeSpan.FromHours(1));
        RateLimitGuard.Policies.ForgotPassword.Limit.Should().Be(3);

        RateLimitGuard.Policies.Authenticated.Limit.Should().Be(300);
        // Per user, not per IP: several people on one office NAT share an
        // address, and an IP budget would let one exhaust everyone else's.
        RateLimitGuard.Policies.Authenticated.PerUser.Should().BeTrue();
        RateLimitGuard.Policies.Login.PerUser.Should().BeFalse();
    }
}
