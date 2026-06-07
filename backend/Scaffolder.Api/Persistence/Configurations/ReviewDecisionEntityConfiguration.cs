using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence.Configurations;

internal sealed class ReviewDecisionEntityConfiguration : IEntityTypeConfiguration<ReviewDecisionEntity>
{
    public void Configure(EntityTypeBuilder<ReviewDecisionEntity> builder)
    {
        builder.ToTable("ReviewDecisions");
        builder.HasKey(r => r.Id);

        // One review decision per run
        builder.HasIndex(r => r.RunId).IsUnique();

        builder.Property(r => r.Decision)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.MergeResult)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.Reviewer).HasMaxLength(500);
        builder.Property(r => r.Comment).HasMaxLength(5000);
    }
}
