using System.Data.Common;
using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class ModelProviderCredentialStorePostgresTests(PostgresContainerFixture postgres)
{
    private const string ExistingVault = "https://agentweaver-kv.vault.azure.net/";
    private const string ByokVault = "https://agentweaver-byok.vault.azure.net/";

    [Fact]
    public async Task UnresolvedWriteClaimStaysQuarantinedAndBlocksRetryAndReuse()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var claim = Claim(Guid.NewGuid(), 0, identity);
        await using (var db = CreateDb(connectionString))
        {
            var acquired = await Store(db).ClaimWriteAsync(claim);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Acquired, acquired.Status);
            Assert.True(acquired.Acquired);
        }

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var retry = await store.ClaimWriteAsync(claim);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined, retry.Status);
            Assert.False(retry.Acquired);
            await AssertCodeAsync("credential_slot_conflict", () => store.RecordReferenceAsync(
                Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a'))));
            Assert.Equal(ModelProviderCredentialWriteClaimState.Claimed,
                (await db.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).State);

            var uncertain = await store.MarkWriteUncertainAsync(claim);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined, uncertain.Status);
            Assert.False(uncertain.Acquired);
            Assert.Equal(ModelProviderCredentialWriteClaimState.Quarantined,
                (await db.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).State);
            await AssertCodeAsync("credential_slot_conflict", () => store.CompleteWriteAsync(
                Record(claim.OperationId, 0, identity, Ref("provider-key", 'a'))));
            await AssertCodeAsync("credential_slot_conflict", () => store.ClaimWriteAsync(
                claim with { OperationId = Guid.NewGuid() }));
        }

        await using var verifyDb = CreateDb(connectionString);
        Assert.Empty(await verifyDb.ModelProviderCredentialHeads.ToListAsync());
        Assert.Empty(await verifyDb.ModelProviderCredentialRevisions.ToListAsync());
        Assert.Equal(ModelProviderCredentialWriteClaimState.Quarantined,
            (await verifyDb.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task ProviderClaimAndReferenceWriteCannotRaceOnTheSameSlot()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var otherProvider = identity with { ProviderId = "provider-2" };
        var claimTask = CaptureClaimAsync(connectionString, Claim(Guid.NewGuid(), 0, identity));
        var recordTask = CaptureAsync(() => RecordInNewContextAsync(connectionString,
            Record(Guid.NewGuid(), 0, otherProvider, Ref("provider-key", 'a'))));
        await Task.WhenAll(claimTask, recordTask);
        var claim = await claimTask;
        var record = await recordTask;
        Assert.NotEqual(claim is not null, record is not null);

        await using var verifyDb = CreateDb(connectionString);
        var heads = await verifyDb.ModelProviderCredentialHeads.AsNoTracking().ToListAsync();
        var claims = await verifyDb.ModelProviderCredentialWriteClaims.AsNoTracking().ToListAsync();
        if (claim is not null)
        {
            Assert.Empty(heads);
            Assert.Single(claims);
            Assert.Equal(identity.ProviderId, claims[0].ProviderId);
        }
        else
        {
            Assert.Single(heads);
            Assert.Empty(claims);
            Assert.Equal(otherProvider.ProviderId, heads[0].ProviderId);
        }
    }

    [Fact]
    public async Task ClaimAndReferenceCannotRaceOnOperationIdAcrossSlots()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var claimedIdentity = Identity();
        var recordedIdentity = Identity() with { ProviderId = "provider-2" };
        var operationId = Guid.NewGuid();
        var claimTask = CaptureClaimAsync(connectionString, Claim(operationId, 0, claimedIdentity));
        var recordTask = CaptureAsync(() => RecordInNewContextAsync(connectionString,
            Record(operationId, 0, recordedIdentity, Ref("provider-key", 'a'))));
        await Task.WhenAll(claimTask, recordTask);
        var claim = await claimTask;
        var record = await recordTask;
        Assert.NotEqual(claim is not null, record is not null);

        await using var verifyDb = CreateDb(connectionString);
        var heads = await verifyDb.ModelProviderCredentialHeads.AsNoTracking().ToListAsync();
        var claims = await verifyDb.ModelProviderCredentialWriteClaims.AsNoTracking().ToListAsync();
        var revisions = await verifyDb.ModelProviderCredentialRevisions.AsNoTracking().ToListAsync();
        if (claim is not null)
        {
            Assert.Empty(heads);
            Assert.Empty(revisions);
            Assert.Single(claims);
            Assert.Equal(operationId, claims[0].OperationId);
        }
        else
        {
            Assert.Single(heads);
            Assert.Equal(recordedIdentity.ProviderId, heads[0].ProviderId);
            Assert.Empty(claims);
            Assert.Single(revisions);
            Assert.Equal(operationId, revisions[0].OperationId);
        }
    }

    [Fact]
    public async Task CompletesWriteClaimAtomicallyAndReplaysExactVersion()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var claim = Claim(Guid.NewGuid(), 0, identity);
        var record = Record(claim.OperationId, 0, identity, Ref("provider-key", 'a'));
        await using (var db = CreateDb(connectionString))
            Assert.True((await Store(db).ClaimWriteAsync(claim)).Acquired);

        await using (var failingDb = CreateDb(connectionString, new FailCredentialRevisionInsertOnce()))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => Store(failingDb).CompleteWriteAsync(record));
            Assert.IsType<InjectedInsertFailure>(exception.InnerException);
        }

        await using (var verifyDb = CreateDb(connectionString))
        {
            Assert.Empty(await verifyDb.ModelProviderCredentialHeads.ToListAsync());
            Assert.Empty(await verifyDb.ModelProviderCredentialRevisions.ToListAsync());
            Assert.Equal(ModelProviderCredentialWriteClaimState.Claimed,
                (await verifyDb.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).State);
        }

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var completed = await store.CompleteWriteAsync(record);
            var replay = await store.CompleteWriteAsync(record);
            var claimReplay = await store.ClaimWriteAsync(claim);
            Assert.Equal(1, completed.Revision);
            Assert.Equal(record.Reference, completed.Reference);
            Assert.Equal(completed.Revision, replay.Revision);
            Assert.Equal(completed.Reference, replay.Reference);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Completed, claimReplay.Status);
            Assert.False(claimReplay.Acquired);
            Assert.Equal(record.Reference, claimReplay.Credential!.Reference);
            await AssertCodeAsync("credential_slot_conflict", () => store.CompleteWriteAsync(
                record with { Reference = Ref("provider-key", 'b') }));
        }

        await using var finalDb = CreateDb(connectionString);
        Assert.Equal(1, await finalDb.ModelProviderCredentialRevisions.CountAsync());
        Assert.Equal(ModelProviderCredentialWriteClaimState.Completed,
            (await finalDb.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task RecordsRotatesDisablesAndReadsExactHistoricalReferencesAcrossContexts()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var referenceA = Ref("provider-key", 'a');
        var referenceB = Ref("provider-key", 'b');
        var record = Record(Guid.NewGuid(), 0, identity, referenceA);
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(record);

        await using (var db = CreateDb(connectionString))
        {
            var rotation = await Store(db).RecordReferenceAsync(Record(Guid.NewGuid(), 1, identity, referenceB));
            Assert.Equal(2, rotation.Revision);
            Assert.Equal(referenceB, rotation.Reference);
        }

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var current = await store.ReadCurrentAsync(identity);
            Assert.Equal(ModelProviderCredentialCurrentStatus.Found, current.Status);
            Assert.Equal(referenceB, current.Credential!.Reference);
            Assert.Equal(referenceA, (await store.ReadPinnedAsync(identity, referenceA)).Credential!.Reference);
        }

        await using (var db = CreateDb(connectionString))
            await Store(db).DisableAsync(Disable(Guid.NewGuid(), 2, identity));

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var current = await store.ReadCurrentAsync(identity);
            Assert.Equal(ModelProviderCredentialCurrentStatus.Disabled, current.Status);
            Assert.Null(current.Credential);
            Assert.Equal(referenceA, (await store.ReadPinnedAsync(identity, referenceA)).Credential!.Reference);
            Assert.Equal(referenceB, (await store.ReadPinnedAsync(identity, referenceB)).Credential!.Reference);
            await AssertCodeAsync("credential_slot_conflict", () =>
                store.RecordReferenceAsync(Record(Guid.NewGuid(), 3, identity, Ref("provider-key", 'c'))));
            await AssertCodeAsync("credential_slot_conflict", () =>
                store.DisableAsync(Disable(Guid.NewGuid(), 3, identity)));
        }
    }

    [Fact]
    public async Task ReplaysOriginalOperationAndRejectsChangedMetadataAndWrongIdentity()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var original = Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a'));
        var rotation = Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b'));
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(original);
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(rotation);

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var replay = await store.RecordReferenceAsync(original);
            Assert.Equal(1, replay.Revision);
            Assert.Equal(original.Reference, replay.Reference);
            await AssertCodeAsync("credential_slot_conflict", () =>
                store.RecordReferenceAsync(original with { Reference = Ref("provider-key", 'c') }));

            var wrongProject = identity with { ProjectId = "other-project" };
            Assert.Equal(ModelProviderCredentialCurrentStatus.NotFound,
                (await store.ReadCurrentAsync(wrongProject)).Status);
            Assert.Equal(ModelProviderCredentialPinnedStatus.NotFound,
                (await store.ReadPinnedAsync(wrongProject, original.Reference)).Status);
            Assert.Equal(ModelProviderCredentialCurrentStatus.NotFound,
                (await store.ReadCurrentAsync(identity with { ProviderId = "provider-2" })).Status);
            Assert.Equal(ModelProviderCredentialPinnedStatus.NotFound,
                (await store.ReadPinnedAsync(identity, Ref("provider-key", 'f'))).Status);
        }

        await using (var db = CreateDb(connectionString))
        {
            var changedVaults = ModelProviderCredentialVaults.Create(
                "https://replacement-kv.vault.azure.net/", ByokVault);
            await AssertCodeAsync("credential_vault_unavailable", () =>
                new ModelProviderCredentialStore(db, changedVaults, TimeProvider.System)
                    .ReadPinnedAsync(identity, original.Reference));
        }
    }

    [Fact]
    public async Task RejectsVaultSourceRebindingWithoutChangingTheHead()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var original = Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a'));
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(original);

        var rebound = new RecordModelProviderCredentialReference(
            Guid.NewGuid(), 1, identity.CredentialSlotId, identity.Scope, identity.ProjectId,
            identity.ProviderId, ModelProviderCredentialVaultSource.Byok, Ref("byok-project-provider-1", 'b'),
            "broker:actor");
        await using (var db = CreateDb(connectionString))
        {
            await AssertCodeAsync("credential_slot_conflict", () =>
                Store(db).RecordReferenceAsync(rebound));
        }

        await using var verifyDb = CreateDb(connectionString);
        var current = await Store(verifyDb).ReadCurrentAsync(identity);
        Assert.Equal(ModelProviderCredentialCurrentStatus.Found, current.Status);
        Assert.Equal(original.Reference, current.Credential!.Reference);
        Assert.Equal(ModelProviderCredentialVaultSource.Existing, current.Credential.VaultSource);
        Assert.Equal(1, await verifyDb.ModelProviderCredentialRevisions.CountAsync());
    }

    [Fact]
    public async Task ReplaysOriginalRecordAndDisableAfterTerminalDisable()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var original = Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a'));
        var disable = Disable(Guid.NewGuid(), 1, identity);
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(original);
        await using (var db = CreateDb(connectionString))
            await Store(db).DisableAsync(disable);

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var recordReplay = await store.RecordReferenceAsync(original);
            var disableReplay = await store.DisableAsync(disable);
            Assert.Equal(1, recordReplay.Revision);
            Assert.Equal(original.Reference, recordReplay.Reference);
            Assert.Equal(2, disableReplay.Revision);
            Assert.Equal(ModelProviderCredentialState.Disabled, disableReplay.State);
        }

        await using var verifyDb = CreateDb(connectionString);
        var current = await Store(verifyDb).ReadCurrentAsync(identity);
        Assert.Equal(ModelProviderCredentialCurrentStatus.Disabled, current.Status);
        Assert.Equal(2, await verifyDb.ModelProviderCredentialRevisions.CountAsync());
        Assert.Equal(2, (await verifyDb.ModelProviderCredentialHeads
            .SingleAsync(row => row.CredentialSlotId == identity.CredentialSlotId)).Revision);
    }

    [Fact]
    public async Task ParallelCreatesAndMutationsLeaveOneCommittedRevisionPerHeadAdvance()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var duplicate = Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a'));
        var sameOperationResults = await Task.WhenAll(
            RecordInNewContextAsync(connectionString, duplicate),
            RecordInNewContextAsync(connectionString, duplicate));
        Assert.All(sameOperationResults, result => Assert.Equal(1, result.Revision));

        var competingIdentity = identity with { CredentialSlotId = Guid.NewGuid(), ProviderId = "provider-2" };
        var competingCreates = await Task.WhenAll(
            CaptureAsync(() => RecordInNewContextAsync(connectionString,
                Record(Guid.NewGuid(), 0, competingIdentity, Ref("provider-key", 'd')))),
            CaptureAsync(() => RecordInNewContextAsync(connectionString,
                Record(Guid.NewGuid(), 0, competingIdentity, Ref("provider-key", 'e')))));
        Assert.Single(competingCreates, result => result is not null);
        Assert.Single(competingCreates, result => result is null);

        var rotate = Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b'));
        var disable = Disable(Guid.NewGuid(), 1, identity);
        var results = await Task.WhenAll(
            CaptureAsync(() => RecordInNewContextAsync(connectionString, rotate)),
            CaptureAsync(() => DisableInNewContextAsync(connectionString, disable)));
        Assert.Single(results, result => result is not null);
        Assert.Single(results, result => result is null);

        await using var db = CreateDb(connectionString);
        Assert.Equal(3, await db.ModelProviderCredentialRevisions.CountAsync());
        var head = await db.ModelProviderCredentialHeads
            .SingleAsync(row => row.CredentialSlotId == identity.CredentialSlotId);
        Assert.Equal(2, head.Revision);
        Assert.Equal(2, await db.ModelProviderCredentialHeads.CountAsync());
    }

    [Fact]
    public async Task ParallelRotationsCommitOnlyOneNextRevision()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a')));

        var rotations = new[]
        {
            Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b')),
            Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'c')),
        };
        var results = await Task.WhenAll(rotations.Select(request =>
            CaptureAsync(() => RecordInNewContextAsync(connectionString, request))));
        Assert.Single(results, result => result is not null);
        Assert.Single(results, result => result is null);

        await using var verifyDb = CreateDb(connectionString);
        var head = await verifyDb.ModelProviderCredentialHeads
            .SingleAsync(row => row.CredentialSlotId == identity.CredentialSlotId);
        Assert.Equal(2, head.Revision);
        Assert.Contains(head.CurrentVersion, rotations.Select(request => request.Reference.Version));
        Assert.Equal(2, await verifyDb.ModelProviderCredentialRevisions
            .CountAsync(row => row.CredentialSlotId == identity.CredentialSlotId));
    }

    [Fact]
    public async Task RollsBackHeadWhenHistoryInsertFailsAndRetriesTheSameOperation()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var first = Ref("provider-key", 'a');
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, first));

        var rotation = Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b'));
        var interceptor = new FailCredentialRevisionInsertOnce();
        await using (var failedDb = CreateDb(connectionString, interceptor))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                Store(failedDb).RecordReferenceAsync(rotation));
            Assert.IsType<InjectedInsertFailure>(exception.InnerException);
        }

        await using (var db = CreateDb(connectionString))
        {
            Assert.Equal(first, (await Store(db).ReadCurrentAsync(identity)).Credential!.Reference);
            Assert.Equal(1, await db.ModelProviderCredentialRevisions.CountAsync());
        }
        await using (var retryDb = CreateDb(connectionString))
            Assert.Equal(2, (await Store(retryDb).RecordReferenceAsync(rotation)).Revision);
    }

    [Fact]
    public async Task MigratedRuntimeRoleCanWriteHeadsAndHistoryButCannotDeleteOrRewriteLedger()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var runtimeRole = "credential_runtime_" + Guid.NewGuid().ToString("N");
        var migrationRole = "credential_migration_" + Guid.NewGuid().ToString("N");
        var tableNames = new[]
        {
            "broker_users", "pending_authorizations", "secret_grant_heads", "secret_grant_revisions",
            "secret_grant_operations", "OpenIddictApplications", "OpenIddictAuthorizations",
            "OpenIddictScopes", "OpenIddictTokens", "__ef_migrations_history",
            "runtime_grant_heads", "runtime_grant_revisions", "runtime_grant_operations",
            "runtime_grant_operation_receipts", "copilot_connections", "copilot_connection_revisions",
            "repo_app_authorization_transactions", "repo_app_connections", "repo_app_installations",
            "repo_app_repository_selections", "issue_sync_grants", "issue_sync_mint_audit",
            "model_provider_credentials", "model_provider_credential_revisions",
            "model_provider_credential_write_claims",
        };
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand(
            $"CREATE ROLE \"{runtimeRole}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT; " +
            $"CREATE ROLE \"{migrationRole}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT; " +
            $"ALTER SCHEMA identity_broker OWNER TO \"{migrationRole}\";", connection))
            await setup.ExecuteNonQueryAsync();
        foreach (var table in tableNames)
        {
            await using var owner = new NpgsqlCommand(
                $"ALTER TABLE identity_broker.{QuoteIdentifier(table)} OWNER TO \"{migrationRole}\"", connection);
            await owner.ExecuteNonQueryAsync();
        }
        Assert.True(await IdentityBrokerPostgresBootstrapCommand.ApplyRuntimeGrantsIfMigratedAsync(
            connection, runtimeRole, migrationRole));

        var slotId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using (var setRole = new NpgsqlCommand($"SET ROLE \"{runtimeRole}\"", connection))
            await setRole.ExecuteNonQueryAsync();
        await using (var write = new NpgsqlCommand("""
            INSERT INTO identity_broker.model_provider_credentials
                (credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
                 secret_name, current_version, state, revision, set_by, set_at)
            VALUES
                (@slotId, 'Project', 'project-1', 'provider-1', 'Existing', @vaultUri,
                 'provider-key', @versionA, 'Enabled', 1, 'broker:actor', now());
            INSERT INTO identity_broker.model_provider_credential_revisions
                (credential_slot_id, revision, scope, project_id, provider_id, vault_source,
                 vault_uri, secret_name, secret_version, state, mutation_kind, operation_id,
                 request_fingerprint, actor_id, recorded_at)
            VALUES
                (@slotId, 1, 'Project', 'project-1', 'provider-1', 'Existing', @vaultUri,
                 'provider-key', @versionA, 'Enabled', 'RecordReference', @operationId,
                 @fingerprint, 'broker:actor', now());
            INSERT INTO identity_broker.model_provider_credential_write_claims
                (operation_id, credential_slot_id, scope, project_id, provider_id, vault_source,
                 vault_uri, secret_name, expected_revision, request_fingerprint, actor_id, state, claimed_at)
            VALUES
                (@claimOperationId, @slotId, 'Project', 'project-1', 'provider-1', 'Existing',
                 @vaultUri, 'provider-key', 2, @fingerprint, 'broker:actor', 'Claimed', now());
            UPDATE identity_broker.model_provider_credential_write_claims
            SET state = 'Quarantined', quarantined_at = now()
            WHERE operation_id = @claimOperationId;
            UPDATE identity_broker.model_provider_credentials
            SET current_version = @versionB, revision = 2, set_at = now()
            WHERE credential_slot_id = @slotId AND revision = 1;
            """, connection))
        {
            write.Parameters.AddWithValue("slotId", slotId);
            write.Parameters.AddWithValue("operationId", operationId);
            write.Parameters.AddWithValue("claimOperationId", Guid.NewGuid());
            write.Parameters.AddWithValue("vaultUri", ExistingVault);
            write.Parameters.AddWithValue("versionA", new string('a', 32));
            write.Parameters.AddWithValue("versionB", new string('b', 32));
            write.Parameters.AddWithValue("fingerprint", new string('c', 64));
            await write.ExecuteNonQueryAsync();
        }

        foreach (var statement in new[]
        {
            $"DELETE FROM identity_broker.model_provider_credentials WHERE credential_slot_id = '{slotId}'",
            "UPDATE identity_broker.model_provider_credential_revisions SET actor_id = 'other'",
            "DELETE FROM identity_broker.model_provider_credential_revisions",
            "TRUNCATE identity_broker.model_provider_credential_revisions",
            $"DELETE FROM identity_broker.model_provider_credential_write_claims WHERE credential_slot_id = '{slotId}'",
            "TRUNCATE identity_broker.model_provider_credential_write_claims",
            "CREATE TABLE identity_broker.runtime_must_not_create (id integer)",
        })
        {
            await using var forbidden = new NpgsqlCommand(statement, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        await using (var resetRole = new NpgsqlCommand("RESET ROLE", connection))
            await resetRole.ExecuteNonQueryAsync();

        foreach (var statement in new[]
        {
            "UPDATE identity_broker.model_provider_credential_write_claims SET actor_id = 'other'",
            "UPDATE identity_broker.model_provider_credential_write_claims SET state = 'Completed', " +
                "quarantined_at = NULL, completed_revision = 3, completed_at = now()",
            $"DELETE FROM identity_broker.model_provider_credential_write_claims WHERE credential_slot_id = '{slotId}'",
            "TRUNCATE identity_broker.model_provider_credential_write_claims",
        })
        {
            await using var guarded = new NpgsqlCommand(statement, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => guarded.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await using (var disableHead = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credentials
            SET state = 'Disabled', revision = 3, set_at = now()
            WHERE credential_slot_id = @slotId
            """, connection))
        {
            disableHead.Parameters.AddWithValue("slotId", slotId);
            await disableHead.ExecuteNonQueryAsync();
        }
        await using (var terminalHead = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credentials
            SET revision = 4, set_at = now()
            WHERE credential_slot_id = @slotId
            """, connection))
        {
            terminalHead.Parameters.AddWithValue("slotId", slotId);
            var terminalError = await Assert.ThrowsAsync<PostgresException>(() => terminalHead.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, terminalError.SqlState);
        }

        foreach (var statement in new[]
        {
            $"DELETE FROM identity_broker.model_provider_credentials WHERE credential_slot_id = '{slotId}'",
            "UPDATE identity_broker.model_provider_credential_revisions SET actor_id = 'other'",
            "DELETE FROM identity_broker.model_provider_credential_revisions",
            "TRUNCATE identity_broker.model_provider_credential_revisions",
        })
        {
            await using var guarded = new NpgsqlCommand(statement, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => guarded.ExecuteNonQueryAsync());
            var expectedSqlState = statement.StartsWith("TRUNCATE", StringComparison.Ordinal)
                ? "0A000"
                : PostgresErrorCodes.CheckViolation;
            Assert.Equal(expectedSqlState, error.SqlState);
        }
        await using (var historyCount = new NpgsqlCommand(
            "SELECT count(*) FROM identity_broker.model_provider_credential_revisions", connection))
            Assert.Equal(1L, await historyCount.ExecuteScalarAsync());
    }

    private static IdentityBrokerDbContext CreateDb(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .AddInterceptors(interceptors)
            .Options;
        return new IdentityBrokerDbContext(options);
    }

    private static ModelProviderCredentialStore Store(IdentityBrokerDbContext db) =>
        new(db, ModelProviderCredentialVaults.Create(ExistingVault, ByokVault), TimeProvider.System);

    private static ModelProviderCredentialIdentity Identity() =>
        new(Guid.NewGuid(), ModelProviderCredentialScope.Project, "project-1", "provider-1");

    private static RecordModelProviderCredentialReference Record(
        Guid operationId,
        long expectedRevision,
        ModelProviderCredentialIdentity identity,
        SecretRef reference) =>
        new(operationId, expectedRevision, identity.CredentialSlotId, identity.Scope, identity.ProjectId,
            identity.ProviderId, ModelProviderCredentialVaultSource.Existing, reference, "broker:actor");

    private static ClaimModelProviderCredentialWrite Claim(
        Guid operationId,
        long expectedRevision,
        ModelProviderCredentialIdentity identity) =>
        new(operationId, expectedRevision, identity.CredentialSlotId, identity.Scope,
            identity.ProjectId, identity.ProviderId, ModelProviderCredentialVaultSource.Existing,
            "provider-key", "broker:actor");

    private static DisableModelProviderCredential Disable(
        Guid operationId,
        long expectedRevision,
        ModelProviderCredentialIdentity identity) =>
        new(operationId, expectedRevision, identity.CredentialSlotId, identity.Scope,
            identity.ProjectId, identity.ProviderId, "broker:actor");

    private static SecretRef Ref(string name, char version) => new(name, new string(version, 32));

    private static async Task<ModelProviderCredentialReference> RecordInNewContextAsync(
        string connectionString,
        RecordModelProviderCredentialReference request)
    {
        await using var db = CreateDb(connectionString);
        return await Store(db).RecordReferenceAsync(request);
    }

    private static async Task<ModelProviderCredentialReference> DisableInNewContextAsync(
        string connectionString,
        DisableModelProviderCredential request)
    {
        await using var db = CreateDb(connectionString);
        return await Store(db).DisableAsync(request);
    }

    private static async Task<ModelProviderCredentialWriteClaimResult?> CaptureClaimAsync(
        string connectionString,
        ClaimModelProviderCredentialWrite request)
    {
        try
        {
            await using var db = CreateDb(connectionString);
            return await Store(db).ClaimWriteAsync(request);
        }
        catch (ModelProviderCredentialException exception) when (exception.Code == "credential_slot_conflict")
        {
            return null;
        }
    }

    private static async Task<ModelProviderCredentialReference?> CaptureAsync(
        Func<Task<ModelProviderCredentialReference>> action)
    {
        try
        {
            return await action();
        }
        catch (ModelProviderCredentialException exception) when (exception.Code == "credential_slot_conflict")
        {
            return null;
        }
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ModelProviderCredentialException>(action);
        Assert.Equal(code, exception.Code);
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class InjectedInsertFailure : Exception;

    private sealed class FailCredentialRevisionInsertOnce : DbCommandInterceptor
    {
        private int _failed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsRevisionInsert(command) && Interlocked.Exchange(ref _failed, 1) == 0)
                return ValueTask.FromException<InterceptionResult<DbDataReader>>(new InjectedInsertFailure());
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsRevisionInsert(command) && Interlocked.Exchange(ref _failed, 1) == 0)
                return ValueTask.FromException<InterceptionResult<int>>(new InjectedInsertFailure());
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static bool IsRevisionInsert(DbCommand command) =>
            command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains("model_provider_credential_revisions", StringComparison.Ordinal);
    }
}
