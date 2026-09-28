using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Identity;

/// <summary>
/// Step-up tokens and the pending-deletion lock (D15, D16).
/// </summary>
public class AccountDataTests
{
    private sealed class MemoryCache : ICacheStore
    {
        public readonly Dictionary<string, string> Values = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Values.ContainsKey(key));

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<TimeSpan?>(null);
    }

    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    // ── step-up tokens ───────────────────────────────────────────────────

    [Fact]
    public async Task A_token_works_once()
    {
        var tokens = new StepUpTokens(new MemoryCache());
        var token = await tokens.IssueAsync(Alice, StepUpTokens.Export, default);

        (await tokens.ConsumeAsync(token, Alice, StepUpTokens.Export, default)).Should().BeTrue();
        (await tokens.ConsumeAsync(token, Alice, StepUpTokens.Export, default)).Should().BeFalse();
    }

    [Fact]
    public async Task An_export_step_up_cannot_delete_the_account()
    {
        var tokens = new StepUpTokens(new MemoryCache());
        var token = await tokens.IssueAsync(Alice, StepUpTokens.Export, default);

        (await tokens.ConsumeAsync(token, Alice, StepUpTokens.Delete, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_token_is_only_good_for_its_own_user()
    {
        var tokens = new StepUpTokens(new MemoryCache());
        var token = await tokens.IssueAsync(Alice, StepUpTokens.Delete, default);

        (await tokens.ConsumeAsync(token, Bob, StepUpTokens.Delete, default)).Should().BeFalse();
        // Burnt by the wrong attempt, so it cannot then be tried again.
        (await tokens.ConsumeAsync(token, Alice, StepUpTokens.Delete, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("made-up")]
    public async Task Missing_or_unknown_tokens_are_refused(string? token)
    {
        var tokens = new StepUpTokens(new MemoryCache());
        (await tokens.ConsumeAsync(token, Alice, StepUpTokens.Export, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_hash_is_stored()
    {
        var cache = new MemoryCache();
        var token = await new StepUpTokens(cache).IssueAsync(Alice, StepUpTokens.Export, default);

        cache.Values.Keys.Should().ContainSingle().Which.Should().NotContain(token);
    }

    [Theory]
    [InlineData("export", true)]
    [InlineData("delete", true)]
    [InlineData("admin", false)]
    [InlineData(null, false)]
    public void Only_named_purposes_exist(string? purpose, bool valid) =>
        StepUpTokens.IsPurpose(purpose).Should().Be(valid);

    // ── pending deletion ─────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("POST", "/api/v1/auth/logout")]
    [InlineData("POST", "/api/v1/auth/refresh")]
    [InlineData("DELETE", "/api/v1/account/deletion")]
    [InlineData("POST", "/api/v1/account/step-up")]
    [InlineData("POST", "/api/v1/account/export")]
    [InlineData("POST", "/api/v1/auth/mfa/select")]
    [InlineData("GET", "/api/version")]
    [InlineData("GET", "/api/health/ready")]
    public void A_pending_account_can_still_cancel_export_and_leave(string method, string path) =>
        DeletionPendingMiddleware.IsAllowed(method, path).Should().BeTrue();

    [Theory]
    [InlineData("GET", "/api/v1/tasks")]
    [InlineData("GET", "/api/v1/medications")]
    [InlineData("PATCH", "/api/v1/auth/me")]
    [InlineData("GET", "/api/v1/notifications")]
    [InlineData("POST", "/api/v1/auth/mfa/totp/enrol")]
    [InlineData("GET", "/api/v1/admin/users")]
    public void Everything_else_is_locked(string method, string path) =>
        DeletionPendingMiddleware.IsAllowed(method, path).Should().BeFalse();

    // ── retention ────────────────────────────────────────────────────────

    [Fact]
    public void Erased_accounts_events_never_drop_whole_partitions()
    {
        var rule = RetentionCleanup.Rules(new RetentionOptions())
            .Single(r => r.Name == "security events of erased accounts");

        // Those months hold everyone else's events too.
        rule.DropPartitions.Should().BeFalse();
        rule.Days.Should().Be(183);
        rule.Predicate.Should().Contain("erasedAt");
    }

    [Fact]
    public void The_grace_period_is_seven_days() =>
        new AccountDeletionOptions().GraceDays.Should().Be(7);
}
