using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NpgsqlTypes;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class SessionsJournalPostgresTests : IAsyncLifetime
{
    private readonly SessionsPostgresFixture _fixture;
    private readonly string _schema = "sessions_" + Guid.NewGuid().ToString("N");
    private readonly ClaimsPrincipal _owner = Principal("project-1", "run-1");
    private PostgresSessionsProviderOptions _options = null!;
    private PostgresSessionsJournal _journal = null!;
    private SessionsProviderBindingService _bindingService = null!;
    private SessionProviderBinding _binding = null!;

    public SessionsJournalPostgresTests(SessionsPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _options = new PostgresSessionsProviderOptions(
            "resource-1", _fixture.DatabaseName, 4, _schema, "options-v1", PollIntervalMilliseconds: 50);
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        _journal = new PostgresSessionsJournal(_fixture.DataSource, _options);
        var provider = new NativePostgresSessionsProvider();
        var registration = provider.CreateRegistration(_options);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []).Value);
        _bindingService = new SessionsProviderBindingService(
            provider, catalog, new ProviderResolver(catalog), _options, _fixture.DataSource);
        _binding = await _bindingService.ResolveAndPinAsync(_owner);
        await _journal.CreateSessionAsync(_owner, "session-1", _binding);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AppendDedupeConflictAndOrderedPagingSurviveReconstruction()
    {
        var firstInput = Turn(Guid.NewGuid(), "turns/one");
        var first = await _journal.AppendAsync(_owner, "session-1", firstInput);
        Assert.Equal(1, first.Event.Position);
        Assert.False(first.IsDuplicate);
        Assert.Single(first.Event.ObjectReferences);
        Assert.Equal(64, first.Event.ObjectReferences[0].Retention.OwnerId.Length);

        var duplicate = await NewJournal().AppendAsync(_owner, "session-1", firstInput);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.Event.Position, duplicate.Event.Position);
        Assert.Equal(first.Event.OccurredAt, duplicate.Event.OccurredAt);

        await Assert.ThrowsAsync<SessionEventConflictException>(() =>
            _journal.AppendAsync(_owner, "session-1",
                firstInput with { Payload = new TurnSessionPayload("assistant", Ref("turns/one")) }));
        var second = await _journal.AppendAsync(_owner, "session-1", Turn(Guid.NewGuid(), "turns/two"));
        var page1 = await NewJournal().ReplayAsync(_owner, new SessionEventPageRequest("session-1", Limit: 1));
        Assert.Single(page1.Events);
        Assert.Equal(1, page1.Events[0].Position);
        Assert.True(page1.HasMore);

        var page2 = await NewJournal().ReplayAsync(_owner,
            new SessionEventPageRequest("session-1", page1.NextCursor, 10));
        Assert.Single(page2.Events);
        Assert.Equal(second.Event.EventId, page2.Events[0].EventId);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task PolicyEvaluationCannotBeAppendedByAGenericRunScopedCaller()
    {
        var payload = PolicyEvaluation(
            PolicyEvaluationOutcome.Deny,
            PolicyEvaluationReasonCode.ProjectRuleNarrowed);
        var input = PolicyEvaluationEvent(Guid.NewGuid(), payload);
        await Assert.ThrowsAsync<SessionAccessDeniedException>(() =>
            _journal.AppendAsync(_owner, "session-1", input));

        var replay = await NewJournal().ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Empty(replay.Events);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT
                (SELECT last_position FROM "{_schema}".session_run_streams
                    WHERE project_id = 'project-1' AND run_id = 'run-1'),
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                (SELECT count(*) FROM "{_schema}".outbox_events)
            """, connection);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }

    [Fact]
    public async Task AuthenticatedRunTokenCannotAppendReservedPolicyOutcomesThroughHttpRoute()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var issuer = new Uri("https://identity.test");
        const string audience = "https://events.test";
        var token = CreateRunToken(rsa, issuer.ToString(), audience);
        string[] validatedBindingIssuers = [];

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        builder.Services.AddOpenIddict().AddValidation(validation =>
        {
            validation.SetIssuer(issuer);
            validation.AddAudiences(audience);
            validation.Configure(configuration => configuration.Configuration = new OpenIddictConfiguration
            {
                Issuer = issuer,
                SigningKeys = { signingKey }
            });
            validation.UseAspNetCore();
        });
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
            json.SerializerOptions.MaxDepth = 16;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddSingleton<ISessionsJournal>(_journal);
        builder.Services.AddSingleton<ISessionsProviderBinder>(_bindingService);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/internal/sessions/session-1/events")
            {
                validatedBindingIssuers = context.User.Identities
                    .Where(identity => identity.IsAuthenticated)
                    .SelectMany(identity => identity.Claims)
                    .Where(claim => claim.Type is "sub" or "project_id" or "run_id")
                    .Select(claim => claim.Issuer)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            await next();
        });
        app.MapEventsAndSessionsEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        foreach (var (outcome, reason) in new[]
        {
            (PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed),
            (PolicyEvaluationOutcome.Deny, PolicyEvaluationReasonCode.NoEffectiveGrant),
            (PolicyEvaluationOutcome.Error, PolicyEvaluationReasonCode.ProviderUnavailable)
        })
        {
            await AssertRejectedAsync(
                PolicyEvaluationEvent(Guid.NewGuid(), PolicyEvaluation(outcome, reason)));
        }
        var authenticatedIssuer = Assert.Single(validatedBindingIssuers);
        Assert.Equal(issuer.AbsoluteUri, new Uri(authenticatedIssuer).AbsoluteUri);

        var duplicateInput = PolicyEvaluationEvent(
            Guid.NewGuid(),
            PolicyEvaluation(PolicyEvaluationOutcome.Deny, PolicyEvaluationReasonCode.NoEffectiveGrant));
        await AssertRejectedAsync(duplicateInput);
        await AssertRejectedAsync(duplicateInput);

        var replay = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Empty(replay.Events);

        using (var ordinaryRequest = new HttpRequestMessage(
                   HttpMethod.Post, "/internal/sessions/session-1/events")
               {
                   Content = JsonContent.Create(Turn(Guid.NewGuid(), "turns/ordinary"), options: jsonOptions)
               })
        {
            ordinaryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var ordinaryResponse = await client.SendAsync(ordinaryRequest);
            Assert.Equal(HttpStatusCode.Created, ordinaryResponse.StatusCode);
        }

        var afterOrdinaryAppend = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Single(afterOrdinaryAppend.Events);
        Assert.Equal(1, afterOrdinaryAppend.Events[0].Position);
        Assert.Equal(SessionEventKind.Turn, afterOrdinaryAppend.Events[0].Kind);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT
                (SELECT last_position FROM "{_schema}".session_run_streams
                    WHERE project_id = 'project-1' AND run_id = 'run-1'),
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                (SELECT count(*) FROM "{_schema}".outbox_events)
            """, connection);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));

        async Task AssertRejectedAsync(AppendSessionEvent input)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/sessions/session-1/events")
            {
                Content = JsonContent.Create(input, options: jsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"Expected forbidden but received {(int)response.StatusCode}: {responseBody}");
            Assert.Contains("session_access_denied", responseBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PolicyEvaluationWithMismatchedActorIsNotAppendableByGenericCaller()
    {
        var forgedActor = PolicyEvaluationEvent(
            Guid.NewGuid(),
            PolicyEvaluation(PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed)
                with { ActorId = "44444444-4444-4444-4444-444444444444" });

        await Assert.ThrowsAsync<SessionAccessDeniedException>(() =>
            _journal.AppendAsync(_owner, "session-1", forgedActor));

        var replay = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Empty(replay.Events);
    }

    [Fact]
    public async Task PolicyEvaluationsRequireEventVersionTwoAndExistingEventsStillAcceptVersionOne()
    {
        var legacy = Turn(Guid.NewGuid(), "turns/legacy") with { EventVersion = SessionsContractVersions.InitialEventVersion };
        var accepted = await _journal.AppendAsync(_owner, "session-1", legacy);
        Assert.Equal(SessionsContractVersions.InitialEventVersion, accepted.Event.EventVersion);

        var unsupportedPolicy = PolicyEvaluationEvent(
            Guid.NewGuid(),
            PolicyEvaluation(PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed))
            with { EventVersion = SessionsContractVersions.InitialEventVersion };
        await Assert.ThrowsAsync<SessionContractVersionException>(() =>
            _journal.AppendAsync(_owner, "session-1", unsupportedPolicy));

        var replay = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Equal(SessionsContractVersions.InitialEventVersion, Assert.Single(replay.Events).EventVersion);
    }

    [Fact]
    public async Task ConcurrentAppendsFromIndependentInstancesReceiveContiguousPositions()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var journals = new[] { _journal, NewJournal() };
        var appendTasks = Enumerable.Range(0, 30)
            .Select(index => journals[index % 2].AppendAsync(
                _owner, index % 2 == 0 ? "session-1" : "session-2",
                Turn(Guid.NewGuid(), $"turns/{index}")))
            .ToArray();

        var results = await Task.WhenAll(appendTasks);
        Assert.Equal(Enumerable.Range(1, 30).Select(value => (long)value),
            results.Select(result => result.Event.Position).Order());

        var replay = await NewJournal().ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1", Limit: 100));
        Assert.Equal(Enumerable.Range(1, 30).Select(value => (long)value),
            replay.Events.Select(item => item.Position));
        Assert.Equal(15, replay.Events.Count(item => item.Identity.SessionId == "session-1"));
        Assert.Equal(15, replay.Events.Count(item => item.Identity.SessionId == "session-2"));
    }

    [Fact]
    public async Task RunJournalOrdersSessionsAndReconnectsFromDeliveredCursor()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var first = await _journal.AppendAsync(_owner, "session-1", Turn(Guid.NewGuid(), "turns/one"));
        var initial = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1", Limit: 1));
        Assert.Equal(first.Event.EventId, Assert.Single(initial.Events).EventId);
        Assert.NotNull(initial.NextCursor);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = NewJournal().SubscribeRunAsync(_owner,
            new SessionRunSubscriptionRequest(
                "project-1", "run-1", initial.NextCursor, MaximumEvents: 1, MaximumDurationSeconds: 5),
            cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var second = Turn(Guid.NewGuid(), "turns/two");
        await NewJournal().AppendAsync(_owner, "session-2", second);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(second.EventId, subscription.Current.Event.EventId);
        Assert.Equal("session-2", subscription.Current.Event.Identity.SessionId);
        Assert.Equal(2, subscription.Current.Event.Position);
        Assert.False(string.IsNullOrWhiteSpace(subscription.Current.NextCursor));

        using var reconnectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reconnected = NewJournal().SubscribeRunAsync(_owner,
            new SessionRunSubscriptionRequest(
                "project-1", "run-1", subscription.Current.NextCursor,
                MaximumEvents: 1, MaximumDurationSeconds: 5),
            reconnectCancellation.Token).GetAsyncEnumerator(reconnectCancellation.Token);
        var third = Turn(Guid.NewGuid(), "turns/three");
        await NewJournal().AppendAsync(_owner, "session-1", third);

        Assert.True(await reconnected.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(third.EventId, reconnected.Current.Event.EventId);
        Assert.Equal(3, reconnected.Current.Event.Position);
        Assert.Equal("session-1", reconnected.Current.Event.Identity.SessionId);
    }

    [Fact]
    public async Task EqualSessionAndEventIdsAreIsolatedAcrossProjectAndRunScopes()
    {
        var otherProject = Principal("project-2", "run-1");
        var otherRun = Principal("project-1", "run-2");
        var otherProjectBinding = await _bindingService.ResolveAndPinAsync(otherProject);
        var otherRunBinding = await _bindingService.ResolveAndPinAsync(otherRun);
        await _journal.CreateSessionAsync(otherProject, "session-1", otherProjectBinding);
        await _journal.CreateSessionAsync(otherRun, "session-1", otherRunBinding);

        var sharedEventId = Guid.NewGuid();
        var ownerEvent = await _journal.AppendAsync(_owner, "session-1",
            Turn(sharedEventId, "turns/owner"));
        var projectEvent = await _journal.AppendAsync(otherProject, "session-1",
            Turn(sharedEventId, "turns/project"));
        var runEvent = await _journal.AppendAsync(otherRun, "session-1",
            Turn(sharedEventId, "turns/run"));

        Assert.Equal(1, ownerEvent.Event.Position);
        Assert.Equal(1, projectEvent.Event.Position);
        Assert.Equal(1, runEvent.Event.Position);
        Assert.NotEqual(ownerEvent.Event.ObjectReferences[0].Retention.OwnerId,
            projectEvent.Event.ObjectReferences[0].Retention.OwnerId);
        Assert.NotEqual(ownerEvent.Event.ObjectReferences[0].Retention.OwnerId,
            runEvent.Event.ObjectReferences[0].Retention.OwnerId);

        var ownerReplay = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        var projectReplay = await _journal.ReplayRunAsync(
            otherProject, new SessionRunEventPageRequest("project-2", "run-1"));
        var runReplay = await _journal.ReplayRunAsync(
            otherRun, new SessionRunEventPageRequest("project-1", "run-2"));
        Assert.Equal(ownerEvent.Event.EventId, Assert.Single(ownerReplay.Events).EventId);
        Assert.Equal(projectEvent.Event.EventId, Assert.Single(projectReplay.Events).EventId);
        Assert.Equal(runEvent.Event.EventId, Assert.Single(runReplay.Events).EventId);
        Assert.Equal("project-1", ownerReplay.Events[0].Identity.ProjectId);
        Assert.Equal("project-2", projectReplay.Events[0].Identity.ProjectId);
        Assert.Equal("run-2", runReplay.Events[0].Identity.RunId);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _journal.ReplayRunAsync(otherProject,
                new SessionRunEventPageRequest("project-2", "run-1", ownerReplay.NextCursor)));
    }

    [Fact]
    public async Task DuplicateObjectReferencePairsAreRejectedBeforePersistence()
    {
        var input = new AppendSessionEvent(
            Guid.NewGuid(),
            SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.CurrentEventVersion,
            new ToolCallSessionPayload(
                "call-duplicate", "search", "completed",
                Ref("calls/shared"), Ref("calls/shared")));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _journal.AppendAsync(_owner, "session-1", input));
        var replay = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Empty(replay.Events);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                (SELECT count(*) FROM "{_schema}".outbox_events)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    [Fact]
    public async Task LiveSubscriptionPollsDatabaseAcrossInstancesAndRestart()
    {
        var earlier = await _journal.AppendAsync(_owner, "session-1", Turn(Guid.NewGuid(), "turns/before"));
        var replay = await NewJournal().ReplayAsync(_owner, new SessionEventPageRequest("session-1"));
        Assert.Equal(earlier.Event.EventId, replay.Events.Single().EventId);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = NewJournal().SubscribeAsync(_owner,
            new SessionSubscriptionRequest("session-1", replay.NextCursor, MaximumEvents: 1, MaximumDurationSeconds: 5),
            cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var nextEvent = Turn(Guid.NewGuid(), "turns/after");
        await NewJournal().AppendAsync(_owner, "session-1", nextEvent);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(nextEvent.EventId, subscription.Current.Event.EventId);
        Assert.Equal(2, subscription.Current.Event.Position);
        Assert.False(string.IsNullOrWhiteSpace(subscription.Current.NextCursor));

        using var reconnectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reconnected = NewJournal().SubscribeAsync(_owner,
            new SessionSubscriptionRequest(
                "session-1", subscription.Current.NextCursor, MaximumEvents: 1, MaximumDurationSeconds: 5),
            reconnectCancellation.Token).GetAsyncEnumerator(reconnectCancellation.Token);
        var afterReconnect = Turn(Guid.NewGuid(), "turns/after-reconnect");
        await NewJournal().AppendAsync(_owner, "session-1", afterReconnect);

        Assert.True(await reconnected.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(afterReconnect.EventId, reconnected.Current.Event.EventId);
        Assert.Equal(3, reconnected.Current.Event.Position);
    }

    [Fact]
    public async Task ClaimsAndSessionOwnershipBlockCrossProjectAndCrossRunAccess()
    {
        var otherProject = Principal("project-2", "run-1");
        var otherRun = Principal("project-1", "run-2");
        await Assert.ThrowsAsync<SessionNotFoundException>(() =>
            _journal.AppendAsync(otherProject, "session-1", Turn(Guid.NewGuid(), "turns/foreign")));
        await Assert.ThrowsAsync<SessionNotFoundException>(() =>
            _journal.ReplayAsync(otherRun, new SessionEventPageRequest("session-1")));
        await Assert.ThrowsAsync<SessionAccessDeniedException>(() =>
            _journal.ReplayRunAsync(_owner, new SessionRunEventPageRequest("project-2", "run-1")));
        await Assert.ThrowsAsync<SessionAuthenticationException>(() =>
            _journal.CreateSessionAsync(Principal("project-1", null), "session-2", _binding));
        await Assert.ThrowsAsync<SessionAuthenticationException>(() =>
            _journal.ReplayAsync(Principal("project-1", "run-1", "run-2"),
                new SessionEventPageRequest("session-1")));

        var cursor = await _journal.ReplayAsync(_owner, new SessionEventPageRequest("session-1"));
        await Assert.ThrowsAsync<SessionNotFoundException>(() =>
            _journal.ReplayAsync(otherProject,
                new SessionEventPageRequest("session-1", cursor.NextCursor)));
    }

    [Fact]
    public async Task IdentityBrokerPrincipalCanAccessOnlyItsCoreGrantedProjectRun()
    {
        var existing = await _journal.AppendAsync(_owner, "session-1", Turn(Guid.NewGuid(), "turns/owned"));
        var owned = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1"));
        Assert.Equal(existing.Event.EventId, Assert.Single(owned.Events).EventId);
        Assert.DoesNotContain(_owner.Claims, claim =>
            claim.Type is "tenant_id" or Claims.Role ||
            claim.Value is "platform_admin" or "orchestrator");

        var missingOwner = IdentityBrokerPrincipal(projectId: null, runId: null);
        await Assert.ThrowsAsync<SessionAuthenticationException>(() =>
            _journal.ReplayRunAsync(missingOwner,
                new SessionRunEventPageRequest("project-1", "run-1")));

        var foreignOwner = IdentityBrokerPrincipal(projectId: "project-2", runId: "run-1");
        await Assert.ThrowsAsync<SessionAccessDeniedException>(() =>
            _journal.ReplayRunAsync(foreignOwner,
                new SessionRunEventPageRequest("project-1", "run-1")));
        await Assert.ThrowsAsync<SessionNotFoundException>(() =>
            _journal.ReplayRunAsync(foreignOwner,
                new SessionRunEventPageRequest("project-2", "run-1")));
    }

    [Fact]
    public async Task RunBindingIsDurableImmutableAndComparedOnRepeatedCreate()
    {
        var stored = await _journal.GetProviderBindingAsync(_owner, "session-1");
        Assert.True(_binding.Matches(stored));
        Assert.Equal(NativePostgresSessionsProvider.ProviderId, stored.ProviderId);
        Assert.Equal("options-v1", stored.OptionsRevision);
        Assert.Equal("resource-1", stored.ResourceId);
        Assert.Equal(4, stored.ResourceGeneration);
        Assert.Equal(SessionsCapabilities.All, stored.NegotiatedCapabilities);

        var changed = new SessionProviderBinding(
            stored.ProjectId, stored.RunId, stored.ProviderId, stored.AdapterVersion,
            stored.OptionsSchemaVersion, stored.OptionsRevision, stored.ResourceId,
            stored.ResourceGeneration + 1, stored.NegotiatedCapabilities);
        await Assert.ThrowsAsync<SessionProviderBindingConflictException>(() =>
            _journal.CreateSessionAsync(_owner, "session-1", changed));
        await Assert.ThrowsAsync<SessionProviderBindingConflictException>(() =>
            _journal.CreateSessionAsync(_owner, "session-3", changed));

        await _journal.CreateSessionAsync(_owner, "session-2", stored);
        Assert.True(stored.Matches(await _journal.GetProviderBindingAsync(_owner, "session-2")));
        Assert.True(stored.Matches(await _journal.GetProviderBindingAsync(_owner, "session-1")));
    }

    [Fact]
    public async Task LaterOperationsRejectChangedOrMissingPinnedProviderBinding()
    {
        var changedOptions = _options with { OptionsRevision = "options-v2", ResourceGeneration = 5 };
        var provider = new NativePostgresSessionsProvider();
        var changedCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [provider.CreateRegistration(changedOptions)],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []).Value);
        var changedBindingService = new SessionsProviderBindingService(
            provider, changedCatalog, new ProviderResolver(changedCatalog), changedOptions, _fixture.DataSource);
        var stored = await _journal.GetProviderBindingAsync(_owner, "session-1");
        await Assert.ThrowsAsync<SessionPinnedProviderUnavailableException>(() =>
            changedBindingService.VerifyPinnedAsync(_owner, stored));

        var missingCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create([], [], []).Value);
        var missingBindingService = new SessionsProviderBindingService(
            provider, missingCatalog, new ProviderResolver(missingCatalog), _options, _fixture.DataSource);
        await Assert.ThrowsAsync<SessionPinnedProviderUnavailableException>(() =>
            missingBindingService.VerifyPinnedAsync(_owner, stored));
    }

    [Fact]
    public async Task EventPersistenceFailureRollsBackReceiptAndPositionAndIsNotReturnedAsSuccess()
    {
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"DROP TABLE \"{_schema}\".session_events CASCADE", connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            _journal.AppendAsync(
                _owner,
                "session-1",
                Turn(Guid.NewGuid(), "turns/persistence-failure")));

        await using var verify = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT
                (SELECT last_position FROM "{_schema}".session_run_streams
                    WHERE project_id = 'project-1' AND run_id = 'run-1'),
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                (SELECT count(*) FROM "{_schema}".outbox_events)
            """, verify);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }

    [Fact]
    public async Task NegotiatesLivePostgresThenPinsAndRejectsMismatchedResource()
    {
        var provider = new NativePostgresSessionsProvider();
        var registration = provider.CreateRegistration(_options);
        var catalog = Assert.IsType<Agentweaver.Providers.ProviderCatalog>(
            Agentweaver.Providers.ProviderCatalog.Create(
                [registration],
                [new Agentweaver.Abstractions.ProviderSelection(
                    Agentweaver.Abstractions.ProviderSeam.Sessions,
                    NativePostgresSessionsProvider.ProviderId)],
                []).Value);
        var resolver = new Agentweaver.Providers.ProviderResolver(catalog);
        var request = new Agentweaver.Abstractions.ProviderResolutionRequest(
            Agentweaver.Abstractions.ProviderSeam.Sessions,
            null,
            NativePostgresSessionsProvider.AdapterVersion,
            NativePostgresSessionsProvider.OptionsSchemaVersion,
            SessionsCapabilities.All);
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        var binding = await provider.ResolveNegotiateAndPinAsync(
            resolver, request, _options, _fixture.DataSource, "resource-1", "run-1");
        Assert.True(binding.IsSuccess);
        Assert.Equal("resource-1", binding.Value!.Resource.ResourceId);
        Assert.Equal(_fixture.DatabaseName, _options.ExpectedDatabaseName);
        Assert.Equal(4, binding.Value.Resource.Generation);
        Assert.Equal(SessionsCapabilities.All, binding.Value.NegotiatedCapabilities);
        var activity = Assert.Single(activities);
        Assert.Equal("sessions.provider.binding.pinned", activity.OperationName);
        Assert.Equal("Sessions", activity.GetTagItem("provider.seam"));
        Assert.Equal(NativePostgresSessionsProvider.ProviderId, activity.GetTagItem("provider.resolved.id"));
        Assert.Equal(binding.Value.ProviderId, activity.GetTagItem("provider.pinned.id"));
        Assert.Equal("1.0.0", activity.GetTagItem("provider.adapter.version"));
        Assert.Equal(1, activity.GetTagItem("provider.options.schema_version"));
        Assert.Equal(_options.OptionsRevision, activity.GetTagItem("provider.options.revision"));
        var resourceIdHash = Assert.IsType<string>(activity.GetTagItem("provider.resource.id_hash"));
        Assert.Equal(24, resourceIdHash.Length);
        Assert.NotEqual(_options.ResourceId, resourceIdHash);
        Assert.Equal(_options.ResourceGeneration, activity.GetTagItem("provider.resource.generation"));
        Assert.Equal(SessionsCapabilities.All.OrderBy(value => value, StringComparer.Ordinal),
            Assert.IsType<string[]>(activity.GetTagItem("provider.negotiated_capabilities")));
        Assert.DoesNotContain(activity.Tags, tag =>
            tag.Key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("password", StringComparison.OrdinalIgnoreCase));

        using var failingListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => throw new InvalidOperationException("simulated telemetry export failure")
        };
        ActivitySource.AddActivityListener(failingListener);
        var stillPinned = await provider.ResolveNegotiateAndPinAsync(
            resolver, request, _options, _fixture.DataSource, "resource-1", "run-1");
        Assert.True(stillPinned.IsSuccess);

        var mismatch = await provider.ResolveNegotiateAndPinAsync(
            resolver, request, _options, _fixture.DataSource, "wrong-resource", "run-1");
        Assert.False(mismatch.IsSuccess);
        Assert.Equal(Agentweaver.Providers.ProviderErrorCode.ResourceMismatch, mismatch.Error!.Code);
    }

    private PostgresSessionsJournal NewJournal() => new(_fixture.DataSource, _options);

    private static AppendSessionEvent Turn(Guid eventId, string key) =>
        new(eventId, SessionsContractVersions.CurrentSchemaVersion, SessionsContractVersions.CurrentEventVersion,
            new TurnSessionPayload("user", Ref(key)));

    private static AppendSessionEvent PolicyEvaluationEvent(
        Guid eventId,
        PolicyEvaluationSessionPayload payload) =>
        new(eventId, SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.PolicyEvaluationEventVersion, payload);

    private static PolicyEvaluationSessionPayload PolicyEvaluation(
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reasonCode) =>
        new(
            "33333333-3333-3333-3333-333333333333",
            "tenant-1",
            "step-1",
            "grant-1",
            "revision-1",
            "coordination.decision",
            "coordinator.question.respond",
            outcome,
            reasonCode,
            1,
            "agt.default",
            "1.0.0",
            1,
            "options-2026-10");

    private static string CreateRunToken(RSA signingKey, string issuer, string audience)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "at+jwt" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer,
            aud = audience,
            sub = "33333333-3333-3333-3333-333333333333",
            project_id = "project-1",
            run_id = "run-1",
            scope = "openid",
            nbf = now - 60,
            exp = now + 300
        }));
        var unsignedToken = $"{header}.{payload}";
        var signature = signingKey.SignData(
            Encoding.ASCII.GetBytes(unsignedToken),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{unsignedToken}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SessionObjectReference Ref(string key) => new(new ObjectKey(key), "transcript", 128);

    private static ClaimsPrincipal Principal(string? projectId, string? runId, params string[] extraRunIds)
        => IdentityBrokerPrincipal(projectId, runId, extraRunIds);

    private static ClaimsPrincipal IdentityBrokerPrincipal(
        string? projectId, string? runId, params string[] extraRunIds)
    {
        var identity = new ClaimsIdentity("identity-broker-profile", Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, "33333333-3333-3333-3333-333333333333"));
        if (projectId is not null) identity.AddClaim(new Claim("project_id", projectId));
        if (runId is not null) identity.AddClaim(new Claim("run_id", runId));
        identity.AddClaims(extraRunIds.Select(value => new Claim("run_id", value)));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(["openid"]);
        principal.SetResources(["agentweaver.events"]);
        return principal;
    }
}
