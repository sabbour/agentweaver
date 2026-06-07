using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence.Configurations;

internal sealed class ToolOperationEntityConfiguration : IEntityTypeConfiguration<ToolOperationEntity>
{
    public void Configure(EntityTypeBuilder<ToolOperationEntity> builder)
    {
        builder.ToTable("ToolOperations");
        builder.HasKey(t => t.Id);
        builder.HasIndex(t => new { t.RunId, t.CallId });
        builder.Property(t => t.ToolName).HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.Result).HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.ErrorCode).HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.RequestedPath).HasMaxLength(2000);
        builder.Property(t => t.ResolvedPath).HasMaxLength(2000);
        builder.HasOne(t => t.Run)
            .WithMany()
            .HasForeignKey(t => t.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
