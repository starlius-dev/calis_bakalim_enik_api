namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Keeping monthly partitions ahead of the data — docs/DATABASE.md §6.
/// </summary>
public sealed class PartitionOptions
{
    public const string SectionName = "Partitions";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many months beyond the current one to keep created.
    /// </summary>
    /// <remarks>
    /// Six because this is the only thing standing between a partitioned table
    /// and its default partition: a deployment that does not run for six months
    /// is a dead project, not an outage. Creating them is cheap — an empty
    /// partition is an empty file — so the number is chosen for how long the
    /// job may be broken before anyone notices, not for storage.
    /// </remarks>
    public int MonthsAhead { get; set; } = 6;

    /// <summary>
    /// The tables that are <c>PARTITION BY RANGE</c> on a month.
    /// </summary>
    /// <remarks>
    /// Passed to the database as parameters, and the functions on the other end
    /// keep their own allowlist — so this list decides what gets ASKED about,
    /// not what is permitted. Adding a table here without partitioning it and
    /// adding it to those functions gets an exception, which is the right way
    /// round.
    /// </remarks>
    public static readonly string[] Partitioned = ["security_events"];

    // The partition NAMES are not built here. They are built by the SECURITY
    // DEFINER functions the migration installs, because those are the only
    // things allowed to create or drop one — and a naming convention with two
    // authors is a naming convention that eventually disagrees with itself.
}
