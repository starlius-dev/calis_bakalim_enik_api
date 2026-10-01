using CalisBakalimEnik.Domain.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class ClientErrorReportConfiguration : IEntityTypeConfiguration<ClientErrorReport>
{
    // The limits the endpoint truncates to. Here so the column and the
    // truncation cannot drift apart.
    public const int MessageLength = 2_000;
    public const int StackLength = 12_000;
    public const int RouteLength = 200;

    public void Configure(EntityTypeBuilder<ClientErrorReport> builder)
    {
        builder.ToTable("client_error_reports");

        builder.Property(r => r.Id).UseIdentityByDefaultColumn();
        builder.Property(r => r.Platform).HasMaxLength(16).IsRequired();
        builder.Property(r => r.AppVersion).HasMaxLength(40).IsRequired();
        builder.Property(r => r.Kind).HasMaxLength(16).IsRequired();
        builder.Property(r => r.Message).HasMaxLength(MessageLength).IsRequired();
        builder.Property(r => r.Stack).HasMaxLength(StackLength);
        builder.Property(r => r.Route).HasMaxLength(RouteLength);
        builder.Property(r => r.CorrelationId).HasMaxLength(64);
        builder.Property(r => r.Locale).HasMaxLength(16);
        builder.Property(r => r.UserAgent).HasMaxLength(400);

        // The admin list (newest first, by id) needs nothing more; these are
        // the retention sweep and the erasure of one person's reports.
        builder.HasIndex(r => r.ReceivedAt);
        builder.HasIndex(r => r.UserId);

        // No FK to users, like security_events: a report from before sign-in
        // has no user, and erasure deletes a person's reports through
        // UserDataMap rather than by cascade.
    }
}
