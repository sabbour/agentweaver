using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Identity;
using Npgsql;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class SessionMaterialPostgresTests(SessionsPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "material_" + Guid.NewGuid().ToString("N");
    private readonly MemoryObjects _objects = new();
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

    [Theory]
    [InlineData(SessionMaterialKind.TurnContent)]
    [InlineData(SessionMaterialKind.SdkCache)]
    public async Task NarrowedPromptCapacityPersistsExactlyAcrossMaterialReplayAndRestart(SessionMaterialKind kind)
    {
        var registration = _registration with
        {
            Binding = _registration.Binding with { MaxPromptTokens = 2048 }
        };
        var source = Source with { MaxPromptTokens = 1024 };
        var input = Request(kind) with { MaxPromptTokens = source.MaxPromptTokens };
        foreach (var changed in new[] { input with { MaxPromptTokens = null }, input with { MaxPromptTokens = 2048 } })
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                Store.WriteAsync(Principal, "session", changed, registration, source, Current, default));
        Assert.Equal(0, _objects.Writes);
        var recorded = await Store.WriteAsync(Principal, "session", input, registration, source, Current, default);
        Assert.Equal(1024, recorded.Reference.Material!.MaxPromptTokens);
        _journal = new(fixture.DataSource, _options);
        Assert.Equal(recorded, await Store.WriteAsync(
            Principal, "session", input, registration, source, Current, default));
        var read = await Store.ReadAsync(Principal, "session", input.EventId, kind, Historical, default);
        Assert.Equal(recorded, read.Material);
        Assert.Equal(1024, read.Material.Reference.Material!.MaxPromptTokens);
        Assert.Equal(1, _objects.Writes);
    }

    [Theory]
    [InlineData(SessionMaterialKind.TurnContent)]
    [InlineData(SessionMaterialKind.SdkCache)]
    public async Task ExactMaterialRetriesAndRestartReturnOriginalRecordedAcknowledgment(SessionMaterialKind kind)
    {
        var input = Request(kind);
        var acknowledgments = await Task.WhenAll(
            Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default),
            Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default));
        Assert.Equal(acknowledgments[0], acknowledgments[1]);
        Assert.Equal(1, _objects.Writes);
        Assert.DoesNotContain(Encoding.UTF8.GetString(input.Bytes), acknowledgments[0].Reference.Key.Value);
        _journal = new(fixture.DataSource, _options);
        var replay = await Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default);
        Assert.Equal(acknowledgments[0], replay);
        var read = await Store.ReadAsync(Principal, "session", input.EventId, kind, Historical, default);
        Assert.Equal(replay, read.Material);
        Assert.Equal(input.Bytes, read.Bytes);
        var page = await _journal.ReplayAsync(Principal, new("session"));
        Assert.Equal(replay.Reference, Assert.Single(Assert.Single(page.Events).ObjectReferences).Reference);
        await Assert.ThrowsAsync<SessionEventConflictException>(() => Store.WriteAsync(Principal, "session",
            input with { Bytes = "different actual bytes"u8.ToArray() }, _registration, Source, Current, default));
        Assert.Equal(1, _objects.Writes);
    }

    [Fact]
    public async Task AuthorityLossAfterObjectWriteLeavesNoJournalReferenceAndExactRetryCanFinish()
    {
        var input = Request();
        _objects.AfterWrite = () => Task.CompletedTask;
        Task CurrentUntilWrite(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_objects.Writes > 0)
                throw new RuntimeAuthorizationException("authority_changed");
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(Principal, "session",
            input, _registration, Source, CurrentUntilWrite, default));
        Assert.Equal(1, _objects.Writes);
        Assert.Empty((await _journal.ReplayAsync(Principal, new("session"))).Events);
        await Assert.ThrowsAsync<SessionNotFoundException>(() => Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, Historical, default));
        var committed = await Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default);
        Assert.Equal(1, committed.Position);
        Assert.Equal(1, _objects.Writes);
    }

    [Fact]
    public async Task AuthorityLossAfterBlobReadPreventsDisclosureOfHistoricalMaterial()
    {
        var input = Request();
        await Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default);
        var checks = 0;
        Task ReadAuthority(SessionMaterialBinding _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++checks == 2)
                throw new RuntimeAuthorizationException("read_authority_changed");
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, ReadAuthority, default));
        Assert.Equal(2, checks);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("digest")]
    [InlineData("length")]
    public async Task RecordedMaterialMustPassOwnerDigestAndLengthChecksBeforeRead(string damage)
    {
        var input = Request();
        var ack = await Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default);
        if (damage == "missing")
            _objects.Values.TryRemove(ack.Reference.Key, out _);
        else
            _objects.Values[ack.Reference.Key] = damage == "digest"
                ? Enumerable.Repeat((byte)'x', input.Bytes.Length).ToArray() : [1];
        await Assert.ThrowsAsync<SessionMaterialIntegrityException>(() => Store.ReadAsync(
            Principal, "session", input.EventId, input.Kind, Historical, default));
    }

    [Fact]
    public async Task PublicJournalAppendCannotFabricateAnOwnerRecordedMaterialBinding()
    {
        var input = Request();
        var ack = await Store.WriteAsync(Principal, "session", input, _registration, Source, Current, default);
        await Assert.ThrowsAsync<SessionAccessDeniedException>(() => _journal.AppendAsync(Principal, "session",
            new(Guid.NewGuid(), 1, 2, new TurnSessionPayload("assistant", ack.Reference))));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(Principal, "session",
            Request() with { ExecutionFence = 2 }, _registration, Source, Current, default));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Store.WriteAsync(Principal, "session",
            Request() with { ModelId = "other-model" }, _registration, Source, Current, default));
        Assert.Equal(1, _objects.Writes);
    }

    private SessionMaterialWriteRequest Request(SessionMaterialKind kind = SessionMaterialKind.TurnContent) =>
        new(1, Guid.NewGuid(), _registration.RuntimeInstanceId, 1, 1, kind, "actual native turn content"u8.ToArray(),
            kind == SessionMaterialKind.TurnContent ? "assistant" : null, Source.SdkVersion, Source.ModelId);

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
            EnvironmentCurrentFencingGeneration = 1, EnvironmentProviderFencingGeneration = 1,
            PlacementProviderId = "agent-sandbox", EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1, ModelSelectionReference = "accepted-model",
            ModelSourceMode = ModelSourceMode.HostedCopilot
        }, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class MemoryObjects : IObjectStore
    {
        public ConcurrentDictionary<ObjectKey, byte[]> Values { get; } = [];
        public int Writes;
        public Func<Task>? AfterWrite { get; set; }
        public async Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (!Values.TryAdd(key, buffer.ToArray()))
                throw new InvalidOperationException("Objects are create-only.");
            Interlocked.Increment(ref Writes);
            if (AfterWrite is not null)
                await AfterWrite();
        }
        public Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Values.TryGetValue(key, out var bytes))
                return Task.FromResult<ObjectRead?>(null);
            var content = new MemoryStream(bytes, writable: false);
            return Task.FromResult<ObjectRead?>(new(content, bytes.Length, content.Dispose));
        }
        public Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryRemove(key, out _));
    }
}
