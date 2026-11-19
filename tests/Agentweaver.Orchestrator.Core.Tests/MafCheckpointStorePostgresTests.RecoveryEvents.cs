using Agentweaver.Orchestrator;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Theory]
    [InlineData("absent", "blocked")]
    [InlineData("sending", "indeterminate")]
    public async Task RecoveryEventsRejectImpossibleChildHoldsInCommittedEvents(string nativeState, string admissionType)
    {
        var fixture = await PrepareChildRecoveryAsync(nativeState: nativeState);
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        await fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
            _actor, _binding.Identity, recovered.OperationId,
            recovered.CheckpointRecovery! with { CurrentStep = "placement" }, CancellationToken.None);
        async Task<CheckpointRecoveryEventPage> ReadAsync() =>
            await fixture.Owner.ReadCheckpointRecoveryEventsAsync(
                _actor, _binding.Identity, recovered.OperationId, _selection, 100, null, CancellationToken.None);
        var page = await ReadAsync();
        Assert.EndsWith("_" + admissionType, page.Events[0].EventType);
        Assert.True(page.Events[0].Status.DispatchHeld);
        Assert.True(page.Events[1].Status.DispatchHeld);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".outbox_events
            SET payload = jsonb_set(payload, @path, to_jsonb(@held))
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("path", NpgsqlDbType.Array | NpgsqlDbType.Text,
            new[] { "result", "checkpointRecovery", "dispatchHeld" });
        command.Parameters.AddWithValue("held", false);
        command.Parameters.AddWithValue("id", recovered.OperationId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var badAdmission = await Assert.ThrowsAsync<CoordinationException>(ReadAsync);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, badAdmission.StatusCode);

        command.Parameters["held"].Value = true;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await using (var omit = new NpgsqlCommand($"""
            UPDATE "{_schema}".outbox_events
            SET payload = payload #- @path
            WHERE id = @id
            """, connection))
        {
            omit.Parameters.AddWithValue("path", NpgsqlDbType.Array | NpgsqlDbType.Text,
                new[] { "result", "checkpointRecovery", "dispatchHeld" });
            omit.Parameters.AddWithValue("id", recovered.OperationId);
            Assert.Equal(1, await omit.ExecuteNonQueryAsync());
            Assert.Null((await ReadAsync()).Events[0].Status.DispatchHeld);
        }
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        command.Parameters["path"].Value = new[] { "status", "dispatchHeld" };
        command.Parameters["held"].Value = false;
        command.Parameters["id"].Value = page.Events[1].EventId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var badProgress = await Assert.ThrowsAsync<CoordinationException>(ReadAsync);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, badProgress.StatusCode);
    }

    [Fact]
    public async Task RecoveryEventsReadCommittedProgressAndRejectMutatedEventType()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        var firstPage = await fixture.Owner.ReadCheckpointRecoveryEventsAsync(
            _actor, _binding.Identity, recovered.OperationId, _selection, 1, null, CancellationToken.None);
        var status = recovered.CheckpointRecovery! with { CurrentStep = "placement" };
        var updates = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
                _actor, _binding.Identity, recovered.OperationId, status, CancellationToken.None)));
        var updated = updates[0];
        var replay = updates[1];
        Assert.Equal(updated.OperationId, replay.OperationId);
        Assert.Equal(updated.CheckpointRecovery, replay.CheckpointRecovery);
        Assert.Single(firstPage.Events);
        Assert.False(firstPage.HasMore);
        var restarted = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        var page = await restarted.ReadCheckpointRecoveryEventsAsync(
            _actor, _binding.Identity, recovered.OperationId, _selection, 1, null, CancellationToken.None);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(recovered.OperationId, Assert.Single(page.Events).EventId);
        await fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
            _actor, _binding.Identity, recovered.OperationId,
            status with { CurrentStep = "bootstrap" }, CancellationToken.None);
        var next = await restarted.ReadCheckpointRecoveryEventsAsync(
            _actor, _binding.Identity, recovered.OperationId, _selection, 1, page.NextCursor, CancellationToken.None);
        var progress = Assert.Single(next.Events);
        Assert.Equal("placement", progress.Status.CurrentStep);
        Assert.Equal("pending", progress.Status.Phase);
        Assert.Equal("orchestrator.run.checkpoint_recovery_progress", progress.EventType);
        Assert.False(next.HasMore);
        var savedBeforeRead = await ReadRecoveryRowsAsync(recovered.OperationId);
        var fresh = await restarted.ReadCheckpointRecoveryEventsAsync(
            _actor, _binding.Identity, recovered.OperationId, _selection, 100, null, CancellationToken.None);
        Assert.Equal(savedBeforeRead, await ReadRecoveryRowsAsync(recovered.OperationId));
        Assert.Equal(3, fresh.Events.Count);
        Assert.Equal("bootstrap", fresh.Events[2].Status.CurrentStep);
        Assert.True(fresh.Watermark > page.Watermark);

        await using (var relayConnection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var delivery = new NpgsqlCommand($"""
            UPDATE "{_schema}".outbox_events SET delivered_at = clock_timestamp()
            WHERE id = @id
            """, relayConnection))
        {
            delivery.Parameters.AddWithValue("id", progress.EventId);
            Assert.Equal(1, await delivery.ExecuteNonQueryAsync());
        }
        var afterDelivery = await restarted.ReadCheckpointRecoveryEventsAsync(
            _actor, _binding.Identity, recovered.OperationId, _selection, 100, null, CancellationToken.None);
        Assert.Equal(fresh.Events, afterDelivery.Events);

        var beforeRollback = await ReadRecoveryRowsAsync(recovered.OperationId);
        await using (var guardConnection = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var rejectNext = new NpgsqlCommand($"""
                ALTER TABLE "{_schema}".outbox_events
                ADD CONSTRAINT test_reject_next_recovery_event
                CHECK (idempotency_key <> 'checkpoint-recovery:{recovered.OperationId:D}:2')
                """, guardConnection);
            await rejectNext.ExecuteNonQueryAsync();
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
                    _actor, _binding.Identity, recovered.OperationId,
                    status with { CurrentStep = "later" }, CancellationToken.None));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            await using var removeGuard = new NpgsqlCommand(
                $"ALTER TABLE \"{_schema}\".outbox_events DROP CONSTRAINT test_reject_next_recovery_event",
                guardConnection);
            await removeGuard.ExecuteNonQueryAsync();
        }
        Assert.Equal(beforeRollback, await ReadRecoveryRowsAsync(recovered.OperationId));

        await using (var headConnection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var changeHead = new NpgsqlCommand($"""
            UPDATE "{_schema}".outbox_streams SET last_sequence = @head WHERE stream_id = @stream
            """, headConnection))
        {
            changeHead.Parameters.AddWithValue("stream",
                $"coordination/{_binding.Identity.ProjectId}/{_binding.Identity.RunId}/execution");
            changeHead.Parameters.AddWithValue("head", 0L);
            Assert.Equal(1, await changeHead.ExecuteNonQueryAsync());
            var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
                restarted.ReadCheckpointRecoveryEventsAsync(
                    _actor, _binding.Identity, recovered.OperationId, _selection, 1, page.NextCursor, CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
            changeHead.Parameters["head"].Value = fresh.Watermark;
            Assert.Equal(1, await changeHead.ExecuteNonQueryAsync());
        }

        var invalid = await Assert.ThrowsAsync<CoordinationException>(() =>
            restarted.ReadCheckpointRecoveryEventsAsync(
                _actor, _binding.Identity, recovered.OperationId, _selection, 0, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        var badCursor = await Assert.ThrowsAsync<CoordinationException>(() =>
            restarted.ReadCheckpointRecoveryEventsAsync(
                _actor, _binding.Identity, recovered.OperationId, _selection, 1, "not-a-cursor", CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, badCursor.StatusCode);
        var foreignTenant = _selection with
        {
            Authorization = _selection.Authorization with { TenantId = "other-tenant" }
        };
        var hidden = await Assert.ThrowsAsync<CoordinationException>(() =>
            restarted.ReadCheckpointRecoveryEventsAsync(
                _actor, _binding.Identity, recovered.OperationId, foreignTenant, 1, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, hidden.StatusCode);

        var foreignActor = _actor with { Subject = Guid.NewGuid().ToString("D") };
        var forbidden = await Assert.ThrowsAsync<CoordinationException>(() =>
            restarted.ReadCheckpointRecoveryEventsAsync(
                foreignActor, _binding.Identity, recovered.OperationId, _selection, 1, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var mutation = new NpgsqlCommand($"""
            UPDATE "{_schema}".outbox_events SET event_type = 'orchestrator.run.checkpoint_recovery_ready'
            WHERE id = @id
            """, connection);
        mutation.Parameters.AddWithValue("id", progress.EventId);
        Assert.Equal(1, await mutation.ExecuteNonQueryAsync());
        var corrupt = await Assert.ThrowsAsync<CoordinationException>(() =>
            restarted.ReadCheckpointRecoveryEventsAsync(
                _actor, _binding.Identity, recovered.OperationId, _selection, 1, page.NextCursor, CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, corrupt.StatusCode);
    }

    [Fact]
    public async Task RecoveryEventsRejectRootFenceChangesDuringTheRead()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var sessionLock = new NpgsqlCommand($"""
            SELECT session_id FROM "{_schema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @session FOR UPDATE
            """, connection, transaction);
        sessionLock.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        sessionLock.Parameters.AddWithValue("run", _binding.Identity.RunId);
        sessionLock.Parameters.AddWithValue("session", _binding.Identity.SessionId);
        Assert.NotNull(await sessionLock.ExecuteScalarAsync());
        var pending = fixture.Owner.ReadCheckpointRecoveryEventsAsync(
            _actor, _binding.Identity, recovered.OperationId, _selection, 100, null, CancellationToken.None);
        await using var observer = await _fixture.DataSource.OpenConnectionAsync();
        await using var waiting = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE wait_event_type = 'Lock' AND query LIKE @schema AND query LIKE '%FOR UPDATE%'
            """, observer);
        waiting.Parameters.AddWithValue("schema", $"%{_schema}%");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((long)(await waiting.ExecuteScalarAsync(timeout.Token))! == 0)
            await Task.Delay(100, timeout.Token);
        await using var change = new NpgsqlCommand($"""
            UPDATE "{_schema}".coordination_sessions SET execution_fence = execution_fence + 1
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            """, connection, transaction);
        change.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        change.Parameters.AddWithValue("run", _binding.Identity.RunId);
        change.Parameters.AddWithValue("session", _binding.Identity.SessionId);
        Assert.Equal(1, await change.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
        var error = await Assert.ThrowsAsync<CoordinationException>(() => pending);
        Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
    }

    [Fact]
    public async Task RecoveryEventsHttpRechecksAuthorityForEveryPage()
    {
        var fixture = await PrepareChildRecoveryAsync();
        var recovered = await fixture.Owner.RecoverRunExecutionAsync(
            _actor, _binding.Identity.ProjectId, _binding.Identity.RunId, fixture.Request,
            CancellationToken.None, _selection, _executionStore);
        await fixture.Owner.UpdateCheckpointRecoveryProgressAsync(
            _actor, _binding.Identity, recovered.OperationId,
            recovered.CheckpointRecovery! with { CurrentStep = "placement" }, CancellationToken.None);
        var options = new OrchestratorOptions(
            _actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IAuthenticationService>(new RecoveryOwnerAuthentication(DateTimeOffset.UtcNow.AddMinutes(5)))
            .BuildServiceProvider();
        var handler = new ReviewSelectionAuthorityHandler(_selection.Selection, _actor.Issuer, _actor.Subject);
        using var http = new HttpClient(handler);
        var projects = new ProjectsRunSelectionClient(http, options, new ReviewNoSnapshotResolver());
        DefaultHttpContext Context()
        {
            var context = new DefaultHttpContext
            {
                RequestServices = services,
                User = new(new ClaimsIdentity(
                [
                    new Claim("sub", _actor.Subject), new Claim("iss", _actor.Issuer),
                    new Claim("aud", options.Audience), new Claim("aud", options.ProjectsAudience),
                    new Claim("project_id", _binding.Identity.ProjectId),
                    new Claim("run_id", _binding.Identity.RunId),
                    new Claim("scope", "api.read projects.orchestrator")
                ], "test"))
            };
            context.Request.Headers.Authorization = "Bearer recovery-test-token";
            context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
            context.Response.Body = new MemoryStream();
            return context;
        }
        var first = Context();
        var result = await CoordinationEndpoints.ReadCheckpointRecoveryEventsAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
            recovered.OperationId.ToString("D"), null, "1", first, options, projects, fixture.Owner, CancellationToken.None);
        await result.ExecuteAsync(first);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal("no-store", first.Response.Headers.CacheControl.ToString());
        first.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(first.Response.Body);
        var cursor = body.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        Assert.Equal(recovered.OperationId, body.RootElement.GetProperty("events")[0].GetProperty("eventId").GetGuid());

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var changeType = new NpgsqlCommand($"""
            UPDATE "{_schema}".outbox_events SET event_type = @type
            WHERE idempotency_key = @key
            """, connection);
        changeType.Parameters.AddWithValue("key", $"checkpoint-recovery:{recovered.OperationId:D}:0");
        changeType.Parameters.AddWithValue("type", "orchestrator.run.checkpoint_recovery_ready");
        Assert.Equal(1, await changeType.ExecuteNonQueryAsync());
        var damaged = Context();
        var damagedResult = await CoordinationEndpoints.ReadCheckpointRecoveryEventsAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
            recovered.OperationId.ToString("D"), cursor, "1", damaged, options, projects, fixture.Owner, CancellationToken.None);
        await damagedResult.ExecuteAsync(damaged);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, damaged.Response.StatusCode);
        damaged.Response.Body.Position = 0;
        using (var failure = await JsonDocument.ParseAsync(damaged.Response.Body))
            Assert.False(failure.RootElement.TryGetProperty("events", out _));
        changeType.Parameters["type"].Value = "orchestrator.run.checkpoint_recovery_progress";
        Assert.Equal(1, await changeType.ExecuteNonQueryAsync());

        var savedBeforeRace = await ReadRecoveryRowsAsync(recovered.OperationId);
        var raceHandler = new ReviewSelectionAuthorityHandler(_selection.Selection, _actor.Issuer, _actor.Subject);
        using (var raceHttp = new HttpClient(new RevokeBeforeReturnHandler(raceHandler)))
        {
            var raceProjects = new ProjectsRunSelectionClient(raceHttp, options, new ReviewNoSnapshotResolver());
            var race = Context();
            var deniedRace = await CoordinationEndpoints.ReadCheckpointRecoveryEventsAsync(
                _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
                recovered.OperationId.ToString("D"), null, "1", race, options, raceProjects, fixture.Owner, CancellationToken.None);
            await deniedRace.ExecuteAsync(race);
            Assert.Equal(StatusCodes.Status403Forbidden, race.Response.StatusCode);
            Assert.Equal("no-store", race.Response.Headers.CacheControl.ToString());
            race.Response.Body.Position = 0;
            using var raceBody = await JsonDocument.ParseAsync(race.Response.Body);
            Assert.False(raceBody.RootElement.TryGetProperty("events", out _));
        }
        Assert.Equal(savedBeforeRace, await ReadRecoveryRowsAsync(recovered.OperationId));

        handler.RevokeAcceptRunSelection();
        var second = Context();
        var denied = await CoordinationEndpoints.ReadCheckpointRecoveryEventsAsync(
            _binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId,
            recovered.OperationId.ToString("D"), cursor, "1", second, options, projects, fixture.Owner, CancellationToken.None);
        await denied.ExecuteAsync(second);
        Assert.Equal(StatusCodes.Status403Forbidden, second.Response.StatusCode);
        Assert.Equal("no-store", second.Response.Headers.CacheControl.ToString());
        second.Response.Body.Position = 0;
        using var deniedBody = await JsonDocument.ParseAsync(second.Response.Body);
        Assert.False(deniedBody.RootElement.TryGetProperty("events", out _));
    }

    [Fact]
    public async Task OriginalLegacyAdmissionIsReadWithItsSavedFieldsAndBindings()
    {
        using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "legacy-recovery-admission-567d151c.json")));
        var fixture = captured.RootElement;
        Assert.Equal("567d151c863b0e7c2639f5ada06fbaecc54dbaf1", fixture.GetProperty("sourceRevision").GetString());
        var payload = fixture.GetProperty("payload");
        var originalResult = payload.GetProperty("result");
        var originalStatus = originalResult.GetProperty("checkpointRecovery");
        foreach (var field in new[] { "operationId", "currentStep", "decisionStateVersion", "recoveryCheckpointHash" })
            Assert.False(originalStatus.TryGetProperty(field, out _));
        var originalSession = originalResult.GetProperty("session");
        var identity = new SessionIdentity(
            originalSession.GetProperty("projectId").GetString()!,
            originalSession.GetProperty("runId").GetString()!,
            originalSession.GetProperty("sessionId").GetString()!);
        var actor = new CoordinationActor(
            fixture.GetProperty("actorIssuer").GetString()!, fixture.GetProperty("actorSubject").GetString()!);
        var tenant = fixture.GetProperty("tenantId").GetString()!;
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            projectId = identity.ProjectId,
            runId = identity.RunId,
            projectRevision = 1,
            projectConfigurationRevision = 1,
            platformRuntimeRevision = 1,
            contextRevision = "context-v1",
            modelSelection = new { reference = "model:provider/alpha" }
        });
        var selection = _selection with
        {
            Selection = _selection.Selection with
            {
                ProjectId = identity.ProjectId, RunId = identity.RunId, Snapshot = snapshot
            },
            Authorization = _selection.Authorization with
            {
                Issuer = actor.Issuer, ActorId = actor.Subject, TenantId = tenant,
                BoundProjectId = identity.ProjectId, BoundRunId = identity.RunId
            }
        };
        var operationId = fixture.GetProperty("eventId").GetGuid();
        Assert.Equal(operationId, originalResult.GetProperty("operationId").GetGuid());
        var owner = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        await owner.AcceptRootAsync(actor, selection, identity.SessionId, CancellationToken.None);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand($"""
                UPDATE "{_schema}".accepted_runs
                SET execution_fence = @fence, execution_state = 'active'
                WHERE project_id = @project AND run_id = @run;
                UPDATE "{_schema}".coordination_sessions SET execution_fence = @fence
                WHERE project_id = @project AND run_id = @run AND session_id = @session;
                INSERT INTO "{_schema}".coordination_execution_operations
                    (project_id, run_id, session_id, operation_id, actor_issuer, actor_subject,
                     operation_kind, idempotency_key, request_hash, result)
                VALUES (@project, @run, @session, @operation, @issuer, @subject,
                        'recovery', 'retained-legacy', @hash, @result)
                """, connection, transaction);
            insert.Parameters.AddWithValue("fence", originalStatus.GetProperty("executionFence").GetInt64());
            insert.Parameters.AddWithValue("project", identity.ProjectId);
            insert.Parameters.AddWithValue("run", identity.RunId);
            insert.Parameters.AddWithValue("session", identity.SessionId);
            insert.Parameters.AddWithValue("operation", operationId);
            insert.Parameters.AddWithValue("issuer", actor.Issuer);
            insert.Parameters.AddWithValue("subject", actor.Subject);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, new string('a', 64));
            insert.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, originalResult.GetRawText());
            await insert.ExecuteNonQueryAsync();
            var stored = await new PostgresOutbox(_fixture.DataSource, _schema).EnqueueAsync(
                connection, transaction,
                new OutboxEvent(operationId, $"coordination/{identity.ProjectId}/{identity.RunId}/execution",
                    "retained-legacy-event", fixture.GetProperty("eventType").GetString()!,
                    fixture.GetProperty("eventVersion").GetInt32(), payload,
                    fixture.GetProperty("occurredAt").GetDateTimeOffset()), CancellationToken.None);
            Assert.Equal(fixture.GetProperty("sequence").GetInt64(), stored.Sequence);
            await transaction.CommitAsync();
        }

        var options = new OrchestratorOptions(actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        using var projectsHttp = new HttpClient(new ReviewSelectionAuthorityHandler(
            selection.Selection, actor.Issuer, actor.Subject));
        var projects = new ProjectsRunSelectionClient(projectsHttp, options, new ReviewNoSnapshotResolver());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(projects);
        builder.Services.AddSingleton(owner);
        builder.Services.AddSingleton<IAuthenticationService>(
            new RecoveryOwnerAuthentication(DateTimeOffset.UtcNow.AddMinutes(5)));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new(new ClaimsIdentity(
            [
                new Claim("sub", actor.Subject), new Claim("iss", actor.Issuer),
                new Claim("aud", options.Audience), new Claim("aud", options.ProjectsAudience),
                new Claim("project_id", identity.ProjectId), new Claim("run_id", identity.RunId),
                new Claim("scope", "api.read projects.orchestrator")
            ], "legacy-test"));
            await next();
        });
        app.UseAuthorization();
        var group = app.MapGroup("/api/projects/{projectId}/runs/{runId}/coordination").RequireAuthorization();
        CoordinationEndpoints.MapCheckpointRecoveryEventRoute(group);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "recovery-test-token");
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", tenant);
        var path = $"/api/projects/{identity.ProjectId}/runs/{identity.RunId}/coordination/sessions/" +
            $"{identity.SessionId}/checkpoint-recoveries/{operationId:D}/events";
        async Task<HttpResponseMessage> ReadAsync() => await client.GetAsync(path);
        using var response = await ReadAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var badLimit = await client.GetAsync(path + "?limit=not-an-integer");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        Assert.True(badLimit.Headers.CacheControl?.NoStore);
        using (var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync()))
        {
            var item = json.RootElement.GetProperty("events")[0];
            var status = item.GetProperty("status");
            Assert.Equal(operationId, item.GetProperty("operationId").GetGuid());
            Assert.Equal(operationId, item.GetProperty("eventId").GetGuid());
            Assert.Equal(fixture.GetProperty("sequence").GetInt64(), item.GetProperty("sequence").GetInt64());
            Assert.Equal(fixture.GetProperty("eventType").GetString(), item.GetProperty("eventType").GetString());
            Assert.Equal(fixture.GetProperty("eventVersion").GetInt32(), item.GetProperty("eventVersion").GetInt32());
            Assert.Equal(fixture.GetProperty("occurredAt").GetDateTimeOffset(), item.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal(identity.RunId, json.RootElement.GetProperty("session").GetProperty("runId").GetString());
            Assert.Equal(originalStatus.GetProperty("sourceCheckpointId").GetString(), status.GetProperty("sourceCheckpointId").GetString());
            Assert.Equal(originalStatus.GetProperty("sourceCheckpointRevision").GetInt64(), status.GetProperty("sourceCheckpointRevision").GetInt64());
            Assert.Equal(originalStatus.GetProperty("previousExecutionFence").GetInt64(), status.GetProperty("previousExecutionFence").GetInt64());
            Assert.Equal(originalStatus.GetProperty("executionFence").GetInt64(), status.GetProperty("executionFence").GetInt64());
            Assert.Equal(originalStatus.GetProperty("previousRuntimeInstanceId").GetGuid(), status.GetProperty("previousRuntimeInstanceId").GetGuid());
            Assert.Equal(originalStatus.GetProperty("previousRegistrationRevision").GetInt64(), status.GetProperty("previousRegistrationRevision").GetInt64());
            Assert.False(status.TryGetProperty("operationId", out _));
            Assert.False(status.TryGetProperty("currentStep", out _));
            Assert.False(status.TryGetProperty("decisionStateVersion", out _));
            Assert.False(status.TryGetProperty("recoveryCheckpointHash", out _));
        }

        var damaged = JsonNode.Parse(payload.GetRawText())!.AsObject();
        damaged["result"]!["checkpointRecovery"]!.AsObject().Remove("sourceCheckpointId");
        await UpdateCapturedPayloadAsync(operationId, damaged.ToJsonString());
        using var missing = await ReadAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        using (var failure = await JsonDocument.ParseAsync(await missing.Content.ReadAsStreamAsync()))
            Assert.False(failure.RootElement.TryGetProperty("events", out _));

        damaged = JsonNode.Parse(payload.GetRawText())!.AsObject();
        damaged["result"]!["previousExecutionFence"] = 999;
        await UpdateCapturedPayloadAsync(operationId, damaged.ToJsonString());
        using var conflict = await ReadAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, conflict.StatusCode);
        using var conflictingFailure = await JsonDocument.ParseAsync(await conflict.Content.ReadAsStreamAsync());
        Assert.False(conflictingFailure.RootElement.TryGetProperty("events", out _));
        await using var missingConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var delete = new NpgsqlCommand(
            $"DELETE FROM \"{_schema}\".outbox_events WHERE id = @id", missingConnection);
        delete.Parameters.AddWithValue("id", operationId);
        Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        using var noAdmission = await ReadAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noAdmission.StatusCode);
    }

    private async Task UpdateCapturedPayloadAsync(Guid id, string payload)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".outbox_events SET payload = @payload WHERE id = @id", connection);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task AssertRecoveryEventsHttpDenialsAsync(
        CoordinationOwnerStore owner, Guid firstOperation, Guid secondOperation, string childSession)
    {
        var savedBinding = _binding;
        var savedSelection = _selection;
        var savedStore = _store;
        var savedExecutionStore = _executionStore;
        var savedContext = _reviewSelectionContext;
        var savedModelPin = _reviewModelBindingPin;
        Guid foreignOperation;
        string foreignRun;
        try
        {
            var foreign = await PrepareChildRecoveryAsync();
            var recovery = await foreign.Owner.RecoverRunExecutionAsync(
                _actor, _binding.Identity.ProjectId, _binding.Identity.RunId,
                foreign.Request with { IdempotencyKey = $"foreign-recovery-{Guid.NewGuid():N}" },
                CancellationToken.None, _selection, _executionStore);
            foreignOperation = recovery.OperationId;
            foreignRun = _binding.Identity.RunId;
        }
        finally
        {
            _binding = savedBinding;
            _selection = savedSelection;
            _store = savedStore;
            _executionStore = savedExecutionStore;
            _reviewSelectionContext = savedContext;
            _reviewModelBindingPin = savedModelPin;
        }
        var options = new OrchestratorOptions(
            _actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        using var projectsHttp = new HttpClient(new TenantBoundRecoveryAuthorityHandler(
            new ReviewSelectionAuthorityHandler(_selection.Selection, _actor.Issuer, _actor.Subject)));
        var projects = new ProjectsRunSelectionClient(projectsHttp, options, new ReviewNoSnapshotResolver());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("recovery-test-signing-key-with-at-least-32-bytes"));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(bearer =>
        {
            bearer.MapInboundClaims = false;
            bearer.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = _actor.Issuer,
                ValidateAudience = true, ValidAudience = options.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
                ValidateLifetime = true
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(projects);
        builder.Services.AddSingleton(owner);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var group = app.MapGroup("/api/projects/{projectId}/runs/{runId}/coordination").RequireAuthorization();
        CoordinationEndpoints.MapCheckpointRecoveryEventRoute(group);
        await app.StartAsync();
        using var client = app.GetTestClient();
        string Token(string kind)
        {
            var scopes = kind switch
            {
                "no-read" => "projects.orchestrator",
                "no-orchestrator" => "api.read",
                _ => "api.read projects.orchestrator"
            };
            var token = new JwtSecurityToken(_actor.Issuer, null,
            [
                new Claim("sub", kind == "foreign-actor" ? Guid.NewGuid().ToString("D") : _actor.Subject),
                new Claim("aud", options.Audience),
                new Claim("aud", options.ProjectsAudience),
                new Claim("project_id", _binding.Identity.ProjectId),
                new Claim("run_id", _binding.Identity.RunId), new Claim("scope", scopes)
            ], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("owner"));
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant-1");
        string Path(string project, string run, string session, Guid operation) =>
            $"/api/projects/{project}/runs/{run}/coordination/sessions/{session}/checkpoint-recoveries/{operation:D}/events";
        var path = Path(_binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId, firstOperation);
        using var success = await client.GetAsync(path + "?limit=1");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var successBody = await JsonDocument.ParseAsync(await success.Content.ReadAsStreamAsync());
        var cursor = successBody.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        Assert.Equal(firstOperation, successBody.RootElement.GetProperty("events")[0].GetProperty("eventId").GetGuid());

        async Task DeniedAsync(string url, HttpStatusCode expected, string token = "owner", string tenant = "tenant-1")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(token));
            request.Headers.Add("X-Agentweaver-Tenant", tenant);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.False(json.RootElement.TryGetProperty("events", out _));
            Assert.DoesNotContain(firstOperation.ToString("D"), json.RootElement.GetRawText());
            Assert.DoesNotContain(secondOperation.ToString("D"), json.RootElement.GetRawText());
            Assert.DoesNotContain(foreignOperation.ToString("D"), json.RootElement.GetRawText());
        }

        await DeniedAsync(path, HttpStatusCode.Forbidden, "no-read");
        await DeniedAsync(path, HttpStatusCode.Forbidden, "no-orchestrator");
        await DeniedAsync(path, HttpStatusCode.Forbidden, "foreign-actor");
        await DeniedAsync(path, HttpStatusCode.Forbidden, tenant: "foreign-tenant");
        await DeniedAsync(Path("foreign-project", _binding.Identity.RunId, _binding.Identity.SessionId, firstOperation),
            HttpStatusCode.Forbidden);
        await DeniedAsync(Path(_binding.Identity.ProjectId, foreignRun, _binding.Identity.SessionId, foreignOperation),
            HttpStatusCode.Forbidden);
        await DeniedAsync(Path(_binding.Identity.ProjectId, _binding.Identity.RunId, childSession, firstOperation),
            HttpStatusCode.NotFound);
        await DeniedAsync(Path(_binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId, foreignOperation),
            HttpStatusCode.NotFound);
        var secondPath = Path(_binding.Identity.ProjectId, _binding.Identity.RunId, _binding.Identity.SessionId, secondOperation);
        await DeniedAsync(secondPath + "?limit=1&cursor=" + Uri.EscapeDataString(cursor), HttpStatusCode.BadRequest);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var readHash = new NpgsqlCommand($"""
            SELECT accepted_selection_hash FROM "{_schema}".accepted_runs WHERE project_id = @project AND run_id = @run
            """, connection);
        readHash.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        readHash.Parameters.AddWithValue("run", _binding.Identity.RunId);
        var hash = (string)(await readHash.ExecuteScalarAsync())!;
        await using var changeHash = new NpgsqlCommand($"""
            UPDATE "{_schema}".accepted_runs SET accepted_selection_hash = @hash WHERE project_id = @project AND run_id = @run
            """, connection);
        changeHash.Parameters.AddWithValue("hash", new string('a', 64));
        changeHash.Parameters.AddWithValue("project", _binding.Identity.ProjectId);
        changeHash.Parameters.AddWithValue("run", _binding.Identity.RunId);
        Assert.Equal(1, await changeHash.ExecuteNonQueryAsync());
        try
        {
            await DeniedAsync(path, HttpStatusCode.Conflict);
        }
        finally
        {
            changeHash.Parameters["hash"].Value = hash;
            Assert.Equal(1, await changeHash.ExecuteNonQueryAsync());
        }
    }

    private sealed class TenantBoundRecoveryAuthorityHandler(ReviewSelectionAuthorityHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants) && tenants.Single() != "tenant-1"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request })
                : _invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _invoker.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RevokeBeforeReturnHandler(ReviewSelectionAuthorityHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner);
        private int _authorityReads;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/authorization/context" &&
                Interlocked.Increment(ref _authorityReads) == 3)
                inner.RevokeAcceptRunSelection();
            return _invoker.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _invoker.Dispose();
            base.Dispose(disposing);
        }
    }

    private async Task<string> ReadRecoveryRowsAsync(Guid operationId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT result::text || progress::text || phase_version::text
                    FROM "{_schema}".coordination_execution_operations WHERE operation_id = @id) ||
                   (SELECT string_agg(row_to_json(events)::text, '' ORDER BY sequence)
                    FROM "{_schema}".outbox_events AS events WHERE stream_id = @stream)
            """, connection);
        command.Parameters.AddWithValue("id", operationId);
        command.Parameters.AddWithValue("stream",
            $"coordination/{_binding.Identity.ProjectId}/{_binding.Identity.RunId}/execution");
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
