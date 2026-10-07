using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

// Storage tests isolate the owner boundary. They are not runtime-provenance acceptance.
[Collection("IdentityBrokerPostgres")]
public sealed class RuntimeGrantPersistenceTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task GrantsConsumeExchangeRotateAndRevokeWithHashOnlyImmutableReceipts()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);
        var owner = new StorageOwner();
        var delivery = new StorageDelivery();
        var actor = CreateActor();
        var policy = CreatePolicy();
        var configurationHash = new string('a', 64);
        var deliverOperation = Guid.NewGuid();
        RuntimeGrantReceipt sourceReceipt;
        string bootstrapValue;
        string sourceValue;
        string rotatedValue;
        await using (var db = new IdentityBrokerDbContext(options))
        {
            var authority = new RuntimeGrantAuthority(db, owner, delivery, policy, actor, TimeProvider.System);
            var delivered = await authority.DeliverBootstrapAsync(
                owner.Registration.RuntimeInstanceId, configurationHash, deliverOperation);
            Assert.Equal(delivered, await authority.DeliverBootstrapAsync(
                owner.Registration.RuntimeInstanceId, configurationHash, deliverOperation));
            Assert.Equal(1, delivery.Deliveries);
            bootstrapValue = delivery.Credential!.GetValue();
            var bootstrap = Proof(delivered.GrantId, 1, RuntimeCredentialPurpose.Configure,
                owner.Registration.Binding.ConfigureEndpoint, delivery.Credential, owner, configurationHash);
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                authority.VerifySourceAsync(bootstrap));
            var consumeOperation = Guid.NewGuid();
            var consumed = await authority.ConsumeBootstrapAsync(bootstrap, consumeOperation);
            Assert.Equal(RuntimeCredentialState.Consumed, consumed.State);
            Assert.Equal(2, consumed.Revision);
            Assert.Equal(consumed, await authority.ConsumeBootstrapAsync(bootstrap, consumeOperation));
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                authority.ConsumeBootstrapAsync(bootstrap, Guid.NewGuid()));
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                authority.ConsumeBootstrapAsync(
                    Proof(delivered.GrantId, 1, RuntimeCredentialPurpose.Configure,
                        owner.Registration.Binding.ConfigureEndpoint, delivery.Credential, owner,
                        new string('b', 64)),
                    consumeOperation));

            var exchangeOperation = Guid.NewGuid();
            var exchangeProof = Proof(delivered.GrantId, 2, RuntimeCredentialPurpose.Configure,
                owner.Registration.Binding.ConfigureEndpoint, delivery.Credential, owner, configurationHash);
            var exchanged = await authority.ExchangeBootstrapAsync(exchangeProof, exchangeOperation);
            Assert.False(exchanged.IsReplay);
            Assert.NotNull(exchanged.Credential);
            sourceValue = exchanged.Credential.GetValue();
            Assert.NotEqual(bootstrapValue, sourceValue);
            var replayedExchange = await authority.ExchangeBootstrapAsync(exchangeProof, exchangeOperation);
            Assert.True(replayedExchange.IsReplay);
            Assert.Null(replayedExchange.Credential);
            Assert.Equal(exchanged.Receipt, replayedExchange.Receipt);
            var source = Proof(exchanged.Receipt.GrantId, 1, RuntimeCredentialPurpose.Observe,
                owner.Registration.Binding.ObservationEndpoint, exchanged.Credential, owner, configurationHash);
            Assert.Equal(exchanged.Receipt, await authority.VerifySourceAsync(source));
            var rotateOperation = Guid.NewGuid();
            var rotated = await authority.RotateSourceAsync(source, rotateOperation);
            Assert.NotNull(rotated.Credential);
            rotatedValue = rotated.Credential.GetValue();
            Assert.NotEqual(sourceValue, rotatedValue);
            Assert.Equal(2, rotated.Receipt.Revision);
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => authority.VerifySourceAsync(source));
            var repeatedRotation = await authority.RotateSourceAsync(source, rotateOperation);
            Assert.True(repeatedRotation.IsReplay);
            Assert.Null(repeatedRotation.Credential);
            Assert.Equal(rotated.Receipt, repeatedRotation.Receipt);
            var current = Proof(rotated.Receipt.GrantId, 2, RuntimeCredentialPurpose.Observe,
                owner.Registration.Binding.ObservationEndpoint, rotated.Credential, owner, configurationHash);
            sourceReceipt = await authority.VerifySourceAsync(current);
            var revokeOperation = Guid.NewGuid();
            var revoked = await authority.RevokeAsync(current, revokeOperation);
            Assert.Equal(RuntimeCredentialState.Revoked, revoked.State);
            Assert.Equal(3, revoked.Revision);
            Assert.Equal(revoked, await authority.RevokeAsync(current, revokeOperation));
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => authority.VerifySourceAsync(current));
        }

        await using var restarted = new IdentityBrokerDbContext(options);
        var revisions = await restarted.RuntimeGrantRevisions.AsNoTracking().ToArrayAsync();
        Assert.Equal(6, revisions.Length);
        var receipts = await restarted.RuntimeGrantOperationReceipts.AsNoTracking().ToArrayAsync();
        Assert.Equal(5, receipts.Length);
        foreach (var value in new[] { bootstrapValue, sourceValue, rotatedValue })
        {
            Assert.All(revisions, revision =>
            {
                Assert.DoesNotContain(value, revision.RegistrationJson);
                Assert.NotEqual(value, revision.VerifierHash);
            });
            Assert.All(receipts, receipt => Assert.DoesNotContain(value, receipt.ReceiptJson));
        }
        var immutableError = await Assert.ThrowsAsync<PostgresException>(() =>
            restarted.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE identity_broker.runtime_grant_revisions SET state = 2
                WHERE grant_id = {sourceReceipt.GrantId} AND revision = {1L}
                """));
        Assert.Contains("append-only", immutableError.MessageText);
        var skippedRevision = await Assert.ThrowsAsync<PostgresException>(() =>
            restarted.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE identity_broker.runtime_grant_heads SET current_revision = 9
                WHERE grant_id = {sourceReceipt.GrantId}
                """));
        Assert.Contains("advance by one", skippedRevision.MessageText);
    }

    [Fact]
    public async Task WrongAudienceSecretRuntimeAndPostAwaitOwnerChangeDenyWithoutConsumingNonce()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);
        var owner = new StorageOwner();
        var delivery = new StorageDelivery();
        var actor = CreateActor();
        var configurationHash = new string('a', 64);
        await using var db = new IdentityBrokerDbContext(options);
        var authority = new RuntimeGrantAuthority(db, owner, delivery, CreatePolicy(), actor, TimeProvider.System);
        var delivered = await authority.DeliverBootstrapAsync(
            owner.Registration.RuntimeInstanceId, configurationHash, Guid.NewGuid());
        var nonce = delivery.Credential!;
        var correct = Proof(delivered.GrantId, 1, RuntimeCredentialPurpose.Configure,
            owner.Registration.Binding.ConfigureEndpoint, nonce, owner, configurationHash);
        foreach (var invalid in new[]
        {
            Proof(delivered.GrantId, 1, RuntimeCredentialPurpose.Configure,
                new Uri("https://foreign.test/configure"), nonce, owner, configurationHash),
            Proof(delivered.GrantId, 1, RuntimeCredentialPurpose.Configure,
                owner.Registration.Binding.ConfigureEndpoint,
                new SecretCredential(new string('f', 64), nonce.ExpiresAt), owner, configurationHash),
            new RuntimeCredentialProof(delivered.GrantId, Guid.NewGuid(), 1,
                RuntimeCredentialPurpose.Configure, owner.Registration.Binding.ConfigureEndpoint,
                configurationHash, nonce)
        })
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                authority.ConsumeBootstrapAsync(invalid, Guid.NewGuid()));
        Assert.Equal(1, await db.RuntimeGrantRevisions.CountAsync());
        var originalRegistration = owner.Registration;
        owner.ChangeAfterRead = owner.Reads + 2;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            authority.ConsumeBootstrapAsync(correct, Guid.NewGuid()));
        Assert.Equal(1, await db.RuntimeGrantRevisions.CountAsync());
        owner.Registration = originalRegistration;
        owner.ChangeAfterRead = null;
        Assert.Equal(RuntimeCredentialState.Consumed,
            (await authority.ConsumeBootstrapAsync(correct, Guid.NewGuid())).State);
    }

    [Fact]
    public async Task MissingOrChangedDeliveryRevokesTheUndeliveredBootstrap()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);
        var owner = new StorageOwner();
        var delivery = new StorageDelivery { WrongPlacement = true };
        await using var db = new IdentityBrokerDbContext(options);
        var authority = new RuntimeGrantAuthority(db, owner, delivery, CreatePolicy(), CreateActor(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            authority.DeliverBootstrapAsync(owner.Registration.RuntimeInstanceId, new string('a', 64), Guid.NewGuid()));
        Assert.Equal("runtime_delivery_binding_invalid", error.Code);
        var revisions = await db.RuntimeGrantRevisions.AsNoTracking().OrderBy(row => row.Revision).ToArrayAsync();
        Assert.Equal(2, revisions.Length);
        Assert.Equal(RuntimeCredentialState.Revoked, revisions[1].State);
        Assert.Empty(await db.RuntimeGrantOperationReceipts.ToArrayAsync());
    }

    private static DbContextOptions<IdentityBrokerDbContext> CreateOptions(NpgsqlDataSource source) =>
        new DbContextOptionsBuilder<IdentityBrokerDbContext>().UseNpgsql(
            source, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema)).UseOpenIddict().Options;

    private static RuntimeCredentialPolicy CreatePolicy() =>
        new("https://broker.test/", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

    private static RuntimeActorAuthorization CreateActor() =>
        new(new SecretCredential("storage-test-actor", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant");

    private static RuntimeCredentialProof Proof(
        Guid grantId, long revision, RuntimeCredentialPurpose purpose, Uri audience,
        SecretCredential credential, StorageOwner owner, string configurationHash) =>
        new(grantId, owner.Registration.RuntimeInstanceId, revision, purpose, audience, configurationHash, credential);

    private sealed class StorageOwner : IRuntimeRegistrationOwner
    {
        public RuntimeRegistration Registration { get; set; } = new(
            Guid.NewGuid(), 1,
            new RuntimeBinding(
                "https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
                "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
                "environment", "placement-uid", 1, "profile",
                new Uri("https://runtime.test/configure"), new Uri("https://orchestrator.test/runtime/observations"))
            {
                EnvironmentCurrentFencingGeneration = 4,
                EnvironmentProviderFencingGeneration = 7
            },
            RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
        public int Reads { get; private set; }
        public int? ChangeAfterRead { get; set; }

        public Task<RuntimeRegistration> ReadCurrentAsync(
            Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = actor.Bearer.GetValue();
            Reads++;
            if (ChangeAfterRead == Reads)
                Registration = Registration with { Revision = Registration.Revision + 1 };
            if (runtimeInstanceId != Registration.RuntimeInstanceId)
                throw new RuntimeAuthorizationException("runtime_registration_unknown");
            return Task.FromResult(Registration);
        }
    }

    private sealed class StorageDelivery : IRuntimeBootstrapDelivery
    {
        public SecretCredential? Credential { get; private set; }
        public int Deliveries { get; private set; }
        public bool WrongPlacement { get; init; }

        public Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(
            RuntimeRegistration registration, RuntimeActorAuthorization actor, Guid operationId,
            Guid grantId, string configurationHash, SecretCredential credential, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = actor.Bearer.GetValue();
            Deliveries++;
            Credential = new SecretCredential(credential.GetValue(), credential.ExpiresAt);
            return Task.FromResult(new RuntimeBootstrapDeliveryReceipt(
                operationId, grantId, registration.RuntimeInstanceId, registration.Revision,
                WrongPlacement ? "foreign-placement" : registration.Binding.PlacementUid,
                registration.Binding.PlacementGeneration, registration.Binding.ExecutionFence,
                configurationHash, DateTimeOffset.UtcNow)
            {
                EnvironmentCurrentFencingGeneration = registration.Binding.EnvironmentCurrentFencingGeneration,
                EnvironmentProviderFencingGeneration = registration.Binding.EnvironmentProviderFencingGeneration
            });
        }
    }
}
