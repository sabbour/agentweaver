using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence.Configurations;

internal sealed class EventEntityConfiguration : IEntityTypeConfiguration<EventEntity>
{
    public void Configure(EntityTypeBuilder<EventEntity> builder)
    {
        builder.ToTable("Events");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.RunId, e.Sequence }).IsUnique();
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(50);
        builder.Property(e => e.Payload).HasColumnType("TEXT");
        builder.HasOne(e => e.Run)
            .WithMany(r => r.Events)
            .HasForeignKey(e => e.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
