using CalisBakalimEnik.Application.Common.Interfaces;

namespace CalisBakalimEnik.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    /// <summary>
    /// Now, truncated to the precision the database actually keeps.
    /// </summary>
    /// <remarks>
    /// <c>DateTimeOffset</c> counts 100-nanosecond ticks; PostgreSQL's
    /// <c>timestamptz</c> stops at microseconds. Without this, a value written
    /// and echoed in the same response carries a digit the stored row does not
    /// — a client that keeps the returned timestamp and later compares it to a
    /// re-fetched one finds them unequal, for a difference that never existed
    /// anywhere but in memory.
    ///
    /// Observed on the focus-session end endpoint, which is idempotent and
    /// still appeared to return a different instant on the second call.
    /// </remarks>
    public DateTimeOffset UtcNow
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return now.AddTicks(-(now.Ticks % TicksPerMicrosecond));
        }
    }

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
}
