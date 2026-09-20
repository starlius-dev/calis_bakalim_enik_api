using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace CalisBakalimEnik.Infrastructure.Services;

/// <summary>
/// The Redis instance is SHARED with other projects on the server, so every key
/// carries the `cbe:` prefix and the API uses its own logical database.
/// FLUSHDB and FLUSHALL are never issued — not even from test helpers.
/// </summary>
public sealed class RedisCacheStore : ICacheStore
{
    private readonly IDatabase _db;
    private readonly string _prefix;

    public RedisCacheStore(IConnectionMultiplexer connection, IConfiguration configuration)
    {
        var index = configuration.GetValue("Redis:Database", 1);
        _db = connection.GetDatabase(index);
        _prefix = configuration["Redis:KeyPrefix"] ?? "cbe:";
    }

    private string Key(string key) => _prefix + key;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        => await _db.StringGetAsync(Key(key));

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
        => _db.StringSetAsync(Key(key), value, ttl);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => _db.KeyExistsAsync(Key(key));

    public Task RemoveAsync(string key, CancellationToken ct = default)
        => _db.KeyDeleteAsync(Key(key));

    public async Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var full = Key(key);
        var value = await _db.StringIncrementAsync(full);

        // Only the first increment sets the expiry, so the window slides from the
        // first failure rather than being extended by every subsequent one.
        if (value == 1) await _db.KeyExpireAsync(full, ttl);

        return value;
    }

    public async Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default)
        => await _db.KeyTimeToLiveAsync(Key(key));
}
