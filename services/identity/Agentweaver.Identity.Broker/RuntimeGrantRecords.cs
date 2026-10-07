using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

public sealed class RuntimeGrantHead
{
    public Guid GrantId { get; set; }
    public long CurrentRevision { get; set; }
}

public sealed class RuntimeGrantRevision
{
    public Guid GrantId { get; set; }
    public long Revision { get; set; }
    public Guid RuntimeInstanceId { get; set; }
    public long RegistrationRevision { get; set; }
    public required string RegistrationJson { get; set; }
    public required string RegistrationHash { get; set; }
    public required string VerifierHash { get; set; }
    public required string Issuer { get; set; }
    public RuntimeCredentialPurpose Purpose { get; set; }
    public required string Audience { get; set; }
    public required string ConfigurationHash { get; set; }
    public RuntimeCredentialState State { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid DeliveryOperationId { get; set; }
}

public sealed class RuntimeGrantOperation
{
    public Guid OperationId { get; set; }
    public Guid GrantId { get; set; }
    public required string RequestHash { get; set; }
}

public sealed class RuntimeGrantOperationReceipt
{
    public Guid OperationId { get; set; }
    public required string ReceiptJson { get; set; }
}

internal static class RuntimeGrantModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<RuntimeGrantHead>(entity =>
        {
            entity.ToTable("runtime_grant_heads", table =>
                table.HasCheckConstraint("ck_runtime_grant_heads_revision", "current_revision > 0"));
            entity.HasKey(row => row.GrantId);
            entity.Property(row => row.GrantId).HasColumnName("grant_id");
            entity.Property(row => row.CurrentRevision).HasColumnName("current_revision");
        });
        builder.Entity<RuntimeGrantRevision>(entity =>
        {
            entity.ToTable("runtime_grant_revisions", table =>
            {
                table.HasCheckConstraint("ck_runtime_grant_revision", "revision > 0 AND registration_revision > 0");
                table.HasCheckConstraint("ck_runtime_grant_hashes",
                    "registration_hash ~ '^[0-9a-f]{64}$' AND verifier_hash ~ '^[0-9a-f]{64}$' AND configuration_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_runtime_grant_lifetime", "expires_at > recorded_at OR state = 2");
                table.HasCheckConstraint("ck_runtime_grant_purpose", "purpose IN (0, 1)");
                table.HasCheckConstraint("ck_runtime_grant_state", "state IN (0, 1, 2)");
            });
            entity.HasKey(row => new { row.GrantId, row.Revision });
            entity.Property(row => row.GrantId).HasColumnName("grant_id");
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.RuntimeInstanceId).HasColumnName("runtime_instance_id");
            entity.Property(row => row.RegistrationRevision).HasColumnName("registration_revision");
            entity.Property(row => row.RegistrationJson).HasColumnName("registration_json").HasMaxLength(16384);
            entity.Property(row => row.RegistrationHash).HasColumnName("registration_hash").HasMaxLength(64);
            entity.Property(row => row.VerifierHash).HasColumnName("verifier_hash").HasMaxLength(64);
            entity.Property(row => row.Issuer).HasColumnName("issuer").HasMaxLength(2048);
            entity.Property(row => row.Purpose).HasColumnName("purpose").HasConversion<int>();
            entity.Property(row => row.Audience).HasColumnName("audience").HasMaxLength(2048);
            entity.Property(row => row.ConfigurationHash).HasColumnName("configuration_hash").HasMaxLength(64);
            entity.Property(row => row.State).HasColumnName("state").HasConversion<int>();
            entity.Property(row => row.ExpiresAt).HasColumnName("expires_at");
            entity.Property(row => row.RecordedAt).HasColumnName("recorded_at");
            entity.Property(row => row.DeliveryOperationId).HasColumnName("delivery_operation_id");
            entity.HasIndex(row => row.RuntimeInstanceId);
            entity.HasOne<RuntimeGrantHead>().WithMany().HasForeignKey(row => row.GrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<RuntimeGrantOperation>(entity =>
        {
            entity.ToTable("runtime_grant_operations");
            entity.HasKey(row => row.OperationId);
            entity.Property(row => row.OperationId).HasColumnName("operation_id");
            entity.Property(row => row.GrantId).HasColumnName("grant_id");
            entity.Property(row => row.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
        });
        builder.Entity<RuntimeGrantOperationReceipt>(entity =>
        {
            entity.ToTable("runtime_grant_operation_receipts");
            entity.HasKey(row => row.OperationId);
            entity.Property(row => row.OperationId).HasColumnName("operation_id");
            entity.Property(row => row.ReceiptJson).HasColumnName("receipt_json").HasMaxLength(16384);
            entity.HasOne<RuntimeGrantOperation>().WithOne().HasForeignKey<RuntimeGrantOperationReceipt>(
                row => row.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
