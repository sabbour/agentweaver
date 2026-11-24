using System.Data.Common;
using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class ModelProviderCredentialStorePostgresTests(PostgresContainerFixture postgres)
{
    private const string ExistingVault = "https://agentweaver-kv.vault.azure.net/";
    private const string ByokVault = "https://agentweaver-byok.vault.azure.net/";

    [Fact]
    public async Task FingerprintsUseVersionedCanonicalEncodingAndPreserveLegacy()
    {
        var claim = new ClaimModelProviderCredentialWrite(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), 0,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ModelProviderCredentialScope.Platform, null, "provider-1",
            ModelProviderCredentialVaultSource.Existing, "provider-key", "broker:actor");
        var legacyConnection = await postgres.CreateMigratedDatabaseAsync();
        await using (var db = CreateDb(legacyConnection))
        {
            await Store(db).ClaimWriteAsync(claim);
            Assert.Equal("86f5cc36fb511286f8020bf2add28e12b41a156fb87e6f9071cc50a5f4d9a8bb",
                (await db.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).RequestFingerprint);
            await AssertCodeAsync("credential_slot_conflict", () => Store(db).ClaimPublicationWriteAsync(Publication(claim)));
        }
        var publicationConnection = await postgres.CreateMigratedDatabaseAsync();
        await using (var db = CreateDb(publicationConnection))
        {
            await Store(db).ClaimPublicationWriteAsync(Publication(claim));
            Assert.Equal("26aa7981f277a2abb84ab869ea89be6359204690c33e7077796b64ca9c4472de",
                (await db.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).RequestFingerprint);
            await AssertCodeAsync("credential_slot_conflict", () => Store(db).ClaimWriteAsync(claim));
        }
        var boundaries = new[]
        {
            (Actor: "actor1", Revision: 1L, Hash: "cfd87c19fe2e4ded1ee77e670a8a11ada27a8f30700213e6c37e160e9c184eb7"),
            (Actor: "actor", Revision: 11L, Hash: "e49878d0fab5be0e78984cc1d2055838a7e17ee02befcf7b952f33af454e2954"),
        };
        foreach (var boundary in boundaries)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync();
            await using var db = CreateDb(connectionString);
            var request = Publication(claim with { ActorId = boundary.Actor }) with { ExpectedProviderRevision = boundary.Revision };
            await Store(db).ClaimPublicationWriteAsync(request);
            Assert.Equal(boundary.Hash,
                (await db.ModelProviderCredentialWriteClaims.AsNoTracking().SingleAsync()).RequestFingerprint);
        }
    }

    [Fact]
    public async Task PublicationIdentityValidationRejectsInvalidInputsOnAcquisitionAndReplay()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var request = Publication(Claim(Guid.NewGuid(), 0, Identity()));
        await using var db = CreateDb(connectionString);
        var store = Store(db);
        var invalid = new[]
        {
            request with { ExpectedProviderRevision = 0 },
            request with { ExpectedProviderRevision = long.MaxValue },
            request with { Purpose = "" },
            request with { Issuer = "https://ISSUER.example/" },
            request with { Issuer = "https://issuer.example/#fragment" },
            request with { Issuer = "https://user@issuer.example/" },
            request with { Issuer = "http://issuer.example/" },
            request with { Issuer = "https://issuer.example/" + new string('a', 2049) },
            request with { Tenant = "" },
            request with { Tenant = "white space" },
            request with { Tenant = new string('a', 257) },
        };
        foreach (var item in invalid)
            await AssertCodeAsync("credential_request_invalid", () => store.ClaimPublicationWriteAsync(item));
        Assert.True((await store.ClaimPublicationWriteAsync(request)).Acquired);
        foreach (var item in invalid)
            await AssertCodeAsync("credential_request_invalid", () => store.ClaimPublicationWriteAsync(item));
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(request with { Tenant = "other" }));
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(request with { Issuer = "https://other.example/" }));
        const string prefix = "https://issuer.example/";
        var maximum = Publication(Claim(Guid.NewGuid(), 0, Identity() with { ProviderId = "provider-maximum" })) with
        {
            Issuer = prefix + new string('a', 2048 - prefix.Length),
            Tenant = new string('a', 256),
        };
        Assert.True((await store.ClaimPublicationWriteAsync(maximum)).Acquired);
        Assert.False((await store.ClaimPublicationWriteAsync(maximum)).Acquired);
    }

    [Theory]
    [InlineData(ModelProviderCredentialWriteClaimState.Claimed)]
    [InlineData(ModelProviderCredentialWriteClaimState.Quarantined)]
    [InlineData(ModelProviderCredentialWriteClaimState.PublicationPending)]
    public async Task UnresolvedPublicationBlocksDisableForItsSlotAndProvider(ModelProviderCredentialWriteClaimState state)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        await using var db = CreateDb(connectionString);
        var store = Store(db);
        await store.RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a')));
        var request = Publication(Claim(Guid.NewGuid(), 1, identity));
        await store.ClaimPublicationWriteAsync(request);
        if (state == ModelProviderCredentialWriteClaimState.Quarantined)
            await store.MarkPublicationWriteUncertainAsync(request);
        if (state == ModelProviderCredentialWriteClaimState.PublicationPending)
            await store.CompletePublicationWriteAsync(request, Ref("provider-key", 'b'));
        const long expectedRevision = 1;
        await AssertCodeAsync("credential_slot_conflict", () => store.DisableAsync(Disable(Guid.NewGuid(), expectedRevision, identity)));
        await AssertCodeAsync("credential_slot_conflict", () => store.DisableAsync(
            Disable(Guid.NewGuid(), expectedRevision, identity with { CredentialSlotId = Guid.NewGuid() })));
        Assert.Equal(expectedRevision, (await store.ReadCurrentAsync(identity)).Credential!.Revision);
    }

    [Fact]
    public async Task PublicationClaimAndDisableRaceHasOneWinner()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a')));
        var claim = Publication(Claim(Guid.NewGuid(), 1, identity));
        async Task<ModelProviderCredentialPublicationClaimResult?> TryClaim()
        {
            try
            {
                await using var db = CreateDb(connectionString);
                return await Store(db).ClaimPublicationWriteAsync(claim);
            }
            catch (ModelProviderCredentialException error) when (error.Code == "credential_slot_conflict") { return null; }
        }
        var acquire = TryClaim();
        var disable = CaptureAsync(() => DisableInNewContextAsync(connectionString, Disable(Guid.NewGuid(), 1, identity)));
        await Task.WhenAll(acquire, disable);
        Assert.NotEqual(await acquire is not null, await disable is not null);
    }

    [Fact]
    public async Task PublicationReferenceAndDifferentSlotClaimRacePreservesOneWinner()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var otherSlot = identity with { CredentialSlotId = Guid.NewGuid() };
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var competing = Publication(Claim(Guid.NewGuid(), 0, otherSlot));
        var reference = Ref("provider-key", 'a');
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ModelProviderCredentialPublicationClaimResult?> PublishAsync(ClaimModelProviderCredentialPublication candidate, bool complete)
        {
            await start.Task;
            try
            {
                await using var db = CreateDb(connectionString);
                var store = Store(db);
                var result = await store.ClaimPublicationWriteAsync(candidate);
                Assert.True(result.Acquired);
                return complete ? await store.CompletePublicationWriteAsync(candidate, reference) : result;
            }
            catch (ModelProviderCredentialException error) when (error.Code == "credential_slot_conflict") { return null; }
        }

        async Task<ModelProviderCredentialReference?> RecordAsync()
        {
            await start.Task;
            return await CaptureAsync(() => RecordInNewContextAsync(connectionString,
                Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'b'))));
        }

        var pendingTask = PublishAsync(request, true);
        var competingTask = PublishAsync(competing, false);
        var recordTask = RecordAsync();
        start.SetResult();
        await Task.WhenAll(pendingTask, competingTask, recordTask);
        var pending = await pendingTask;
        var acquired = await competingTask;
        var recorded = await recordTask;
        Assert.Equal(1, new[] { pending is not null, acquired is not null, recorded is not null }.Count(winner => winner));

        await using var verify = CreateDb(connectionString);
        var claims = await verify.ModelProviderCredentialWriteClaims.AsNoTracking().ToListAsync();
        var history = await verify.ModelProviderCredentialRevisions.AsNoTracking().ToListAsync();
        var store = Store(verify);
        if (pending is not null)
        {
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending, pending.Status);
            Assert.Empty(history);
            var claim = Assert.Single(claims);
            Assert.Equal(request.Claim.OperationId, claim.OperationId);
            Assert.Equal(reference.Id, claim.SecretName);
            Assert.Equal(reference.Version, claim.PendingSecretVersion);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending,
                (await store.ClaimPublicationWriteAsync(request)).Status);
            await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(competing));
            await AssertCodeAsync("credential_slot_conflict", () => store.RecordReferenceAsync(
                Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'c'))));
        }
        else if (acquired is not null)
        {
            Assert.Empty(history);
            Assert.Equal(competing.Claim.OperationId, Assert.Single(claims).OperationId);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Claimed,
                (await store.ClaimPublicationWriteAsync(competing)).Status);
            await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(request));
            await AssertCodeAsync("credential_slot_conflict", () => store.RecordReferenceAsync(
                Record(Guid.NewGuid(), 0, identity, reference)));
        }
        else
        {
            Assert.Empty(claims);
            Assert.Equal(recorded!.Reference.Version, Assert.Single(history).SecretVersion);
            await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(request));
            await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(competing));
        }
    }

    [Fact]
    public async Task PendingCommitKeepsReservationAgainstConcurrentReferenceAndProviderClaim()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var competing = Publication(Claim(Guid.NewGuid(), 0, identity with { CredentialSlotId = Guid.NewGuid() }));
        var reference = Ref("provider-key", 'a');
        await using (var db = CreateDb(connectionString))
            Assert.True((await Store(db).ClaimPublicationWriteAsync(request)).Acquired);

        var hold = new HoldPublicationTransition();
        await using var completingDb = CreateDb(connectionString, hold);
        var completion = Store(completingDb).CompletePublicationWriteAsync(request, reference);
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var competingDb = CreateDb(connectionString);
            var otherClaim = Store(competingDb).ClaimPublicationWriteAsync(competing);
            await using var recordDb = CreateDb(connectionString);
            await AssertCodeAsync("credential_slot_conflict", () => Store(recordDb).RecordReferenceAsync(
                Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'b'))));
            hold.Release.TrySetResult();
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending, (await completion).Status);
            await AssertCodeAsync("credential_slot_conflict", () => otherClaim);
        }
        finally
        {
            hold.Release.TrySetResult();
        }
        await using var verify = CreateDb(connectionString);
        var claim = Assert.Single(await verify.ModelProviderCredentialWriteClaims.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ModelProviderCredentialRevisions.AsNoTracking().ToListAsync());
        Assert.Equal(request.Claim.OperationId, claim.OperationId);
        Assert.Equal(ModelProviderCredentialWriteClaimState.PublicationPending, claim.State);
        Assert.Equal(reference.Version, claim.PendingSecretVersion);
    }

    [Fact]
    public async Task ConcurrentPublicationReplaysGrantOneWritePermission()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var request = Publication(Claim(Guid.NewGuid(), 0, Identity()));
        async Task<ModelProviderCredentialPublicationClaimResult> Acquire()
        {
            await using var db = CreateDb(connectionString);
            return await Store(db).ClaimPublicationWriteAsync(request);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Acquire()));
        Assert.Single(results, item => item.Acquired && item.Status == ModelProviderCredentialWriteClaimStatus.Acquired);
        Assert.Equal(4, results.Count(item => !item.Acquired && item.Status == ModelProviderCredentialWriteClaimStatus.Claimed));
    }

    [Theory]
    [InlineData(ModelProviderCredentialWriteClaimState.Claimed)]
    [InlineData(ModelProviderCredentialWriteClaimState.Quarantined)]
    [InlineData(ModelProviderCredentialWriteClaimState.PublicationPending)]
    [InlineData(ModelProviderCredentialWriteClaimState.Published)]
    public async Task RollbackWorksForLegacyClaimsButRejectsPublicationClaims(ModelProviderCredentialWriteClaimState state)
    {
        const string previous = "20261117221913_AddModelProviderCredentialWriteClaims";
        var legacyConnection = await postgres.CreateMigratedDatabaseAsync();
        var legacy = Claim(Guid.NewGuid(), 0, Identity());
        await using (var db = CreateDb(legacyConnection))
        {
            await Store(db).ClaimWriteAsync(legacy);
            await db.GetService<IMigrator>().MigrateAsync(previous);
            await db.GetService<IMigrator>().MigrateAsync();
        }
        await using (var db = CreateDb(legacyConnection))
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined, (await Store(db).ClaimWriteAsync(legacy)).Status);

        var publicationConnection = await postgres.CreateMigratedDatabaseAsync();
        await using (var db = CreateDb(publicationConnection))
        {
            var store = Store(db);
            var request = Publication(Claim(Guid.NewGuid(), 0, Identity()));
            var reference = Ref("provider-key", 'a');
            await store.ClaimPublicationWriteAsync(request);
            if (state == ModelProviderCredentialWriteClaimState.Quarantined)
                await store.MarkPublicationWriteUncertainAsync(request);
            if (state is ModelProviderCredentialWriteClaimState.PublicationPending or ModelProviderCredentialWriteClaimState.Published)
                await store.CompletePublicationWriteAsync(request, reference);
            if (state == ModelProviderCredentialWriteClaimState.Published)
                await store.AcknowledgePublicationAsync(request, Receipt(request, reference));
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(previous));
            Assert.Equal(
                state is ModelProviderCredentialWriteClaimState.PublicationPending or ModelProviderCredentialWriteClaimState.Published
                    ? "credential_lifecycle_rollback_blocked"
                    : "credential_publication_journal_rollback_blocked",
                error.MessageText);
        }
    }

    [Fact]
    public async Task LegacyRollbackRestoresWidthIndexesAndConstraints()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var db = CreateDb(connectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261117221913_AddModelProviderCredentialWriteClaims");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var width = new NpgsqlCommand("""
            SELECT character_maximum_length FROM information_schema.columns
            WHERE table_schema = 'identity_broker' AND table_name = 'model_provider_credential_write_claims'
              AND column_name = 'state'
            """, connection))
            Assert.Equal(16, await width.ExecuteScalarAsync());
        await using (var columns = new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'identity_broker' AND table_name = 'model_provider_credential_write_claims'
              AND column_name IN ('expected_provider_revision', 'purpose', 'issuer', 'tenant',
                                  'published_provider_revision', 'published_configuration_hash', 'published_at')
            """, connection))
            Assert.Equal(0L, await columns.ExecuteScalarAsync());

        var indexes = new Dictionary<string, string>();
        await using (var query = new NpgsqlCommand("""
            SELECT a.attname, pg_get_expr(i.indpred, i.indrelid), i.indisunique
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0]
            WHERE i.indrelid = 'identity_broker.model_provider_credential_write_claims'::regclass
              AND i.indpred IS NOT NULL
            """, connection))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                Assert.True(reader.GetBoolean(2));
                indexes.Add(reader.GetString(0), reader.GetString(1));
            }
        Assert.Equal(new[] { "credential_slot_id", "provider_id" }, indexes.Keys.Order());
        foreach (var predicate in indexes.Values)
        {
            await using var query = new NpgsqlCommand($"""
                SELECT state FROM (VALUES ('Claimed'::varchar), ('Quarantined'::varchar),
                    ('Completed'::varchar), ('PublicationPending'::varchar), ('Published'::varchar)) AS sample(state)
                WHERE {predicate}
                """, connection);
            await using var reader = await query.ExecuteReaderAsync();
            var reserved = new List<string>();
            while (await reader.ReadAsync())
                reserved.Add(reader.GetString(0));
            Assert.Equal(new[] { "Claimed", "Quarantined" }, reserved);
        }

        var checks = new Dictionary<string, string>();
        await using (var query = new NpgsqlCommand("""
            SELECT conname, pg_get_expr(conbin, conrelid) FROM pg_constraint
            WHERE conrelid = 'identity_broker.model_provider_credential_write_claims'::regclass AND contype = 'c'
            """, connection))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                checks.Add(reader.GetString(0), reader.GetString(1));
        Assert.Equal(new[] { "byok_name", "fingerprint", "outcome", "revision", "scope_project",
            "secret_name", "state", "vault_source" }.Select(name => "ck_model_provider_credential_write_claims_" + name).Order(),
            checks.Keys.Order());
        var stateCheck = checks["ck_model_provider_credential_write_claims_state"];
        await using (var query = new NpgsqlCommand($"""
            SELECT state FROM (VALUES ('Claimed'::varchar), ('Quarantined'::varchar), ('Completed'::varchar),
                ('PublicationPending'::varchar), ('Published'::varchar)) AS sample(state) WHERE {stateCheck}
            """, connection))
        await using (var reader = await query.ExecuteReaderAsync())
        {
            var allowed = new List<string>();
            while (await reader.ReadAsync())
                allowed.Add(reader.GetString(0));
            Assert.Equal(new[] { "Claimed", "Quarantined", "Completed" }, allowed);
        }
        var outcomeCheck = checks["ck_model_provider_credential_write_claims_outcome"];
        await using (var query = new NpgsqlCommand($"""
            SELECT label FROM (VALUES
                ('claimed', 'Claimed', NULL::timestamptz, NULL::bigint, NULL::timestamptz),
                ('bad_claimed', 'Claimed', now(), NULL::bigint, NULL::timestamptz),
                ('quarantined', 'Quarantined', now(), NULL::bigint, NULL::timestamptz),
                ('completed', 'Completed', NULL::timestamptz, 1::bigint, now()),
                ('bad_completed', 'Completed', NULL::timestamptz, NULL::bigint, now()),
                ('published', 'Published', NULL::timestamptz, 1::bigint, now())
            ) AS sample(label, state, quarantined_at, completed_revision, completed_at) WHERE {outcomeCheck}
            """, connection))
        await using (var reader = await query.ExecuteReaderAsync())
        {
            var allowed = new List<string>();
            while (await reader.ReadAsync())
                allowed.Add(reader.GetString(0));
            Assert.Equal(new[] { "claimed", "quarantined", "completed" }, allowed);
        }
        await using (var foreignKey = new NpgsqlCommand("""
            SELECT count(*) FROM pg_constraint
            WHERE conrelid = 'identity_broker.model_provider_credential_write_claims'::regclass AND contype = 'f'
              AND pg_get_constraintdef(oid) LIKE '%(credential_slot_id, completed_revision)%model_provider_credential_revisions%'
            """, connection))
            Assert.Equal(1L, await foreignKey.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EmptyStateLifecycleRollbackRestoresPriorWriteClaimGuard()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var db = CreateDb(connectionString))
            await db.GetService<IMigrator>().MigrateAsync("20261119125557_AddCredentialPublicationJournal");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var fnQuery = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_functiondef(p.oid)
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'identity_broker'
              AND p.proname = 'guard_model_provider_credential_write_claim'
            """,
            connection);
        var fnSource = (string)(await fnQuery.ExecuteScalarAsync())!;
        Assert.DoesNotContain("dispatched_at", fnSource);
        Assert.DoesNotContain("pending_secret_version", fnSource);
        Assert.DoesNotContain("cancelled_at", fnSource);
    }

    [Fact]
    public async Task LegacyStatesAndFingerprintsSurviveMigrationRoundTrip()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var claimedIdentity = Identity() with { ProviderId = "provider-claimed" };
        var quarantineIdentity = Identity() with { ProviderId = "provider-quarantined" };
        var completedIdentity = Identity() with { ProviderId = "provider-completed" };
        var claimed = Claim(Guid.NewGuid(), 0, claimedIdentity);
        var quarantined = Claim(Guid.NewGuid(), 0, quarantineIdentity);
        var completed = Claim(Guid.NewGuid(), 0, completedIdentity);
        var reference = Ref("provider-key", 'a');
        Dictionary<Guid, string> fingerprints;
        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            await store.ClaimWriteAsync(claimed);
            await store.ClaimWriteAsync(quarantined);
            await store.MarkWriteUncertainAsync(quarantined);
            await store.ClaimWriteAsync(completed);
            await store.CompleteWriteAsync(Record(completed.OperationId, 0, completedIdentity, reference));
            fingerprints = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                .ToDictionaryAsync(row => row.OperationId, row => row.RequestFingerprint);
            await db.GetService<IMigrator>().MigrateAsync("20261117221913_AddModelProviderCredentialWriteClaims");
            await db.GetService<IMigrator>().MigrateAsync();
        }
        await using var verify = CreateDb(connectionString);
        var replay = Store(verify);
        var after = await verify.ModelProviderCredentialWriteClaims.AsNoTracking()
            .ToDictionaryAsync(row => row.OperationId, row => row.RequestFingerprint);
        Assert.Equal(fingerprints.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined, (await replay.ClaimWriteAsync(claimed)).Status);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined, (await replay.ClaimWriteAsync(quarantined)).Status);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Completed, (await replay.ClaimWriteAsync(completed)).Status);
        Assert.Equal(reference, (await replay.ReadCurrentAsync(completedIdentity)).Credential!.Reference);
        Assert.Equal(reference, (await replay.ReadPinnedAsync(completedIdentity, reference)).Credential!.Reference);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task DirectClaimTransitionRequiresMatchingKindAndHistory(bool publication, bool historyMatches)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var claim = Claim(Guid.NewGuid(), 0, identity);
        await using var db = CreateDb(connectionString);
        var store = Store(db);
        if (publication)
            await store.ClaimPublicationWriteAsync(Publication(claim));
        else
            await store.ClaimWriteAsync(claim);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            INSERT INTO identity_broker.model_provider_credentials
            (credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri, secret_name,
             current_version, state, revision, set_by, set_at)
            VALUES (@slot, 'Project', 'project-1', 'provider-1', 'Existing', @vault, 'provider-key',
             @version, 'Enabled', 1, 'broker:actor', now());
            INSERT INTO identity_broker.model_provider_credential_revisions
            (credential_slot_id, revision, scope, project_id, provider_id, vault_source, vault_uri, secret_name,
             secret_version, state, mutation_kind, operation_id, request_fingerprint, actor_id, recorded_at)
            VALUES (@slot, 1, 'Project', 'project-1', 'provider-1', 'Existing', @vault, 'provider-key',
             @version, 'Enabled', 'RecordReference', @operation, @fingerprint, 'broker:actor', now())
            """, connection))
        {
            setup.Parameters.AddWithValue("slot", claim.CredentialSlotId);
            setup.Parameters.AddWithValue("vault", ExistingVault);
            setup.Parameters.AddWithValue("version", new string('a', 32));
            setup.Parameters.AddWithValue("operation", historyMatches ? claim.OperationId : Guid.NewGuid());
            setup.Parameters.AddWithValue("fingerprint", new string('a', 64));
            await setup.ExecuteNonQueryAsync();
        }
        var invalidState = publication ? "Completed" : "PublicationPending";
        await using (var invalid = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET state = @state, completed_revision = 1, completed_at = now()
            WHERE operation_id = @operation
            """, connection))
        {
            invalid.Parameters.AddWithValue("state", invalidState);
            invalid.Parameters.AddWithValue("operation", claim.OperationId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("credential_write_claim_transition_invalid", error.MessageText);
        }
        var validSql = publication
            ? "UPDATE identity_broker.model_provider_credential_write_claims SET state = 'PublicationPending', pending_secret_version = repeat('a', 32) WHERE operation_id = @operation"
            : "UPDATE identity_broker.model_provider_credential_write_claims SET state = 'Completed', completed_revision = 1, completed_at = now() WHERE operation_id = @operation";
        await using (var valid = new NpgsqlCommand(validSql, connection))
        {
            valid.Parameters.AddWithValue("operation", claim.OperationId);
            if (!publication && !historyMatches)
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => valid.ExecuteNonQueryAsync());
                Assert.Equal("credential_write_claim_transition_invalid", error.MessageText);
                return;
            }
            Assert.Equal(1, await valid.ExecuteNonQueryAsync());
        }
        await using var terminal = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET completed_at = completed_at WHERE operation_id = @operation
            """, connection);
        terminal.Parameters.AddWithValue("operation", claim.OperationId);
        var terminalError = await Assert.ThrowsAsync<PostgresException>(() => terminal.ExecuteNonQueryAsync());
        Assert.Equal("credential_write_claim_transition_invalid", terminalError.MessageText);
    }

    [Fact]
    public async Task PublicationReservesUntilMatchingReceiptAndReplaysPublishedOutcome()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var reference = Ref("provider-key", 'a');
        await using var db = CreateDb(connectionString);
        var store = Store(db);
        Assert.True((await store.ClaimPublicationWriteAsync(request)).Acquired);
        var claimedReplay = await store.ClaimPublicationWriteAsync(request);
        Assert.False(claimedReplay.Acquired);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Claimed, claimedReplay.Status);
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimWriteAsync(request.Claim));
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(
            request with { ExpectedProviderRevision = 12 }));

        var pending = await store.CompletePublicationWriteAsync(request, reference);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending, pending.Status);
        Assert.Equal(reference, pending.Credential!.Reference);
        Assert.Equal(reference, (await store.ReadPinnedAsync(identity, reference)).Credential!.Reference);
        await AssertCodeAsync("credential_slot_conflict", () => store.RecordReferenceAsync(
            Record(request.Claim.OperationId, 0, identity, reference)));
        await AssertCodeAsync("credential_slot_conflict", () => store.CompleteWriteAsync(
            Record(request.Claim.OperationId, 0, identity, reference)));
        await AssertCodeAsync("credential_slot_conflict", () => store.DisableAsync(Disable(Guid.NewGuid(), 1, identity)));
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(
            Publication(Claim(Guid.NewGuid(), 1, identity))));

        var receipt = Receipt(request, reference);
        var invalidHash = new string('é', 64);
        var hashError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity_broker.model_provider_credential_write_claims SET state = 'Published', completed_revision = 1, completed_at = now(), published_provider_revision = {receipt.ProviderRevision}, published_configuration_hash = {invalidHash}, published_at = now() WHERE operation_id = {request.Claim.OperationId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, hashError.SqlState);
        var otherReference = Ref("provider-key", 'b');
        var changedReceipts = new[]
        {
            receipt with { OperationId = Guid.NewGuid() },
            receipt with { ProviderRevision = receipt.ProviderRevision + 1 },
            receipt with { ProjectId = "other", CredentialIdentity = receipt.CredentialIdentity with { ProjectId = "other" } },
            receipt with { ProviderId = "other", CredentialIdentity = receipt.CredentialIdentity with { ProviderId = "other" } },
            receipt with { CredentialIdentity = receipt.CredentialIdentity with { CredentialSlotId = Guid.NewGuid() } },
            receipt with { CredentialVersion = otherReference.Version, CredentialReference = otherReference },
            receipt with { CredentialReference = Ref("other-key", 'a') },
            receipt with { Scope = ModelProviderBindingScope.Platform, ProjectId = null,
                CredentialIdentity = receipt.CredentialIdentity with { Scope = ModelProviderBindingScope.Platform, ProjectId = null } },
        };
        foreach (var changed in changedReceipts)
            await AssertCodeAsync("credential_slot_conflict", () => store.AcknowledgePublicationAsync(request, changed));
        var published = await store.AcknowledgePublicationAsync(request, receipt);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Published, published.Status);
        Assert.Equal(receipt.ConfigurationHash, published.PublishedConfigurationHash);
        Assert.Equal(published, await store.AcknowledgePublicationAsync(request, receipt));
        var terminalError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity_broker.model_provider_credential_write_claims SET published_at = published_at WHERE operation_id = {request.Claim.OperationId}"));
        Assert.Equal("credential_write_claim_transition_invalid", terminalError.MessageText);
        await AssertCodeAsync("credential_slot_conflict", () => store.AcknowledgePublicationAsync(
            request, receipt with { ConfigurationHash = new string('b', 64) }));
        var replay = await store.CompletePublicationWriteAsync(request, reference);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Published, replay.Status);
        Assert.False(replay.Acquired);
        Assert.Equal(reference, replay.Credential!.Reference);
        Assert.Equal(1, await db.ModelProviderCredentialRevisions.CountAsync());
        Assert.Equal(2, (await store.DisableAsync(Disable(Guid.NewGuid(), 1, identity))).Revision);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("reference")]
    [InlineData("disable")]
    public async Task PublishedClaimReleasesReservationsForNewOperations(string operation)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var reference = Ref("provider-key", 'a');
        await using var db = CreateDb(connectionString);
        var store = Store(db);
        await store.ClaimPublicationWriteAsync(request);
        await store.CompletePublicationWriteAsync(request, reference);
        await store.AcknowledgePublicationAsync(request, Receipt(request, reference));
        switch (operation)
        {
            case "claim":
                Assert.True((await store.ClaimPublicationWriteAsync(Publication(Claim(Guid.NewGuid(), 1, identity)))).Acquired);
                break;
            case "reference":
                Assert.Equal(2, (await store.RecordReferenceAsync(
                    Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b')))).Revision);
                break;
            case "disable":
                Assert.Equal(2, (await store.DisableAsync(Disable(Guid.NewGuid(), 1, identity))).Revision);
                break;
        }
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(
            Publication(Claim(Guid.NewGuid(), 0, identity with { CredentialSlotId = Guid.NewGuid() }))));
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Published, (await store.ClaimPublicationWriteAsync(request)).Status);
    }

    [Fact]
    public async Task PendingVersionAndOlderVersionRemainReadableWithoutPublication()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var oldReference = Ref("provider-key", 'a');
        var pendingReference = Ref("provider-key", 'b');
        await using var db = CreateDb(connectionString);
        var store = Store(db);
        await store.RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, oldReference));
        var request = Publication(Claim(Guid.NewGuid(), 1, identity));
        await store.ClaimPublicationWriteAsync(request);
        var pending = await store.CompletePublicationWriteAsync(request, pendingReference);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending, pending.Status);
        Assert.Null(pending.PublishedProviderRevision);
        Assert.Equal(oldReference, (await store.ReadCurrentAsync(identity)).Credential!.Reference);
        Assert.Equal(oldReference, (await store.ReadPinnedAsync(identity, oldReference)).Credential!.Reference);
        Assert.Equal(pendingReference, (await store.ReadPinnedAsync(identity, pendingReference)).Credential!.Reference);
        Assert.Equal(ModelProviderCredentialPinnedStatus.NotFound,
            (await store.ReadPinnedAsync(identity, Ref("provider-key", 'c'))).Status);
    }

    [Fact]
    public async Task GenericReferenceConflictReplayCannotReturnConcurrentPublicationHistory()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var reference = Ref("provider-key", 'a');
        await using var db = CreateDb(connectionString, new PublishDuringConflictReplay(connectionString, request, reference));
        await AssertCodeAsync("credential_slot_conflict", () => Store(db).RecordReferenceAsync(
            Record(request.Claim.OperationId, 0, identity, reference)));
        await using var verify = CreateDb(connectionString);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending,
            (await Store(verify).ClaimPublicationWriteAsync(request)).Status);
        Assert.Equal(0, await verify.ModelProviderCredentialRevisions.CountAsync());
    }

    [Fact]
    public async Task PublicationClaimRollbackAndQuarantineNeverGrantAnotherWrite()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        await using (var db = CreateDb(connectionString))
            Assert.True((await Store(db).ClaimPublicationWriteAsync(request)).Acquired);
        await using (var db = CreateDb(connectionString, new FailCommandOnce("UPDATE", "model_provider_credential_write_claims")))
            await Assert.ThrowsAnyAsync<Exception>(() =>
                Store(db).CompletePublicationWriteAsync(request, Ref("provider-key", 'a')));
        await using var verify = CreateDb(connectionString);
        var store = Store(verify);
        Assert.Empty(await verify.ModelProviderCredentialHeads.ToListAsync());
        Assert.Empty(await verify.ModelProviderCredentialRevisions.ToListAsync());
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Claimed, (await store.ClaimPublicationWriteAsync(request)).Status);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined, (await store.MarkPublicationWriteUncertainAsync(request)).Status);
        await AssertCodeAsync("credential_slot_conflict", () => store.CompletePublicationWriteAsync(request, Ref("provider-key", 'a')));
        await AssertCodeAsync("credential_slot_conflict", () => store.ClaimPublicationWriteAsync(
            Publication(Claim(Guid.NewGuid(), 0, identity))));
    }

    [Theory]
    [InlineData("INSERT", "model_provider_credentials")]
    [InlineData("UPDATE", "model_provider_credential_write_claims")]
    public async Task PublicationTransactionRollsBackOnHeadOrClaimFailure(string verb, string table)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var reference = Ref("provider-key", 'a');
        await using (var db = CreateDb(connectionString))
            await Store(db).ClaimPublicationWriteAsync(request);
        await using (var db = CreateDb(connectionString, new FailCommandOnce(verb, table)))
        {
            if (table == "model_provider_credentials")
            {
                await Store(CreateDb(connectionString)).CompletePublicationWriteAsync(request, reference);
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    Store(db).AcknowledgePublicationAsync(request, Receipt(request, reference)));
                Assert.True(error is InjectedInsertFailure || error is DbUpdateException { InnerException: InjectedInsertFailure });
            }
            else
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    Store(db).CompletePublicationWriteAsync(request, reference));
                Assert.True(error is InjectedInsertFailure || error is DbUpdateException { InnerException: InjectedInsertFailure });
            }
        }
        await using var verify = CreateDb(connectionString);
        var store = Store(verify);
        Assert.Empty(await verify.ModelProviderCredentialHeads.ToListAsync());
        Assert.Empty(await verify.ModelProviderCredentialRevisions.ToListAsync());
        if (table == "model_provider_credentials")
        {
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending,
                (await store.ClaimPublicationWriteAsync(request)).Status);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Published,
                (await store.AcknowledgePublicationAsync(request, Receipt(request, reference))).Status);
        }
        else
        {
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Claimed,
                (await store.ClaimPublicationWriteAsync(request)).Status);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending,
                (await store.CompletePublicationWriteAsync(request, reference)).Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitResponseRequiresFreshClaimRead(bool afterCommit)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        var reference = Ref("provider-key", 'a');
        await using (var db = CreateDb(connectionString))
            await Store(db).ClaimPublicationWriteAsync(request);
        await using (var db = CreateDb(connectionString, new FailCommitOnce(afterCommit)))
            await Assert.ThrowsAsync<InjectedCommitFailure>(() => Store(db).CompletePublicationWriteAsync(request, reference));
        await using var verify = CreateDb(connectionString);
        var replay = await Store(verify).ClaimPublicationWriteAsync(request);
        Assert.False(replay.Acquired);
        Assert.Equal(afterCommit ? ModelProviderCredentialWriteClaimStatus.PublicationPending :
            ModelProviderCredentialWriteClaimStatus.Claimed, replay.Status);
        Assert.Equal(0, await verify.ModelProviderCredentialRevisions.CountAsync());
        Assert.Equal(afterCommit ? reference : null, replay.Credential?.Reference);
    }

    [Fact]
    public async Task DatabaseRejectsDirectTerminalInsertAndCompetingPendingReservations()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var request = Publication(Claim(Guid.NewGuid(), 0, identity));
        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            await store.ClaimPublicationWriteAsync(request);
            await store.CompletePublicationWriteAsync(request, Ref("provider-key", 'a'));
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var state in new[] { "Completed", "Quarantined", "PublicationPending", "Published" })
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO identity_broker.model_provider_credential_write_claims
                (operation_id, credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
                 secret_name, expected_revision, request_fingerprint, actor_id, state, claimed_at)
                VALUES (@operation, @slot, 'Project', 'project-1', @provider, 'Existing', @vault,
                 'provider-key', 0, @fingerprint, 'broker:actor', @state, now())
                """, connection);
            command.Parameters.AddWithValue("operation", Guid.NewGuid());
            command.Parameters.AddWithValue("slot", Guid.NewGuid());
            command.Parameters.AddWithValue("provider", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("vault", ExistingVault);
            command.Parameters.AddWithValue("fingerprint", new string('a', 64));
            command.Parameters.AddWithValue("state", state);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("credential_write_claim_initial_state_invalid", error.MessageText);
        }
        foreach (var sameSlot in new[] { true, false })
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO identity_broker.model_provider_credential_write_claims
                (operation_id, credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
                 secret_name, expected_revision, request_fingerprint, actor_id, state, claimed_at)
                VALUES (@operation, @slot, 'Project', 'project-1', @provider, 'Existing', @vault,
                 'provider-key', 0, @fingerprint, 'broker:actor', 'Claimed', now())
                """, connection);
            command.Parameters.AddWithValue("operation", Guid.NewGuid());
            command.Parameters.AddWithValue("slot", sameSlot ? identity.CredentialSlotId : Guid.NewGuid());
            command.Parameters.AddWithValue("provider", sameSlot ? "another-provider" : identity.ProviderId);
            command.Parameters.AddWithValue("vault", ExistingVault);
            command.Parameters.AddWithValue("fingerprint", new string('a', 64));
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
    }

    [Theory]
    [InlineData("NULL, 'credential-publication', 'https://issuer.example/', 'tenant'")]
    [InlineData("1, NULL, 'https://issuer.example/', 'tenant'")]
    [InlineData("1, 'other', 'https://issuer.example/', 'tenant'")]
    [InlineData("1, 'credential-publication', '', 'tenant'")]
    [InlineData("1, 'credential-publication', 'https://issuer.example/ ', 'tenant'")]
    [InlineData("1, 'credential-publication', 'https://issuer.example/é', 'tenant'")]
    [InlineData("1, 'credential-publication', 'https://issuer.example/', 'bad tenant'")]
    [InlineData("1, 'credential-publication', 'https://issuer.example/', NULL")]
    [InlineData("9223372036854775807, 'credential-publication', 'https://issuer.example/', 'tenant'")]
    public async Task DatabaseRejectsPartialOrInvalidPublicationIdentity(string identityTuple)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO identity_broker.model_provider_credential_write_claims
            (operation_id, credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
             secret_name, expected_revision, request_fingerprint, actor_id, state, claimed_at,
             expected_provider_revision, purpose, issuer, tenant)
            VALUES (@operation, @slot, 'Project', 'project-1', 'provider-1', 'Existing', @vault,
             'provider-key', 0, @fingerprint, 'broker:actor', 'Claimed', now(), {identityTuple})
            """, connection);
        command.Parameters.AddWithValue("operation", Guid.NewGuid());
        command.Parameters.AddWithValue("slot", Guid.NewGuid());
        command.Parameters.AddWithValue("vault", ExistingVault);
        command.Parameters.AddWithValue("fingerprint", new string('a', 64));
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_model_provider_credential_write_claims_publication_identity", error.ConstraintName);
    }

    private static ClaimModelProviderCredentialPublication Publication(ClaimModelProviderCredentialWrite claim) =>
        new(claim, 11, "credential-publication", "https://issuer.example/", "tenant-1");

    private static ModelProviderCredentialPublicationReceipt Receipt(
        ClaimModelProviderCredentialPublication request, SecretRef reference)
    {
        var claim = request.Claim;
        var scope = claim.Scope == ModelProviderCredentialScope.Platform
            ? ModelProviderBindingScope.Platform : ModelProviderBindingScope.Project;
        return new(claim.OperationId, scope, claim.ProjectId, claim.ProviderId, request.ExpectedProviderRevision + 1,
            reference.Version, new string('a', 64),
            new(claim.CredentialSlotId, scope, claim.ProjectId, claim.ProviderId), reference);
    }

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
        await VerifyPublicationAsRuntimeAsync(connection, runtimeRole);

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
            Assert.Equal(4L, await historyCount.ExecuteScalarAsync());
    }

    private static async Task VerifyPublicationAsRuntimeAsync(NpgsqlConnection connection, string runtimeRole)
    {
        await using (var currentRole = new NpgsqlCommand("SELECT current_user", connection))
            Assert.Equal(runtimeRole, await currentRole.ExecuteScalarAsync());
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema)).Options;
        await using var db = new IdentityBrokerDbContext(options);
        var store = Store(db);
        var identity = Identity() with { ProviderId = "provider-runtime-publication" };
        var publication = Publication(Claim(Guid.NewGuid(), 0, identity));
        var reference = Ref("provider-key", 'a');
        Assert.True((await store.ClaimPublicationWriteAsync(publication)).Acquired);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.PublicationPending,
            (await store.CompletePublicationWriteAsync(publication, reference)).Status);

        foreach (var state in new[] { "Completed", "Quarantined", "PublicationPending", "Published", "Claimed" })
        {
            var operationId = Guid.NewGuid();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO identity_broker.model_provider_credential_write_claims
                (operation_id, credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
                 secret_name, expected_revision, request_fingerprint, actor_id, state, claimed_at, quarantined_at,
                 completed_revision, completed_at, expected_provider_revision, purpose, issuer, tenant,
                 published_provider_revision, published_configuration_hash, published_at)
                SELECT @operation, credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
                       secret_name, expected_revision, request_fingerprint, actor_id, @state, claimed_at,
                       CASE WHEN @state = 'Quarantined' THEN now() END,
                       CASE WHEN @state IN ('Completed', 'PublicationPending', 'Published') THEN completed_revision END,
                       CASE WHEN @state IN ('Completed', 'PublicationPending', 'Published', 'Claimed') THEN now() END,
                       CASE WHEN @state = 'Completed' THEN NULL ELSE expected_provider_revision END,
                       CASE WHEN @state = 'Completed' THEN NULL ELSE purpose END,
                       CASE WHEN @state = 'Completed' THEN NULL ELSE issuer END,
                       CASE WHEN @state = 'Completed' THEN NULL ELSE tenant END,
                       CASE WHEN @state = 'Published' THEN expected_provider_revision + 1 END,
                       CASE WHEN @state = 'Published' THEN repeat('F', 64) END,
                       CASE WHEN @state = 'Published' THEN now() END
                FROM identity_broker.model_provider_credential_write_claims WHERE operation_id = @source
                """, connection);
            insert.Parameters.AddWithValue("operation", operationId);
            insert.Parameters.AddWithValue("state", state);
            insert.Parameters.AddWithValue("source", publication.Claim.OperationId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("credential_write_claim_initial_state_invalid", error.MessageText);
            await using var absent = new NpgsqlCommand("""
                SELECT count(*) FROM identity_broker.model_provider_credential_write_claims WHERE operation_id = @operation
                """, connection);
            absent.Parameters.AddWithValue("operation", operationId);
            Assert.Equal(0L, await absent.ExecuteScalarAsync());
        }

        await AssertRuntimeTransitionRejectedAsync(connection, publication.Claim.OperationId,
            "state = 'Published', published_provider_revision = 12, " +
            "published_configuration_hash = repeat('a', 64), published_at = now(), completed_revision = 2");
        await using (var invalidRevision = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET state = 'Published', completed_revision = 1, completed_at = now(),
                published_provider_revision = 13,
                published_configuration_hash = repeat('a', 64), published_at = now()
            WHERE operation_id = @operation
            """, connection))
        {
            invalidRevision.Parameters.AddWithValue("operation", publication.Claim.OperationId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => invalidRevision.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        var receipt = Receipt(publication, reference) with { ConfigurationHash = new string('F', 64) };
        var published = await store.AcknowledgePublicationAsync(publication, receipt);
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Published, published.Status);
        Assert.Equal(receipt.ConfigurationHash, published.PublishedConfigurationHash);
        await AssertRuntimeTransitionRejectedAsync(connection, publication.Claim.OperationId,
            "state = 'PublicationPending', completed_revision = NULL, completed_at = NULL, published_provider_revision = NULL, published_configuration_hash = NULL, published_at = NULL");

        var directClaim = Publication(Claim(Guid.NewGuid(), 0,
            Identity() with { ProviderId = "provider-runtime-direct" }));
        await store.ClaimPublicationWriteAsync(directClaim);
        await InsertRuntimeHistoryAsync(connection, directClaim.Claim);
        await AssertRuntimeTransitionRejectedAsync(connection, directClaim.Claim.OperationId,
            "state = 'Completed', completed_revision = 1, completed_at = now()");
        await using (var pending = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET state = 'PublicationPending', pending_secret_version = repeat('a', 32)
            WHERE operation_id = @operation
            """, connection))
        {
            pending.Parameters.AddWithValue("operation", directClaim.Claim.OperationId);
            Assert.Equal(1, await pending.ExecuteNonQueryAsync());
        }
        await using (var publish = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET state = 'Published', completed_revision = 1, completed_at = now(),
                published_provider_revision = 12,
                published_configuration_hash = repeat('a', 64), published_at = now()
            WHERE operation_id = @operation
            """, connection))
        {
            publish.Parameters.AddWithValue("operation", directClaim.Claim.OperationId);
            Assert.Equal(1, await publish.ExecuteNonQueryAsync());
        }
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Published,
            (await store.ClaimPublicationWriteAsync(directClaim)).Status);

        var uncertain = Publication(Claim(Guid.NewGuid(), 0,
            Identity() with { ProviderId = "provider-runtime-quarantined" }));
        await store.ClaimPublicationWriteAsync(uncertain);
        await store.MarkPublicationWriteUncertainAsync(uncertain);
        await InsertRuntimeHistoryAsync(connection, uncertain.Claim);
        await AssertRuntimeTransitionRejectedAsync(connection, uncertain.Claim.OperationId,
            "state = 'PublicationPending', quarantined_at = NULL, completed_revision = 1, completed_at = now()");
        Assert.Equal(ModelProviderCredentialWriteClaimStatus.Quarantined,
            (await store.ClaimPublicationWriteAsync(uncertain)).Status);

        const string issuerPrefix = "https://issuer.example/";
        var maximum = Publication(Claim(Guid.NewGuid(), 0, Identity() with { ProviderId = "provider-runtime-maximum" })) with
        {
            ExpectedProviderRevision = long.MaxValue - 1,
            Issuer = issuerPrefix + new string('a', 2048 - issuerPrefix.Length),
            Tenant = new string('z', 256),
        };
        Assert.True((await store.ClaimPublicationWriteAsync(maximum)).Acquired);
        await using var bounds = new NpgsqlCommand("""
            SELECT length(issuer), length(tenant), expected_provider_revision
            FROM identity_broker.model_provider_credential_write_claims WHERE operation_id = @operation
            """, connection);
        bounds.Parameters.AddWithValue("operation", maximum.Claim.OperationId);
        await using var reader = await bounds.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2048, reader.GetInt32(0));
        Assert.Equal(256, reader.GetInt32(1));
        Assert.Equal(long.MaxValue - 1, reader.GetInt64(2));
    }

    private static async Task InsertRuntimeHistoryAsync(NpgsqlConnection connection, ClaimModelProviderCredentialWrite claim)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO identity_broker.model_provider_credentials
            (credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri, secret_name,
             current_version, state, revision, set_by, set_at)
            VALUES (@slot, 'Project', 'project-1', @provider, 'Existing', @vault, 'provider-key',
             @version, 'Enabled', 1, 'broker:actor', now());
            INSERT INTO identity_broker.model_provider_credential_revisions
            (credential_slot_id, revision, scope, project_id, provider_id, vault_source, vault_uri, secret_name,
             secret_version, state, mutation_kind, operation_id, request_fingerprint, actor_id, recorded_at)
            VALUES (@slot, 1, 'Project', 'project-1', @provider, 'Existing', @vault, 'provider-key',
             @version, 'Enabled', 'RecordReference', @operation, @fingerprint, 'broker:actor', now())
            """, connection);
        insert.Parameters.AddWithValue("slot", claim.CredentialSlotId);
        insert.Parameters.AddWithValue("provider", claim.ProviderId);
        insert.Parameters.AddWithValue("vault", ExistingVault);
        insert.Parameters.AddWithValue("version", new string('a', 32));
        insert.Parameters.AddWithValue("operation", claim.OperationId);
        insert.Parameters.AddWithValue("fingerprint", new string('a', 64));
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task AssertRuntimeTransitionRejectedAsync(NpgsqlConnection connection, Guid operationId, string update)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE identity_broker.model_provider_credential_write_claims SET {update} WHERE operation_id = @operation
            """, connection);
        command.Parameters.AddWithValue("operation", operationId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("credential_write_claim_transition_invalid", error.MessageText);
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

    [Fact]
    public async Task DisablePendingFencesConcurrentWritesCompletesIdempotentlyAndBlocksInvalidRollback()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        var opRecord = Guid.NewGuid();
        var opDisable = Guid.NewGuid();
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(Record(opRecord, 0, identity, Ref("provider-key", 'a')));

        var claimDisable = new ClaimModelProviderCredentialDisable(
            opDisable, null, 1, identity.Scope, identity.ProjectId, identity.ProviderId,
            "broker:actor", "https://issuer.example/");

        await using (var db = CreateDb(connectionString))
        {
            var store = Store(db);
            var acquired = await store.ClaimDisableAsync(claimDisable);
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.DisablePending, acquired.Status);
            Assert.True(acquired.Acquired);

            await AssertCodeAsync("credential_slot_conflict", () =>
                store.ClaimWriteAsync(Claim(Guid.NewGuid(), 1, identity)));
            await AssertCodeAsync("credential_slot_conflict", () =>
                store.RecordReferenceAsync(Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b'))));
            await AssertCodeAsync("credential_slot_conflict", () =>
                store.ClaimDisableAsync(claimDisable with { OperationId = Guid.NewGuid() }));
        }

        // Concurrent same-operation completion returns deterministic Disabled replay.
        var complete = new CompleteModelProviderCredentialDisable(
            opDisable, 1, identity.CredentialSlotId, identity.Scope, identity.ProjectId,
            identity.ProviderId, "broker:actor",
            (await Store(CreateDb(connectionString)).ClaimDisableAsync(claimDisable)).RequestFingerprint,
            4, new string('b', 64), "tenant-1");
        var completions = await Task.WhenAll(
            Store(CreateDb(connectionString)).CompleteDisableAsync(complete),
            Store(CreateDb(connectionString)).CompleteDisableAsync(complete));
        Assert.All(completions, result =>
        {
            Assert.Equal(ModelProviderCredentialWriteClaimStatus.Disabled, result.Status);
            Assert.Equal(2, result.CompletedCredentialRevision);
            Assert.Equal(4, result.PublishedProviderRevision);
        });

        await using (var verify = CreateDb(connectionString))
        {
            Assert.Equal(2, await verify.ModelProviderCredentialRevisions.CountAsync());
            var claimRow = await verify.ModelProviderCredentialWriteClaims.AsNoTracking()
                .SingleAsync(row => row.OperationId == opDisable);
            Assert.Equal(ModelProviderCredentialWriteClaimState.Disabled, claimRow.State);
            Assert.Equal("tenant-1", claimRow.Tenant);
        }

        // Actual EF Core Down() blocks when Disabled row exists.
        await using (var downDb = CreateDb(connectionString))
        {
            var migrator = downDb.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
                migrator.MigrateAsync("20261120231929_AddCredentialLifecycleDispatchAndCancel"));
            var rollbackError = thrown as PostgresException ?? thrown.InnerException as PostgresException;
            Assert.NotNull(rollbackError);
            Assert.Equal("55000", rollbackError.SqlState);
            Assert.Contains("credential_disable_claims_rollback_blocked", rollbackError.MessageText);
        }

        // Trigger guard: reject direct DisablePending -> Disabled without matching Disable revision,
        // reject Disabled with NULL tenant, and reject changing tenant once committed.
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var mutateTenant = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET tenant = 'other-tenant'
            WHERE operation_id = @op
            """, conn))
        {
            mutateTenant.Parameters.AddWithValue("op", opDisable);
            var err = await Assert.ThrowsAsync<PostgresException>(() => mutateTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, err.SqlState);
            Assert.Equal("credential_write_claim_identity_immutable", err.MessageText);
        }

        await using (var insertWithTenant = new NpgsqlCommand("""
            INSERT INTO identity_broker.model_provider_credential_write_claims
            (operation_id, credential_slot_id, scope, project_id, provider_id, vault_source, vault_uri,
             secret_name, expected_revision, request_fingerprint, actor_id, state, claimed_at,
             pending_secret_version, purpose, issuer, tenant)
            VALUES (@op, @slot, 'Project', 'project-1', @provider, 'Existing', @vault,
             'provider-key', 1, @fp, 'broker:actor', 'DisablePending', now(),
             @ver, 'credential-disable', 'https://issuer.example/', 'tenant-1')
            """, conn))
        {
            insertWithTenant.Parameters.AddWithValue("op", Guid.NewGuid());
            insertWithTenant.Parameters.AddWithValue("slot", Guid.NewGuid());
            insertWithTenant.Parameters.AddWithValue("provider", Guid.NewGuid().ToString("N"));
            insertWithTenant.Parameters.AddWithValue("vault", ExistingVault);
            insertWithTenant.Parameters.AddWithValue("fp", new string('c', 64));
            insertWithTenant.Parameters.AddWithValue("ver", new string('d', 32));
            var err = await Assert.ThrowsAsync<PostgresException>(() => insertWithTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, err.SqlState);
        }

        // Actual EF Core Down() succeeds on a clean DB without DisablePending/Disabled rows.
        var cleanConnectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var cleanDb = CreateDb(cleanConnectionString))
        {
            var migrator = cleanDb.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261120231929_AddCredentialLifecycleDispatchAndCancel");
        }
    }

    [Fact]
    public async Task DisablePendingTriggerRejectsDirectDisabledTransitionWithoutMatchingDisableRevision()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a')));

        var opDisable = Guid.NewGuid();
        await using (var db = CreateDb(connectionString))
            await Store(db).ClaimDisableAsync(new ClaimModelProviderCredentialDisable(
                opDisable, null, 1, identity.Scope, identity.ProjectId, identity.ProviderId,
                "broker:actor", "https://issuer.example/"));

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var directDisable = new NpgsqlCommand("""
            UPDATE identity_broker.model_provider_credential_write_claims
            SET state = 'Disabled',
                completed_revision = 2,
                completed_at = now(),
                tenant = 'tenant-1',
                published_provider_revision = 4,
                published_configuration_hash = @hash,
                published_at = now()
            WHERE operation_id = @op
            """, conn);
        directDisable.Parameters.AddWithValue("op", opDisable);
        directDisable.Parameters.AddWithValue("hash", new string('e', 64));
        var err = await Assert.ThrowsAsync<PostgresException>(() => directDisable.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, err.SqlState);
        Assert.Equal("credential_write_claim_transition_invalid", err.MessageText);
    }

    [Fact]
    public async Task DisablePendingCancelRequiresResolvedTenantAndReleasesFence()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var identity = Identity();
        await using (var db = CreateDb(connectionString))
            await Store(db).RecordReferenceAsync(Record(Guid.NewGuid(), 0, identity, Ref("provider-key", 'a')));

        var opDisable = Guid.NewGuid();
        var claimDisable = new ClaimModelProviderCredentialDisable(
            opDisable, 3, 1, identity.Scope, identity.ProjectId, identity.ProviderId,
            "broker:actor", "https://issuer.example/");
        ModelProviderCredentialDisableClaimResult pending;
        await using (var db = CreateDb(connectionString))
            pending = await Store(db).ClaimDisableAsync(claimDisable);

        await using (var db = CreateDb(connectionString))
            await Store(db).CancelDisableClaimAsync(opDisable, "tenant-1", pending.RequestFingerprint);

        await using (var db = CreateDb(connectionString))
        {
            var rotated = await Store(db).RecordReferenceAsync(
                Record(Guid.NewGuid(), 1, identity, Ref("provider-key", 'b')));
            Assert.Equal(2, rotated.Revision);
        }
    }

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
    private sealed class InjectedCommitFailure : Exception;

    private sealed class HoldPublicationTransition : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("model_provider_credential_write_claims", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class PublishDuringConflictReplay(
        string connectionString,
        ClaimModelProviderCredentialPublication request,
        SecretRef reference) : DbCommandInterceptor
    {
        private int _stage;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("model_provider_credential_revisions", StringComparison.Ordinal))
            {
                if (_stage == 0 && eventData.Context!.Database.CurrentTransaction is not null)
                {
                    _stage = 1;
                    throw ModelProviderCredentialException.Conflict();
                }
                if (_stage == 1 && eventData.Context!.Database.CurrentTransaction is null)
                {
                    _stage = 2;
                    await using var db = CreateDb(connectionString);
                    var store = Store(db);
                    await store.ClaimPublicationWriteAsync(request, cancellationToken);
                    await store.CompletePublicationWriteAsync(request, reference, cancellationToken);
                }
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FailCommitOnce(bool afterCommit) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            afterCommit ? base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken) :
                ValueTask.FromException<InterceptionResult>(new InjectedCommitFailure());

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            afterCommit ? Task.FromException(new InjectedCommitFailure()) :
                base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    private sealed class FailCommandOnce(string verb, string table) : DbCommandInterceptor
    {
        private int _failed;

        private bool ShouldFail(DbCommand command) =>
            command.CommandText.Contains(verb, StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains(table, StringComparison.Ordinal) &&
            Interlocked.Exchange(ref _failed, 1) == 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            ShouldFail(command) ? ValueTask.FromException<InterceptionResult<DbDataReader>>(new InjectedInsertFailure()) :
            base.ReaderExecutingAsync(command, eventData, result, cancellationToken);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ShouldFail(command) ? ValueTask.FromException<InterceptionResult<int>>(new InjectedInsertFailure()) :
            base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

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
