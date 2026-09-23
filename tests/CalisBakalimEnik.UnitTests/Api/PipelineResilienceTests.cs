using System.Security.Claims;
using System.Text.Json;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Application.Common.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// Two ways the request pipeline used to answer 500 for something that was not
/// a server fault, both found by running the acceptance tests against a live
/// instance rather than by any test in this project.
/// </summary>
public class PipelineResilienceTests
{
    // ── the revocation cache ─────────────────────────────────────────

    /// <summary>
    /// An <see cref="ITokenService"/> whose cache is down. Only the two
    /// revocation lookups are reachable from the middleware; the rest exist to
    /// satisfy the interface and throw if anything ever calls them.
    /// </summary>
    private sealed class UnreachableCache : ITokenService
    {
        public string CreateAccessToken(
            AccessTokenSubject subject, out DateTimeOffset expiresAt, out Guid jti) =>
            throw new NotSupportedException();

        public string CreateRefreshToken() => throw new NotSupportedException();
        public string HashRefreshToken(string token) => throw new NotSupportedException();

        public Task DenylistAsync(Guid jti, DateTimeOffset expiresAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> IsDenylistedAsync(Guid jti, CancellationToken ct = default) =>
            throw new TimeoutException("The message timed out in the backlog.");

        public Task RevokeIssuedBeforeAsync(
            Guid userId, DateTimeOffset cutoff, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DateTimeOffset?> IssuedBeforeCutoffAsync(
            Guid userId, CancellationToken ct = default) =>
            throw new TimeoutException("The message timed out in the backlog.");
    }

    private static HttpContext SignedIn()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("nbf", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
        ], authenticationType: "Test"));

        return context;
    }

    [Fact]
    public async Task A_cache_outage_lets_the_request_through_instead_of_failing_it()
    {
        // The whole point. A dropped Redis connection used to surface as a 500
        // on EVERY authenticated route — a cache blip became a total outage.
        // Redis is not the system of record here: the refresh token is in
        // PostgreSQL and is still revoked, so the exposure is bounded by the
        // access token's own lifetime.
        var reached = false;
        var middleware = new JwtDenylistMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        var context = SignedIn();

        await middleware.InvokeAsync(
            context, new UnreachableCache(), NullLogger<JwtDenylistMiddleware>.Instance);

        reached.Should().BeTrue("a cache outage must not be an outage");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task An_anonymous_request_never_touches_the_cache_at_all()
    {
        var reached = false;
        var middleware = new JwtDenylistMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        // No identity: the lookups are skipped, so the throwing fake is never
        // asked anything.
        await middleware.InvokeAsync(
            new DefaultHttpContext(), new UnreachableCache(),
            NullLogger<JwtDenylistMiddleware>.Instance);

        reached.Should().BeTrue();
    }

    // ── an unreadable request body ───────────────────────────────────

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static async Task<(int Status, JsonElement Body)> ThrowAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tasks";
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new Env());

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();

        return (context.Response.StatusCode, JsonDocument.Parse(json).RootElement);
    }

    [Fact]
    public async Task A_body_that_cannot_be_bound_is_the_callers_mistake()
    {
        // Missing body, empty body, malformed JSON and a JSON array where an
        // object was expected all arrive as this one exception, raised while
        // binding and before any handler runs. Every one of them answered 500.
        var (status, body) = await ThrowAsync(
            new BadHttpRequestException(
                "Implicit body inferred for parameter but no body was provided."));

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("type").GetString()
            .Should().Be("https://calisbakalimenik.app/errors/validation");
    }

    [Fact]
    public async Task It_keeps_a_status_the_exception_chose_for_itself()
    {
        // A body over the configured size limit is a 413, not a 400. Flattening
        // every BadHttpRequestException to 400 would tell a caller to fix their
        // JSON when the problem is that they sent too much of it.
        var (status, _) = await ThrowAsync(
            new BadHttpRequestException("Request body too large.",
                StatusCodes.Status413PayloadTooLarge));

        status.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task A_genuine_fault_is_still_a_500_and_still_says_nothing()
    {
        var (status, body) = await ThrowAsync(new InvalidOperationException("boom"));

        status.Should().Be(StatusCodes.Status500InternalServerError);
        body.TryGetProperty("exception", out _)
            .Should().BeFalse("a production environment never sees the real message");
        body.GetProperty("detail").GetString().Should().NotContain("boom");
    }
}
