using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Maps a successfully validated external login to a persisted local identity. The
/// (issuer, subject) pair is the only trusted linking key; email is stored for display only
/// and never used to merge accounts.
/// </summary>
public sealed class BrokerUserProvisioner(IdentityBrokerDbContext db, TimeProvider timeProvider)
{
    public async Task<BrokerUser> ProvisionAsync(
        string issuer, string subject, string? displayName, string? email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer)) throw new ArgumentException("An issuer is required.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("A subject is required.", nameof(subject));

        var user = await db.Users.SingleOrDefaultAsync(
            u => u.Issuer == issuer && u.Subject == subject, cancellationToken);
        var inserting = user is null;
        if (user is null)
        {
            user = new BrokerUser
            {
                Id = Guid.NewGuid(),
                Issuer = issuer,
                Subject = subject,
                DisplayName = displayName,
                Email = email,
                CreatedAt = timeProvider.GetUtcNow(),
            };
            db.Users.Add(user);
        }
        else
        {
            user.DisplayName = displayName ?? user.DisplayName;
            user.Email = email ?? user.Email;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (inserting &&
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                SchemaName: IdentityBrokerDbContext.Schema,
                TableName: "broker_users",
                ConstraintName: "IX_broker_users_Issuer_Subject"
            })
        {
            db.Entry(user).State = EntityState.Detached;
            return await db.Users.SingleAsync(
                u => u.Issuer == issuer && u.Subject == subject, cancellationToken);
        }
        return user;
    }
}
