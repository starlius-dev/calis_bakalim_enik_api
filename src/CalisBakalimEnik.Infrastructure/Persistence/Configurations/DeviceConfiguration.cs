using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");

        builder.Property(d => d.InstallationId).HasMaxLength(128).IsRequired();
        builder.Property(d => d.Platform).HasConversion<short>();
        builder.Property(d => d.FcmToken).HasMaxLength(512);
        builder.Property(d => d.DeviceName).HasMaxLength(120);
        builder.Property(d => d.AppVersion).HasMaxLength(40);

        // GLOBAL, not per-user: FCM tokens are globally unique, and one physical
        // device reinstalling under a different account must not leave two rows
        // pointing at the same token -- the previous owner would keep receiving
        // the new owner's notifications.
        builder.HasIndex(d => d.FcmToken)
            .IsUnique()
            .HasFilter("fcm_token IS NOT NULL AND deleted_at IS NULL");

        builder.HasIndex(d => new { d.UserId, d.InstallationId }).IsUnique();

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
