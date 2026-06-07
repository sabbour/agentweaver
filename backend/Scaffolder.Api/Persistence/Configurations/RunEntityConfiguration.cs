using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence.Configurations;

internal sealed class RunEntityConfiguration : IEntityTypeConfiguration<RunEntity>
{
    public void Configure(EntityTypeBuilder<RunEntity> builder)
    {
        builder.ToTable("Runs");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ModelSource)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.CreatedAt);

        builder.HasOne(r => r.Session)
            .WithOne(s => s.Run)
            .HasForeignKey<RunEntity>(r => r.SessionId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Events)
            .WithOne(e => e.Run)
            .HasForeignKey(e => e.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relationships to entities implemented in later tasks are configured once
        // those entities expose their RunId foreign keys and Run navigation properties:
        //   TODO(T012): builder.HasOne(r => r.ReviewDecision) -> ReviewDecisionEntity (RunId FK), cascade delete.
        //   TODO(T013): builder.HasOne(r => r.OperationalRecord) -> OperationalRecordEntity (RunId FK), cascade delete.
    }
}
