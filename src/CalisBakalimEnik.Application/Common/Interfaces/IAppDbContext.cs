namespace CalisBakalimEnik.Application.Common.Interfaces;

/// <summary>
/// The Application layer's view of persistence. DbSets are added per feature as
/// entities land in Phase 3 and Phase 6. Application deliberately does not
/// reference a database provider — only Infrastructure does.
/// </summary>
public interface IAppDbContext
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
