using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence.Configurations;

internal sealed class SessionEntityConfiguration : IEntityTypeConfiguration<SessionEntity>
{
    public void Configure(EntityTypeBuilder<SessionEntity> builder)
    {
        builder.ToTable("Sessions");
        builder.HasKey(s => s.Id);

        builder.HasIndex(s => s.RunId).IsUnique();

        builder.Property(s => s.ArtifactDir).HasMaxLength(1000);
        builder.Property(s => s.WorktreePath).HasMaxLength(1000);
        builder.Property(s => s.OriginatingCommit).HasMaxLength(100);
    }
}
