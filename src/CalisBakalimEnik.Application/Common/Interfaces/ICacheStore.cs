namespace CalisBakalimEnik.Application.Common.Interfaces;

/// <summary>
/// Redis, behind a port. Nothing here is authoritative: a flush resets counters
/// and empties caches, and no user data is lost. See docs/DATABASE.md §10.
/// </summary>
public interface ICacheStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Increments and returns the new value, setting the TTL on first write.</summary>
    Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default);

    Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default);
}
