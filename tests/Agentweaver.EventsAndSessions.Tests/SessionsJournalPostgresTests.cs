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
using Microsoft.AspNetCore.Http;
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
    private NativePostgresMessagingProviderOptions _messagingOptions = null!;
    private PostgresSessionsJournal _journal = null!;
    private SessionsProviderBindingService _bindingService = null!;
    private ProviderCatalog _catalog = null!;
    private ProviderResolver _resolver = null!;
    private NativePostgresMessagingProvider _messagingProvider = null!;
    private PostgresAddressedMessageStore _messages = null!;
    private SessionProviderBinding _binding = null!;

    public SessionsJournalPostgresTests(SessionsPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _options = new PostgresSessionsProviderOptions(
            "resource-1", _fixture.DatabaseName, 4, _schema, "options-v1", PollIntervalMilliseconds: 50);
        _messagingOptions = new NativePostgresMessagingProviderOptions("messaging-options-v1");
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        _journal = new PostgresSessionsJournal(_fixture.DataSource, _options);
        var provider = new NativePostgresSessionsProvider();
        var registration = provider.CreateRegistration(_options);
        _messagingProvider = new NativePostgresMessagingProvider();
        var messagingRegistration = _messagingProvider.CreateRegistration(_messagingOptions);
        _catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration, messagingRegistration],
            [
                new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId),
                new ProviderSelection(ProviderSeam.Messaging, NativePostgresMessagingProvider.ProviderId)
            ],
            []).Value);
        _resolver = new ProviderResolver(_catalog);
        _bindingService = new SessionsProviderBindingService(
            provider, _catalog, _resolver, _options, _fixture.DataSource, _journal);
        _binding = await _bindingService.ResolveAndPinAsync(_owner);
        await _journal.CreateSessionAsync(_owner, "session-1", _binding);
        _messages = NewMessageStore();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AddressedMessageIsIdempotentAndCommitsWithJournalAndOutbox()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var draft = Message("first-message", AddressedMessageDeliveryMode.Immediate) with
        {
            Purpose = AddressedMessagePurpose.Handoff,
            UserQuote = "Please verify the exact output.",
            CoordinatorInstructions = "Report evidence without changing scope."
        };

        var sent = await _messages.SendAsync(_owner, draft);
        var replay = await NewMessageStore().SendAsync(_owner, draft);
        Assert.False(sent.IsDuplicate);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(sent.Message.MessageId, replay.Message.MessageId);
        Assert.Equal(1, sent.Message.ThreadSequence);
        Assert.Equal("Please verify the exact output.", replay.Message.UserQuote);
        Assert.Equal("Report evidence without changing scope.", replay.Message.CoordinatorInstructions);
        Assert.Equal(24, sent.Message.Provider.ResourceIdHash.Length);
        Assert.DoesNotContain(_options.ResourceId, JsonSerializer.Serialize(sent.Message), StringComparison.Ordinal);

        var history = await _journal.ReplayAsync(_owner, new SessionEventPageRequest("session-1"));
        var messageReference = Assert.IsType<AddressedMessageSessionPayload>(
            Assert.Single(history.Events).Payload);
        Assert.Equal(sent.Message.MessageId, messageReference.MessageId);
        Assert.Equal(AddressedMessagePurpose.Handoff, messageReference.Purpose);

        await Assert.ThrowsAsync<AddressedMessageException>(() =>
            _messages.SendAsync(_owner, draft with { Payload = Payload("""{"text":"changed"}""") }));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".addressed_messages),
                (SELECT count(*) FROM "{_schema}".addressed_message_threads),
                (SELECT count(*) FROM "{_schema}".session_events),
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                (SELECT count(*) FROM "{_schema}".outbox_events
                    WHERE event_type = 'sessions.addressed_message'),
                (SELECT payload ->> 'purpose' FROM "{_schema}".outbox_events
                    WHERE event_type = 'sessions.addressed_message'),
                (SELECT payload ->> 'deliveryMode' FROM "{_schema}".outbox_events
                    WHERE event_type = 'sessions.addressed_message')
            """, connection);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal(1, reader.GetInt64(4));
        Assert.Equal("handoff", reader.GetString(5));
        Assert.Equal("immediate", reader.GetString(6));
    }

    [Fact]
    public async Task AddressedMessageIdempotencyIsScopedToTheSenderSession()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);

        var first = await _messages.SendAsync(
            _owner, Message("same-key", AddressedMessageDeliveryMode.Immediate));
        var second = await _messages.SendAsync(
            _owner,
            Message("same-key", AddressedMessageDeliveryMode.Immediate,
                senderSession: "session-2", recipientSession: "session-1"));

        Assert.False(first.IsDuplicate);
        Assert.False(second.IsDuplicate);
        Assert.NotEqual(first.Message.MessageId, second.Message.MessageId);
    }

    [Fact]
    public async Task ProgressMessagesAreStoredButNotPresentedAsRecipientInput()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var recipient = new SessionIdentity("project-1", "run-1", "session-2");
        var progress = await _messages.SendAsync(
            _owner, Message("progress", AddressedMessageDeliveryMode.Immediate));
        var input = await _messages.SendAsync(
            _owner,
            Message("input", AddressedMessageDeliveryMode.Immediate) with
            {
                Purpose = AddressedMessagePurpose.Handoff,
                ThreadId = progress.Message.ThreadId
            });

        var claim = await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "recipient-worker");

        Assert.Equal(input.Message.MessageId, Assert.IsType<AddressedMessageClaim>(claim).Message.MessageId);
        Assert.Equal(AddressedMessageStatus.Accepted,
            (await NewMessageStore().GetAsync("project-1", progress.Message.MessageId))!.Status);
        var history = await _journal.ReplayAsync(_owner, new SessionEventPageRequest("session-1"));
        Assert.Equal(2, history.Events.Length);
    }

    [Fact]
    public async Task AddressedMessageJournalAndOutboxFailureRollsBackMessageAndThread()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"""
            CREATE FUNCTION "{_schema}".reject_test_outbox_insert()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'injected outbox insert failure';
            END;
            $$;
            CREATE TRIGGER reject_test_outbox_insert
                BEFORE INSERT ON "{_schema}".outbox_events
                FOR EACH ROW EXECUTE FUNCTION "{_schema}".reject_test_outbox_insert();
            """, connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            _messages.SendAsync(_owner, Message("rollback", AddressedMessageDeliveryMode.Immediate)));

        await using var verify = await _fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".addressed_messages),
                (SELECT count(*) FROM "{_schema}".addressed_message_threads),
                (SELECT count(*) FROM "{_schema}".messaging_provider_bindings),
                (SELECT last_position FROM "{_schema}".session_run_streams
                    WHERE project_id = 'project-1' AND run_id = 'run-1'),
                (SELECT count(*) FROM "{_schema}".session_events),
                (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts)
            """, verify);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.Equal(0, reader.GetInt64(3));
        Assert.Equal(0, reader.GetInt64(4));
        Assert.Equal(0, reader.GetInt64(5));
    }

    [Fact]
    public async Task AddressedMessageBoundaryClaimPrioritizesImmediateAndRecoversFencedClaims()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var recipient = new SessionIdentity("project-1", "run-1", "session-2");
        var queued = await _messages.SendAsync(
            _owner, Message("queued-first", AddressedMessageDeliveryMode.Enqueue) with
            {
                Purpose = AddressedMessagePurpose.Handoff
            });
        var laterImmediateSameThread = await _messages.SendAsync(
            _owner, Message("immediate-same-thread", AddressedMessageDeliveryMode.Immediate) with
            {
                Purpose = AddressedMessagePurpose.Handoff,
                ThreadId = queued.Message.ThreadId
            });
        var immediateOtherThread = await _messages.SendAsync(
            _owner, Message("immediate-other-thread", AddressedMessageDeliveryMode.Immediate) with
            {
                Purpose = AddressedMessagePurpose.Handoff
            });

        var first = (await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "worker-1"))!;
        Assert.Equal(immediateOtherThread.Message.MessageId, first.Message.MessageId);
        var firstPresented = await NewMessageStore().PresentAsync(first);
        Assert.Equal(AddressedMessageStatus.Delivered, firstPresented.Status);
        var firstAcknowledged = await NewMessageStore().AcknowledgeAsync(
            recipient, first.Message.MessageId, first.Owner, first.Message.ClaimFence);
        Assert.Equal(AddressedMessageStatus.Acknowledged, firstAcknowledged.Status);
        var repeatedAcknowledgment = await NewMessageStore().AcknowledgeAsync(
            recipient, first.Message.MessageId, first.Owner, first.Message.ClaimFence);
        Assert.Equal(firstAcknowledged.AcknowledgedAt, repeatedAcknowledgment.AcknowledgedAt);

        var second = (await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "worker-1"))!;
        Assert.Equal(queued.Message.MessageId, second.Message.MessageId);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var expireLease = new NpgsqlCommand($"""
            UPDATE "{_schema}".addressed_messages
            SET claimed_until = clock_timestamp() - interval '1 second'
            WHERE project_id = 'project-1' AND message_id = @message
            """, connection))
        {
            expireLease.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, second.Message.MessageId);
            Assert.Equal(1, await expireLease.ExecuteNonQueryAsync());
        }

        var recovered = (await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "worker-2"))!;
        Assert.Equal(second.Message.MessageId, recovered.Message.MessageId);
        Assert.Equal(second.Message.ClaimFence + 1, recovered.Message.ClaimFence);
        await Assert.ThrowsAsync<AddressedMessageException>(() =>
            NewMessageStore().PresentAsync(second));
        var delivered = await NewMessageStore().PresentAsync(recovered);
        Assert.NotNull(delivered.PresentedAt);
        await Assert.ThrowsAsync<AddressedMessageException>(() =>
            NewMessageStore().AcknowledgeAsync(
                recipient, delivered.MessageId, second.Owner, second.Message.ClaimFence));
        var acknowledged = await NewMessageStore().AcknowledgeAsync(
            recipient, delivered.MessageId, recovered.Owner, recovered.Message.ClaimFence);
        Assert.Equal(AddressedMessageStatus.Acknowledged, acknowledged.Status);

        var third = (await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "worker-3"))!;
        Assert.Equal(laterImmediateSameThread.Message.MessageId, third.Message.MessageId);

        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".outbox_events
            WHERE event_type = 'sessions.addressed_message'
            """, verifyConnection);
        Assert.Equal(3L, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AddressedMessageBoundaryRetryReturnsTheSameActiveClaimUntilAcknowledged()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var recipient = new SessionIdentity("project-1", "run-1", "session-2");
        var sent = await _messages.SendAsync(
            _owner, Message("boundary-retry", AddressedMessageDeliveryMode.Immediate) with
            {
                Purpose = AddressedMessagePurpose.Handoff
            });

        var first = (await _messages.ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "same-owner"))!;
        var retry = (await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "same-owner"))!;
        Assert.Equal(sent.Message.MessageId, first.Message.MessageId);
        Assert.Equal(first.Message.ClaimFence, retry.Message.ClaimFence);

        await _messages.PresentAsync(first);
        var presentedRetry = (await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "same-owner"))!;
        Assert.Equal(AddressedMessageStatus.Delivered, presentedRetry.Message.Status);
        Assert.Equal(first.Message.ClaimFence, presentedRetry.Message.ClaimFence);
        await _messages.AcknowledgeAsync(
            recipient, first.Message.MessageId, first.Owner, first.Message.ClaimFence);
        Assert.Null(await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "same-owner"));
    }

    [Fact]
    public async Task AddressedMessageRepliesRequireAcknowledgedReverseParticipantAndCorrelation()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var original = await _messages.SendAsync(
            _owner, Message("question", AddressedMessageDeliveryMode.Enqueue) with
            {
                Purpose = AddressedMessagePurpose.NeedsInput,
                Kind = AddressedMessageKind.Question,
                RequestId = "gate-42"
            });
        var reverse = Message(
            "reply", AddressedMessageDeliveryMode.Enqueue,
            senderSession: "session-2",
            recipientSession: "session-1") with
        {
            ReplyToId = original.Message.MessageId,
            ReplyCorrelationId = "gate-42"
        };
        var premature = await Assert.ThrowsAsync<AddressedMessageException>(() =>
            _messages.SendAsync(_owner, reverse));
        Assert.Equal("reply_unavailable", premature.Code);

        var recipient = new SessionIdentity("project-1", "run-1", "session-2");
        var claim = (await _messages.ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 4, owner: "question-worker"))!;
        await _messages.PresentAsync(claim);
        await _messages.AcknowledgeAsync(
            recipient, claim.Message.MessageId, claim.Owner, claim.Message.ClaimFence);

        var reply = await NewMessageStore().SendAsync(_owner, reverse with
        {
            UserQuote = "The operator's exact question.",
            CoordinatorInstructions = "Only answer that question."
        });
        Assert.False(reply.IsDuplicate);
        Assert.Equal(original.Message.ThreadId, reply.Message.ThreadId);
        Assert.Equal(2, reply.Message.ThreadSequence);
        Assert.Equal(original.Message.MessageId, reply.Message.ReplyToId);
        Assert.Equal("gate-42", reply.Message.ReplyCorrelationId);
        Assert.Equal("The operator's exact question.", reply.Message.UserQuote);
        Assert.Equal("Only answer that question.", reply.Message.CoordinatorInstructions);
        Assert.Null(reply.Message.AcknowledgedAt);
    }

    [Fact]
    public async Task AddressedMessageExpiryStaleFenceAndUndeliverableNeverForgeAcknowledgment()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var recipient = new SessionIdentity("project-1", "run-1", "session-2");
        var stale = await _messages.SendAsync(
            _owner, Message("stale-fence", AddressedMessageDeliveryMode.Immediate));
        var noClaim = await NewMessageStore().ClaimNextAtTurnBoundaryAsync(
            recipient, currentFence: 5, owner: "new-generation");
        Assert.Null(noClaim);
        var staleStored = (await _messages.GetAsync("project-1", stale.Message.MessageId))!;
        Assert.Equal(AddressedMessageStatus.Undeliverable, staleStored.Status);
        Assert.Equal(AddressedMessageFailureReason.StaleFence, staleStored.FailureReason);
        Assert.Null(staleStored.AcknowledgedAt);

        var expiring = await _messages.SendAsync(
            _owner, Message("expires", AddressedMessageDeliveryMode.Enqueue) with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            });
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var expire = new NpgsqlCommand($"""
            UPDATE "{_schema}".addressed_messages
            SET expires_at = clock_timestamp() - interval '1 second'
            WHERE project_id = 'project-1' AND message_id = @message
            """, connection))
        {
            expire.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, expiring.Message.MessageId);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        Assert.Equal(1, await NewMessageStore().ExpireDueAsync());
        var expired = (await _messages.GetAsync("project-1", expiring.Message.MessageId))!;
        Assert.Equal(AddressedMessageStatus.Expired, expired.Status);
        Assert.Null(expired.AcknowledgedAt);

        var pending = await _messages.SendAsync(
            _owner, Message("undeliverable", AddressedMessageDeliveryMode.Enqueue));
        var failed = await NewMessageStore().MarkUndeliverableAsync(
            recipient, pending.Message.MessageId, AddressedMessageFailureReason.TargetCancelled);
        Assert.Equal(AddressedMessageStatus.Undeliverable, failed.Status);
        Assert.Equal(AddressedMessageFailureReason.TargetCancelled, failed.FailureReason);
        Assert.Null(failed.AcknowledgedAt);
    }

    [Fact]
    public async Task ConcurrentAddressedMessagesKeepContiguousThreadSequence()
    {
        await _journal.CreateSessionAsync(_owner, "session-2", _binding);
        var first = await _messages.SendAsync(
            _owner, Message("concurrent-base", AddressedMessageDeliveryMode.Enqueue));
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            NewMessageStore().SendAsync(
                _owner,
                Message(
                    $"concurrent-{index}",
                    index % 2 == 0 ? AddressedMessageDeliveryMode.Immediate : AddressedMessageDeliveryMode.Enqueue)
                with { ThreadId = first.Message.ThreadId })));

        Assert.Equal(Enumerable.Range(2, 16).Select(value => (long)value),
            results.Select(result => result.Message.ThreadSequence).Order());
        var replay = await _journal.ReplayRunAsync(
            _owner, new SessionRunEventPageRequest("project-1", "run-1", Limit: 100));
        Assert.Equal(17, replay.Events.Length);
        Assert.Equal(Enumerable.Range(1, 17).Select(value => (long)value),
            replay.Events.Select(item => item.Position));
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
    public async Task UpgradedProviderPreservesLegacyRunCapabilitiesAndPinsExpandedCapabilitiesForNewRuns()
    {
        var legacyRun = Principal("project-1", "legacy-run");
        var legacyCapabilities = SessionsCapabilities.All.Remove(SessionsCapabilities.PolicyEvaluations);
        var legacyBinding = new SessionProviderBinding(
            "project-1",
            "legacy-run",
            NativePostgresSessionsProvider.ProviderId,
            NativePostgresSessionsProvider.AdapterVersion,
            NativePostgresSessionsProvider.OptionsSchemaVersion,
            _options.OptionsRevision,
            _options.ResourceId,
            _options.ResourceGeneration,
            legacyCapabilities);
        await _journal.CreateSessionAsync(legacyRun, "legacy-root", legacyBinding);

        var restartedJournal = new PostgresSessionsJournal(_fixture.DataSource, _options);
        var provider = new NativePostgresSessionsProvider();
        var upgradedBindingService = new SessionsProviderBindingService(
            provider, _catalog, _resolver, _options, _fixture.DataSource, restartedJournal);

        var pinned = await restartedJournal.GetRunProviderBindingAsync(
            legacyRun, "project-1", "legacy-run");
        await upgradedBindingService.VerifyPinnedAsync(legacyRun, pinned);
        Assert.Equal(legacyCapabilities, pinned.NegotiatedCapabilities);

        var beforePolicyAppend = await ReadRunEventStateAsync();
        Assert.Equal(0, beforePolicyAppend.Position);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<PostgresSessionsJournal>(restartedJournal);
        builder.Services.AddSingleton<ISessionsJournal>(restartedJournal);
        builder.Services.AddSingleton<ISessionsProviderBinder>(upgradedBindingService);
        builder.Services.AddSingleton<ICoordinationOwnerClient>(new UnusedCoordinationOwnerClient());
        await using (var app = builder.Build())
        {
            app.Use(async (context, next) =>
            {
                context.User = legacyRun;
                await next();
            });
            app.UseAuthorization();
            app.MapEventsAndSessionsEndpoints();
            await app.StartAsync();

            using var client = app.GetTestClient();
            using var response = await client.PostAsJsonAsync(
                "/internal/sessions/legacy-root/policy-evaluations",
                new PolicyEvaluationReceiptReferenceRequest(Guid.NewGuid()));
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("sessions_provider_binding_conflict", responseBody, StringComparison.Ordinal);
        }
        Assert.Equal(beforePolicyAppend, await ReadRunEventStateAsync());

        var first = await restartedJournal.AppendAsync(
            legacyRun, "legacy-root", Turn(Guid.NewGuid(), "turns/legacy-root"));
        Assert.Equal(1, first.Event.Position);
        var replay = await restartedJournal.ReplayAsync(
            legacyRun, new SessionEventPageRequest("legacy-root"));
        Assert.Equal(first.Event.EventId, Assert.Single(replay.Events).EventId);

        await using (var subscription = restartedJournal.SubscribeAsync(
            legacyRun, new SessionSubscriptionRequest("legacy-root", MaximumEvents: 1))
            .GetAsyncEnumerator())
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(first.Event.EventId, subscription.Current.Event.EventId);
        }

        var additionalSessionBinding = await upgradedBindingService.ResolveAndPinAsync(legacyRun);
        Assert.Equal(legacyCapabilities, additionalSessionBinding.NegotiatedCapabilities);
        await restartedJournal.CreateSessionAsync(legacyRun, "legacy-child", additionalSessionBinding);
        Assert.True(additionalSessionBinding.Matches(
            await restartedJournal.GetProviderBindingAsync(legacyRun, "legacy-child")));

        var newRun = Principal("project-1", "new-run");
        var newRunBinding = await upgradedBindingService.ResolveAndPinAsync(newRun);
        Assert.Equal(SessionsCapabilities.All, newRunBinding.NegotiatedCapabilities);
        Assert.Contains(SessionsCapabilities.PolicyEvaluations, newRunBinding.NegotiatedCapabilities);

        async Task<(long Position, long Events, long Inbox, long Outbox)> ReadRunEventStateAsync()
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT
                    COALESCE((SELECT last_position FROM "{_schema}".session_run_streams
                        WHERE project_id = 'project-1' AND run_id = 'legacy-run'), 0),
                    (SELECT count(*) FROM "{_schema}".session_events),
                    (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                    (SELECT count(*) FROM "{_schema}".outbox_events)
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }
    }

    [Fact]
    public async Task LaterOperationsRejectChangedOrMissingPinnedProviderBinding()
    {
        var changedOptions = _options with { OptionsRevision = "options-v2", ResourceGeneration = 5 };
        var provider = new NativePostgresSessionsProvider();
        var registration = provider.CreateRegistration(_options);
        var changedCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [provider.CreateRegistration(changedOptions)],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []).Value);
        var changedBindingService = new SessionsProviderBindingService(
            provider, changedCatalog, new ProviderResolver(changedCatalog), changedOptions,
            _fixture.DataSource, _journal);
        var stored = await _journal.GetProviderBindingAsync(_owner, "session-1");
        await Assert.ThrowsAsync<SessionPinnedProviderUnavailableException>(() =>
            changedBindingService.VerifyPinnedAsync(_owner, stored));

        var missingCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create([], [], []).Value);
        var missingBindingService = new SessionsProviderBindingService(
            provider, missingCatalog, new ProviderResolver(missingCatalog), _options,
            _fixture.DataSource, _journal);
        await Assert.ThrowsAsync<SessionPinnedProviderUnavailableException>(() =>
            missingBindingService.VerifyPinnedAsync(_owner, stored));

        var unsupportedCapabilities = stored.NegotiatedCapabilities.Add("sessions.unsupported");
        var tampered = new SessionProviderBinding(
            stored.ProjectId, stored.RunId, stored.ProviderId, stored.AdapterVersion,
            stored.OptionsSchemaVersion, stored.OptionsRevision, stored.ResourceId,
            stored.ResourceGeneration, unsupportedCapabilities);
        await Assert.ThrowsAsync<SessionPinnedProviderUnavailableException>(() =>
            _bindingService.VerifyPinnedAsync(_owner, tampered));

        var narrowedDescriptor = registration.Descriptor with
        {
            AdvertisedCapabilities = stored.NegotiatedCapabilities.Remove(SessionsCapabilities.Replay)
        };
        var unsupportedRegistration = registration with { Descriptor = narrowedDescriptor };
        var unsupportedCatalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [unsupportedRegistration],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []).Value);
        var unsupportedBindingService = new SessionsProviderBindingService(
            provider, unsupportedCatalog, new ProviderResolver(unsupportedCatalog), _options,
            _fixture.DataSource, _journal);
        await Assert.ThrowsAsync<SessionPinnedProviderUnavailableException>(() =>
            unsupportedBindingService.VerifyPinnedAsync(_owner, stored));
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

    private PostgresAddressedMessageStore NewMessageStore() => new(
        _fixture.DataSource,
        _options,
        _messagingOptions,
        _messagingProvider,
        _resolver,
        NewJournal(),
        "https://identity.example");

    private static AddressedMessageDraft Message(
        string idempotencyKey,
        AddressedMessageDeliveryMode deliveryMode,
        string senderSession = "session-1",
        string recipientSession = "session-2") =>
        new(
            new SessionIdentity("project-1", "run-1", senderSession),
            new SessionIdentity("project-1", "run-1", recipientSession),
            idempotencyKey,
            deliveryMode,
            AddressedMessagePurpose.Progress,
            AddressedMessageKind.Text,
            Payload("""{"text":"status"}"""),
            SenderFence: 4,
            RecipientFence: 4);

    private static System.Text.Json.JsonElement Payload(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

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

    private sealed class UnusedCoordinationOwnerClient : ICoordinationOwnerClient
    {
        public Task<MessageRouteBinding> ValidateMessageRouteAsync(
            HttpContext context,
            string projectId,
            string runId,
            MessageRouteValidationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The legacy policy route must reject before owner admission.");

        public Task<CoordinationSessionBinding> GetSessionBindingAsync(
            HttpContext context,
            string projectId,
            string runId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The legacy policy route must reject before owner admission.");

        public Task<PolicyEvaluationReceiptView> ReadPolicyEvaluationReceiptAsync(
            HttpContext context,
            SessionIdentity identity,
            Guid receiptId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The legacy policy route must reject before owner admission.");

        public Task ValidatePolicyEvaluationReceiptAdmissionAsync(
            HttpContext context,
            SessionIdentity identity,
            Guid receiptId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The legacy policy route must reject before owner admission.");
    }
}
