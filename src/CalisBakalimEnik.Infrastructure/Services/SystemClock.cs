using CalisBakalimEnik.Application.Common.Interfaces;

namespace CalisBakalimEnik.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
