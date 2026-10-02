using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class PersonalDetailChangeConfiguration : IEntityTypeConfiguration<PersonalDetailChange>
{
    public void Configure(EntityTypeBuilder<PersonalDetailChange> builder)
    {
        builder.ToTable("personal_detail_changes");

        builder.Property(c => c.Field).HasMaxLength(40).IsRequired();
        builder.Property(c => c.OldValue).HasMaxLength(256);
        builder.Property(c => c.NewValue).HasMaxLength(256);

        // The person's own history, newest first, and the retention sweep.
        builder.HasIndex(c => new { c.UserId, c.ChangedAt })
            .HasDatabaseName("ix_personal_detail_changes_user");
        builder.HasIndex(c => c.ChangedAt);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
