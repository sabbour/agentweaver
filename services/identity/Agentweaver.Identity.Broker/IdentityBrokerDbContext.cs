using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Owns the broker's Postgres schema: OpenIddict's application/authorization/scope/token
/// stores plus the broker's own <see cref="BrokerUser"/> table. No other service reads or
/// writes this schema directly.
/// </summary>
public sealed class IdentityBrokerDbContext : DbContext
{
    public const string Schema = "identity_broker";

    public IdentityBrokerDbContext(DbContextOptions<IdentityBrokerDbContext> options) : base(options)
    {
    }

    public DbSet<BrokerUser> Users => Set<BrokerUser>();

    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<BrokerUser>(entity =>
        {
            entity.ToTable("broker_users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Issuer).HasMaxLength(2048).IsRequired();
            entity.Property(user => user.Subject).HasMaxLength(512).IsRequired();
            entity.Property(user => user.DisplayName).HasMaxLength(256);
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.HasIndex(user => new { user.Issuer, user.Subject }).IsUnique();
        });

        modelBuilder.Entity<PendingAuthorization>(entity =>
        {
            entity.ToTable("pending_authorizations");
            entity.HasKey(pending => pending.Id);
            entity.Property(pending => pending.HandleHash).HasMaxLength(64).IsRequired();
            entity.Property(pending => pending.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(pending => pending.RedirectUri).HasMaxLength(2048).IsRequired();
            entity.Property(pending => pending.Scope).HasMaxLength(2048).IsRequired();
            entity.Property(pending => pending.State).HasMaxLength(2048);
            entity.Property(pending => pending.CodeChallenge).HasMaxLength(256);
            entity.Property(pending => pending.CodeChallengeMethod).HasMaxLength(32);
            entity.Property(pending => pending.Nonce).HasMaxLength(256);
            entity.HasIndex(pending => pending.HandleHash).IsUnique();
            entity.HasIndex(pending => pending.ExpiresAt);
        });

        // Registers OpenIddict's application/authorization/scope/token entity types;
        // they share this context's default schema set above.
        modelBuilder.UseOpenIddict();
    }
}
