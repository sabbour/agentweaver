using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence.Configurations;

internal sealed class OperationalRecordEntityConfiguration : IEntityTypeConfiguration<OperationalRecordEntity>
{
    public void Configure(EntityTypeBuilder<OperationalRecordEntity> builder)
    {
        builder.ToTable("OperationalRecords");
        builder.HasKey(o => o.Id);

        // One operational record per run (distinct from event log)
        builder.HasIndex(o => o.RunId).IsUnique();

        builder.Property(o => o.ModelSource)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(o => o.Outcome)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(o => o.SubmittedBy).HasMaxLength(500);
        builder.Property(o => o.PolicyTrace).HasColumnType("TEXT");
    }
}
