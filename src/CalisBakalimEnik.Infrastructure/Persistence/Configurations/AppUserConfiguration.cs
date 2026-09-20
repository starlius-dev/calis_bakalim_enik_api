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

        builder.HasIndex(u => u.Status);
        builder.HasQueryFilter(u => u.DeletedAt == null);
    }
}
