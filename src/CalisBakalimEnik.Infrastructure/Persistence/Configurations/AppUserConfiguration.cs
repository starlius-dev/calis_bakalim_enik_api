using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("users");

        builder.Property(u => u.DisplayName).HasMaxLength(80).IsRequired();
        builder.Property(u => u.AvatarUrl).HasMaxLength(500);
        builder.Property(u => u.Locale).HasMaxLength(10).IsRequired();
        builder.Property(u => u.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(u => u.Status).HasConversion<short>();

        // Identity's own global unique indexes on NormalizedUserName and
        // NormalizedEmail are KEPT. Without a tenant, email is a global identity
        // and one address must map to exactly one account. An earlier revision of
        // the design called for dropping them in favour of per-tenant partial
        // indexes; that is wrong here. See docs/DATABASE.md §3.

        // Identity's own EmailIndex is NOT unique — it enforces uniqueness only in
        // UserManager, so two concurrent registrations can race past it and create
        // two accounts on one address. Email is the global identity here, so the
        // database enforces it. Filtered on deleted_at so a purged account's
        // address becomes reusable.
        builder.HasIndex(u => u.NormalizedEmail)
            .HasDatabaseName("ux_users_normalized_email")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        builder.HasIndex(u => u.NormalizedUserName)
            .HasDatabaseName("ux_users_normalized_user_name")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        builder.HasIndex(u => u.Status);
        builder.HasQueryFilter(u => u.DeletedAt == null);
    }
}
