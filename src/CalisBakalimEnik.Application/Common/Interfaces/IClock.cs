namespace CalisBakalimEnik.Application.Common.Interfaces;

/// <summary>Never call DateTime.Now. Everything is UTC and comes from here.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
