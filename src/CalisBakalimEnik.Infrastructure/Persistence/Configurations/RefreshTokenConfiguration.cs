using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.Property(t => t.TokenHash).HasMaxLength(88).IsRequired();
        builder.Property(t => t.RevokedReason).HasConversion<short?>();
        builder.Property(t => t.CreatedIp).HasMaxLength(64);
        builder.Property(t => t.UserAgent).HasMaxLength(400);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => new { t.UserId, t.RevokedAt });
        builder.HasIndex(t => t.ExpiresAt).HasFilter("revoked_at IS NULL");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(t => t.ReplacedById)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
