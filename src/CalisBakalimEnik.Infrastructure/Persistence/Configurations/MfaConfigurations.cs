using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class MfaFactorConfiguration : IEntityTypeConfiguration<MfaFactor>
{
    public void Configure(EntityTypeBuilder<MfaFactor> builder)
    {
        builder.ToTable("mfa_factors");

        builder.Property(f => f.FactorType).HasConversion<short>();
        builder.Property(f => f.Destination).HasMaxLength(254);
        builder.Ignore(f => f.IsUsable);
        builder.Ignore(f => f.MaskedDestination);

        // One factor of each type per user -- except recovery codes, which are
        // a batch rather than a single enrolment.
        builder.HasIndex(f => new { f.UserId, f.FactorType })
            .IsUnique()
            .HasDatabaseName("ux_mfa_factors_user_type")
            .HasFilter("deleted_at IS NULL AND factor_type <> 4");

        builder.HasIndex(f => f.UserId)
            .IsUnique()
            .HasDatabaseName("ux_mfa_factors_primary")
            .HasFilter("is_primary AND deleted_at IS NULL");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_mfa_factor_type", "factor_type BETWEEN 1 AND 4"));

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        builder.ToTable("mfa_recovery_codes");

        builder.Property(c => c.CodeHash).HasMaxLength(256).IsRequired();
        builder.Property(c => c.UsedIp).HasMaxLength(64);

        builder.HasIndex(c => c.UserId)
            .HasDatabaseName("ix_mfa_recovery_codes_user")
            .HasFilter("used_at IS NULL");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("security_events");

        builder.Property(e => e.Id).UseIdentityByDefaultColumn();
        builder.Property(e => e.EventType).HasConversion<short>();
        builder.Property(e => e.Ip).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(400);
        builder.Property(e => e.Detail).HasColumnType("jsonb").HasDefaultValue("{}");

        builder.HasIndex(e => new { e.UserId, e.OccurredAt })
            .HasDatabaseName("ix_security_events_user")
            .IsDescending(false, true);

        builder.HasIndex(e => new { e.Ip, e.OccurredAt })
            .HasDatabaseName("ix_security_events_ip")
            .IsDescending(false, true);

        builder.HasIndex(e => new { e.EventType, e.OccurredAt })
            .HasDatabaseName("ix_security_events_type")
            .IsDescending(false, true);

        // No FK to users: an attempt against an address that does not exist has
        // no user, and a purged account must not take its security history with it.
    }
}
