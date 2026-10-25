using System.Collections.Concurrent;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class SessionMaterialFailureRecoveryTests(SessionsPostgresFixture fixture) : IAsyncLifetime
{
    private const int AdvisoryLockClass = 2350;
    private const int AdvisoryLockObject = 235001;
    private readonly string _schema = "material_failure_" + Guid.NewGuid().ToString("N");
    private readonly ControlledObjects _objects = new();
    private readonly RuntimeRegistration _registration = Registration();
    private PostgresSessionsJournal _journal = null!;
    private PostgresSessionsProviderOptions _options = null!;
    private ClaimsPrincipal Principal => new(new ClaimsIdentity(
    [
        new("sub", _registration.Binding.ActorId), new("iss", _registration.Binding.ActorIssuer),
        new("project_id", "project"), new("run_id", "run")
    ], "validated-bearer"));
    private SdkSessionFacts Source => new(
        _registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(_registration.Binding),
        "1.0.11+native", "1.0.0", "accepted-model", "model", new string('a', 64), 1,
        "hosted-copilot", SdkMeterSources.CopilotNanoAiu, new string('b', 64), 1);
    private SessionMaterialStore Store => new(_journal, _objects);

    public async Task InitializeAsync()
    {
        _options = new("resource", fixture.DatabaseName, 1, _schema, "options", PollIntervalMilliseconds: 50);
        await EventsAndSessionsMigrator.MigrateAsync(fixture.DataSource, _schema);
        _journal = new(fixture.DataSource, _options);
        await _journal.CreateSessionAsync(Principal, "session", new SessionProviderBinding(
            "project", "run", NativePostgresSessionsProvider.ProviderId, new(1, 0, 0),
            1, "options", "resource", 1, SessionsCapabilities.All));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task LostObjectStoreResponseAfterBytesExistRecoversTheSameIdentityWithANewOwner()
    {
        var input = Request();
        _objects.AfterWrite = (_, _) => Task.FromException(new IOException("injected lost write response"));

        var failure = await Assert.ThrowsAsync<IOException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default));
        Assert.Equal("injected lost write response", failure.Message);
        var key = Assert.Single(_objects.Values.Keys);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);

        // A new owner instance reconciles the exact key, verifies its bytes, and commits the original event once.
        _objects.AfterWrite = null;
        _journal = new(fixture.DataSource, _options);
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        Assert.Equal(key, acknowledgment.Reference.Key);
        Assert.Equal(1, acknowledgment.Position);
        Assert.Equal(input.Bytes, (await Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, Historical, default)).Bytes);
        Assert.Equal(1, _objects.Writes);
        Assert.Single((await _journal.ReplayAsync(Principal, new("session"))).Events);
    }

    [Fact]
    public async Task DefiniteJournalRollbackAfterObjectWriteCanRetryFromANewOwner()
    {
        var input = Request();
        await CreateFailingJournalCommitTriggerAsync();

        // The deferred database trigger fails at commit, after immutable bytes already exist.
        var failure = await Assert.ThrowsAsync<PostgresException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default));
        Assert.Contains("injected journal commit failure", failure.MessageText);
        var key = Assert.Single(_objects.Values.Keys);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);

        await DropFailingJournalCommitTriggerAsync();
        _journal = new(fixture.DataSource, _options);
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        Assert.Equal(key, acknowledgment.Reference.Key);
        Assert.Equal(1, acknowledgment.Position);
        Assert.Equal(input.Bytes, (await Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, Historical, default)).Bytes);
        Assert.Equal(1, _objects.Writes);
        Assert.Single((await _journal.ReplayAsync(Principal, new("session"))).Events);
    }

    [Fact]
    public async Task CallerCancellationAfterCommitRecoversTheOriginalAcknowledgment()
    {
        var input = Request();
        using var cancellation = new CancellationTokenSource();
        var authorityChecks = 0;

        // The fifth owner check is after PostgreSQL commits, so cancellation models a lost response, not a rollback.
        Task CancelAfterCommit(CancellationToken token)
        {
            if (Interlocked.Increment(ref authorityChecks) == 5)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Current(token);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, CancelAfterCommit, cancellation.Token));
        Assert.Equal(5, authorityChecks);
        Assert.Single((await _journal.ReplayAsync(Principal, new("session"))).Events);
        Assert.Single(_objects.Values);

        // Reopen against the same database and require the retry to return the committed position without rewriting.
        _journal = new(fixture.DataSource, _options);
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        Assert.Equal(1, acknowledgment.Position);
        Assert.Equal(input.Bytes, (await Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, Historical, default)).Bytes);
        Assert.Equal(1, _objects.Writes);
        Assert.Single((await _journal.ReplayAsync(Principal, new("session"))).Events);

        var changed = await Assert.ThrowsAsync<SessionEventConflictException>(() => Store.WriteAsync(
            Principal, "session", input with { Bytes = "changed retry bytes"u8.ToArray() },
            _registration, Source, Current, default));
        Assert.NotNull(changed);
        Assert.Equal(1, _objects.Writes);
    }

    [Fact]
    public async Task RevocationAfterObjectWriteDeniesTheCommitAndEveryRetry()
    {
        var input = Request();
        var revoked = false;
        Task ValidateCurrentAuthority(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return revoked
                ? Task.FromException(new RuntimeAuthorizationException("runtime_registration_revoked"))
                : Task.CompletedTask;
        }
        _objects.AfterWrite = (_, _) =>
        {
            revoked = true;
            return Task.CompletedTask;
        };

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, ValidateCurrentAuthority, default));
        Assert.Equal("runtime_registration_revoked", failure.Code);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);
        Assert.Single(_objects.Values);

        // The restart does not restore authority; a retry is denied before it can read or replace the bytes.
        _journal = new(fixture.DataSource, _options);
        var readsBeforeRetry = _objects.Reads;
        var retry = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, ValidateCurrentAuthority, default));
        Assert.Equal("runtime_registration_revoked", retry.Code);
        Assert.Equal(readsBeforeRetry, _objects.Reads);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);
    }

    [Fact]
    public async Task ChangedExecutionFenceAfterObjectWriteRejectsTheOldRetry()
    {
        var input = Request();
        var currentFence = _registration.Binding.ExecutionFence;
        Task ValidateCurrentFence(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return currentFence == _registration.Binding.ExecutionFence
                ? Task.CompletedTask
                : Task.FromException(new RuntimeAuthorizationException("runtime_execution_fence_changed"));
        }
        _objects.AfterWrite = (_, _) =>
        {
            currentFence++;
            return Task.CompletedTask;
        };

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, ValidateCurrentFence, default));
        Assert.Equal("runtime_execution_fence_changed", failure.Code);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);

        // A new owner still rejects stale material after restart, even when supplied the newer registration.
        _journal = new(fixture.DataSource, _options);
        _objects.AfterWrite = null;
        var freshRegistration = _registration with
        {
            Binding = _registration.Binding with { ExecutionFence = currentFence }
        };
        var readsBeforeRetry = _objects.Reads;
        var retry = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(
            Principal, "session", input, freshRegistration, Source, Current, default));
        Assert.Equal("runtime_material_binding_invalid", retry.Code);
        Assert.Equal(readsBeforeRetry, _objects.Reads);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);
    }

    [Fact]
    public async Task ReferenceExpiryDuringAnAwaitedObjectReadPreventsDisclosure()
    {
        var input = Request();
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReadToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _objects.AfterRead = async (_, token) =>
        {
            readStarted.TrySetResult();
            await allowReadToFinish.Task.WaitAsync(token);
        };

        // Hold the storage response after the journal's first deadline check, then expire only this event reference.
        var read = Store.ReadAsync(Principal, "session", input.EventId, input.Kind, Historical, default);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await ExpireReferenceAndInventoryAsync(input.EventId, acknowledgment.Reference.Key);
        }
        finally
        {
            allowReadToFinish.TrySetResult();
        }

        var failure = await Assert.ThrowsAsync<SessionMaterialIntegrityException>(() => read);
        Assert.Equal("session_material_retention_expired", failure.Code);
        Assert.Contains(acknowledgment.Reference.Key, _objects.Values.Keys);
    }

    [Fact]
    public async Task CleanupCanPersistTombstoneAfterDeleteSucceededButItsFirstCommitWasLost()
    {
        var input = Request();
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        await ExpireReferenceAndInventoryAsync(input.EventId, acknowledgment.Reference.Key);
        var interrupted = 0;
        _objects.AfterDelete = (_, _) => Interlocked.Exchange(ref interrupted, 1) == 0
            ? Task.FromException(new IOException("injected cleanup interruption"))
            : Task.CompletedTask;
        var retention = new SessionObjectRetentionService(fixture.DataSource, _options, _objects);

        // The adapter removes bytes first; the exception then forces PostgreSQL to roll back the tombstone.
        var failure = await Assert.ThrowsAsync<IOException>(() => retention.CleanupAsync(default));
        Assert.Equal("injected cleanup interruption", failure.Message);
        Assert.DoesNotContain(acknowledgment.Reference.Key, _objects.Values.Keys);
        Assert.False(await IsInventoryDeletedAsync(acknowledgment.Reference.Key));
        Assert.Single((await _journal.ReplayAsync(Principal, new("session"))).Events);

        // A fresh cleanup owner treats an already-absent key as success and commits the durable tombstone.
        _journal = new(fixture.DataSource, _options);
        var recoveredRetention = new SessionObjectRetentionService(fixture.DataSource, _options, _objects);
        Assert.Equal(1, await recoveredRetention.CleanupAsync(default));
        Assert.True(await IsInventoryDeletedAsync(acknowledgment.Reference.Key));
        Assert.Single((await _journal.ReplayAsync(Principal, new("session"))).Events);
        var retry = await Assert.ThrowsAsync<SessionMaterialIntegrityException>(() => Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default));
        Assert.Equal("session_material_retention_expired", retry.Code);
        Assert.DoesNotContain(acknowledgment.Reference.Key, _objects.Values.Keys);
    }

    [Fact]
    public async Task CleanupLockWinnerPreventsAConcurrentReferenceFromBecomingDangling()
    {
        var input = Request();
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        await ExpireReferenceAndInventoryAsync(input.EventId, acknowledgment.Reference.Key);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDeleteToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _objects.AfterDelete = async (_, token) =>
        {
            deleteStarted.TrySetResult();
            await allowDeleteToFinish.Task.WaitAsync(token);
        };
        var retention = new SessionObjectRetentionService(fixture.DataSource, _options, _objects);
        var cleanup = retention.CleanupAsync(default);
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The cleanup transaction owns the inventory row while its object delete is paused.
        var append = AppendOrdinaryReferenceAsync(acknowledgment.Reference);
        try
        {
            await WaitForInventoryWriterWaitAsync();
        }
        finally
        {
            allowDeleteToFinish.TrySetResult();
        }

        Assert.Equal(1, await cleanup);
        var failure = await Assert.ThrowsAsync<SessionMaterialIntegrityException>(() => append);
        Assert.Equal("session_material_retention_expired", failure.Code);
        Assert.DoesNotContain(acknowledgment.Reference.Key, _objects.Values.Keys);
        Assert.True(await IsInventoryDeletedAsync(acknowledgment.Reference.Key));
        Assert.Equal(1, await CountEventsAsync());
        Assert.Equal(1, await CountReferencesAsync(acknowledgment.Reference.Key));
    }

    [Fact]
    public async Task WriterLockWinnerExtendsRetentionBeforeCleanupCanDeleteTheSharedKey()
    {
        var input = Request();
        var acknowledgment = await Store.WriteAsync(
            Principal, "session", input, _registration, Source, Current, default);
        await ExpireReferenceAndInventoryAsync(input.EventId, acknowledgment.Reference.Key);
        await CreateWriterPauseTriggerAsync();

        // Hold a transaction-scoped advisory lock so the journal writer pauses after locking inventory.
        await using var lockConnection = await fixture.DataSource.OpenConnectionAsync();
        await using (var acquireLock = new NpgsqlCommand(
            $"SELECT pg_advisory_lock({AdvisoryLockClass}, {AdvisoryLockObject})", lockConnection))
            await acquireLock.ExecuteScalarAsync();

        var append = AppendOrdinaryReferenceAsync(acknowledgment.Reference);
        try
        {
            await WaitForAdvisoryLockWaitAsync();
            Assert.Equal(0, await new SessionObjectRetentionService(
                fixture.DataSource, _options, _objects).CleanupAsync(default));
        }
        finally
        {
            await using var releaseLock = new NpgsqlCommand(
                $"SELECT pg_advisory_unlock({AdvisoryLockClass}, {AdvisoryLockObject})", lockConnection);
            await releaseLock.ExecuteScalarAsync();
        }

        var second = await append;
        Assert.Equal(2, second.Event.Position);
        Assert.Equal(0, await new SessionObjectRetentionService(
            fixture.DataSource, _options, _objects).CleanupAsync(default));
        Assert.Contains(acknowledgment.Reference.Key, _objects.Values.Keys);
        Assert.Equal(2, await CountEventsAsync());
        Assert.Equal(2, await CountReferencesAsync(acknowledgment.Reference.Key));

        // The new reference retains the bytes, but the expired first event remains unreadable.
        var expired = await Assert.ThrowsAsync<SessionMaterialIntegrityException>(() => Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, Historical, default));
        Assert.Equal("session_material_retention_expired", expired.Code);
    }

    private async Task CreateFailingJournalCommitTriggerAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE FUNCTION "{_schema}".fail_session_event_commit()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                RAISE EXCEPTION 'injected journal commit failure';
            END;
            $body$;
            CREATE CONSTRAINT TRIGGER fail_session_event_commit
            AFTER INSERT ON "{_schema}".session_events
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION "{_schema}".fail_session_event_commit()
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropFailingJournalCommitTriggerAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            DROP TRIGGER fail_session_event_commit ON "{_schema}".session_events;
            DROP FUNCTION "{_schema}".fail_session_event_commit()
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateWriterPauseTriggerAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE FUNCTION "{_schema}".pause_material_writer()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                IF NEW.retain_until IS DISTINCT FROM OLD.retain_until
                    AND NEW.retain_until > clock_timestamp() THEN
                    PERFORM pg_advisory_xact_lock({AdvisoryLockClass}, {AdvisoryLockObject});
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER pause_material_writer
            BEFORE UPDATE OF retain_until ON "{_schema}".session_object_inventory
            FOR EACH ROW
            EXECUTE FUNCTION "{_schema}".pause_material_writer()
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExpireReferenceAndInventoryAsync(Guid eventId, ObjectKey key)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var reference = new NpgsqlCommand($"""
            UPDATE "{_schema}".session_object_references
            SET retain_until = clock_timestamp() - interval '1 second'
            WHERE project_id = 'project' AND run_id = 'run' AND event_id = @event AND object_key = @key
            """, connection, transaction))
        {
            reference.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, eventId);
            reference.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key.Value);
            Assert.Equal(1, await reference.ExecuteNonQueryAsync());
        }
        await using (var inventory = new NpgsqlCommand($"""
            UPDATE "{_schema}".session_object_inventory
            SET retain_until = clock_timestamp() - interval '1 second'
            WHERE object_key = @key
            """, connection, transaction))
        {
            inventory.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key.Value);
            Assert.Equal(1, await inventory.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    private async Task<bool> IsInventoryDeletedAsync(ObjectKey key)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT deleted_at IS NOT NULL FROM "{_schema}".session_object_inventory
            WHERE object_key = @key
            """, connection);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key.Value);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountEventsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".session_events
            WHERE project_id = 'project' AND run_id = 'run'
            """, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountReferencesAsync(ObjectKey key)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".session_object_references WHERE object_key = @key
            """, connection);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task WaitForAdvisoryLockWaitAsync()
    {
        // Observe the database lock, not an elapsed-time guess, before starting cleanup.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory'
                        AND classid = @lock_class AND objid = @lock_object
                        AND objsubid = 2 AND NOT granted
                )
                """, connection);
            command.Parameters.AddWithValue("lock_class", NpgsqlDbType.Integer, AdvisoryLockClass);
            command.Parameters.AddWithValue("lock_object", NpgsqlDbType.Integer, AdvisoryLockObject);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
                return;
            await Task.Delay(20, timeout.Token);
        }
        throw new TimeoutException("The journal writer did not reach its controlled inventory-lock wait.");
    }

    private async Task WaitForInventoryWriterWaitAsync()
    {
        // This bounded poll confirms the competing writer is blocked on the cleanup-owned inventory row.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database()
                        AND wait_event_type = 'Lock'
                        AND query LIKE '%session_object_inventory%'
                )
                """, connection);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
                return;
            await Task.Delay(20, timeout.Token);
        }
        throw new TimeoutException("The reference writer did not wait for the cleanup-owned inventory row.");
    }

    private Task<SessionAppendResult> AppendOrdinaryReferenceAsync(SessionObjectReference reference) =>
        _journal.AppendAsync(Principal, "session", new(
            Guid.NewGuid(), 1, 2, new TurnSessionPayload("assistant",
                new SessionObjectReference(reference.Key, reference.Purpose, reference.ByteLength))));

    private SessionMaterialWriteRequest Request() =>
        new(1, Guid.NewGuid(), _registration.RuntimeInstanceId, _registration.Revision,
            _registration.Binding.ExecutionFence, SessionMaterialKind.TurnContent,
            "recovery proof turn bytes"u8.ToArray(), "assistant", Source.SdkVersion, Source.ModelId);

    private static Task Current(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static Task Historical(SessionMaterialBinding material, CancellationToken token)
    {
        Assert.Equal("tenant", material.TenantId);
        return Current(token);
    }

    private static RuntimeRegistration Registration() => new(
        Guid.NewGuid(), 1, new RuntimeBinding(
            "https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run", "session",
            "agent", "turn", 1, 1, 1, "context", new string('b', 64), 1, "environment", "placement", 1,
            "profile", new("https://runtime.test/configure"), new("https://orchestrator.test/internal/runtime/observations"))
        {
            WorkflowStepId = "step",
            EnvironmentCurrentFencingGeneration = 1, EnvironmentProviderFencingGeneration = 1,
            PlacementProviderId = "agent-sandbox", EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1, ModelSelectionReference = "accepted-model",
            ModelSourceMode = ModelSourceMode.HostedCopilot
        }, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class ControlledObjects : IObjectStore
    {
        public ConcurrentDictionary<ObjectKey, byte[]> Values { get; } = [];
        public int Writes;
        public int Reads;
        public Func<ObjectKey, CancellationToken, Task>? AfterWrite { get; set; }
        public Func<ObjectKey, CancellationToken, Task>? AfterRead { get; set; }
        public Func<ObjectKey, CancellationToken, Task>? AfterDelete { get; set; }

        public async Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (!Values.TryAdd(key, buffer.ToArray()))
                throw new InvalidOperationException("Objects are create-only.");
            Interlocked.Increment(ref Writes);
            if (AfterWrite is not null)
                await AfterWrite(key, cancellationToken);
        }

        public async Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            if (!Values.TryGetValue(key, out var bytes))
                return null;
            if (AfterRead is not null)
                await AfterRead(key, cancellationToken);
            var content = new MemoryStream(bytes, writable: false);
            return new(content, bytes.Length, content.Dispose);
        }

        public async Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = Values.TryRemove(key, out _);
            if (AfterDelete is not null)
                await AfterDelete(key, cancellationToken);
            return removed;
        }
    }
}

public sealed class SessionMaterialEndpointRetentionTests
{
    [Theory]
    [InlineData(true, "session_material_object_missing", 404)]
    [InlineData(true, "session_material_retention_expired", 404)]
    [InlineData(true, "session_material_digest_invalid", 503)]
    [InlineData(false, "session_material_retention_expired", 503)]
    public async Task OnlyOptionalCacheAbsenceAndExpiryBecomeNotFound(
        bool optionalCache, string errorCode, int expectedStatus)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Services.BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var result = await SessionMaterialEndpoints.ExecuteAsync(
            context,
            () => Task.FromException<int>(new SessionMaterialIntegrityException(errorCode)),
            optionalCache);

        // Execute the real result so this assertion covers the HTTP status, body, and no-store header.
        await result.ExecuteAsync(context);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, leaveOpen: true);
        Assert.Contains(errorCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task AuthorizationFailureDoesNotBecomeAnOptionalCacheMiss()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Services.BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        var result = await SessionMaterialEndpoints.ExecuteAsync(
            context,
            () => Task.FromException<int>(new RuntimeAuthorizationException("runtime_material_read_authority_denied")),
            missingCacheIsNotFound: true);

        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }
}
