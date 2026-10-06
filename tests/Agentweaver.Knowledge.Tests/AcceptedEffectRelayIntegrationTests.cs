using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Knowledge;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

[Collection("Knowledge PostgreSQL")]
public sealed class AcceptedEffectRelayIntegrationTests(KnowledgePostgresFixture postgres)
{
    private static readonly Uri Issuer = new("https://identity.test/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task PendingRelayRetriesThroughEventsAndReturnsPersistedAckAfterRestart()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var eventsSchema = $"project_facts_{Guid.NewGuid():N}";
        var eventsOptions = new AcceptedEffectRuntimeOptions(
            Issuer, "events-tests", new Uri("https://knowledge.test/"),
            new Uri("https://projects.test/"), eventsSchema);
        await EventsAndSessionsMigrator.MigrateAsync(postgres.DataSource, eventsSchema);

        var switchingHandler = new SwitchingHandler();
        switchingHandler.SetTarget(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        await using var knowledgeApp = await CreateKnowledgeAppAsync(database, switchingHandler);
        using var knowledgeClient = knowledgeApp.GetTestClient();

        var deniedEvents = await CreateEventsAppAsync(
            postgres.DataSource,
            eventsOptions,
            knowledgeApp.GetTestServer().CreateHandler(),
            writeAllowed: false);
        switchingHandler.SetTarget(deniedEvents.GetTestServer().CreateHandler());

        var proposalId = await CreateProposalAsync(knowledgeClient);
        using var firstAttempt = await PromoteAsync(knowledgeClient, proposalId);
        Assert.Equal(HttpStatusCode.Created, firstAttempt.StatusCode);
        using var firstResult = JsonDocument.Parse(await firstAttempt.Content.ReadAsStringAsync());
        var receiptId = firstResult.RootElement.GetProperty("outboxEventId").GetGuid();
        Assert.Equal("PENDING", firstResult.RootElement.GetProperty("delivery").GetString());
        Assert.True(
            firstResult.RootElement.GetProperty("deliveryCode").GetString() == "events_forbidden",
            firstResult.RootElement.GetRawText());
        await AssertOutboxStateAsync(database, receiptId, delivered: false);
        await AssertFactAndInboxCountAsync(postgres.DataSource, eventsSchema, count: 0);

        await deniedEvents.DisposeAsync();
        var failingEvents = await CreateEventsAppAsync(
            postgres.DataSource,
            eventsOptions,
            knowledgeApp.GetTestServer().CreateHandler(),
            writeAllowed: true);
        await InstallProjectFactInsertFailureAsync(postgres.DataSource, eventsSchema);
        switchingHandler.SetTarget(failingEvents.GetTestServer().CreateHandler());

        using var commitFailure = await PromoteAsync(knowledgeClient, proposalId, eventsToken: "events-token");
        Assert.Equal(HttpStatusCode.OK, commitFailure.StatusCode);
        using var failureResult = JsonDocument.Parse(await commitFailure.Content.ReadAsStringAsync());
        Assert.Equal("PENDING", failureResult.RootElement.GetProperty("delivery").GetString());
        Assert.Equal("events_unavailable", failureResult.RootElement.GetProperty("deliveryCode").GetString());
        await AssertOutboxStateAsync(database, receiptId, delivered: false);
        await AssertFactAndInboxCountAsync(postgres.DataSource, eventsSchema, count: 0);
        await RemoveProjectFactInsertFailureAsync(postgres.DataSource, eventsSchema);
        await failingEvents.DisposeAsync();

        var eventsAfterRestart = await CreateEventsAppAsync(
            postgres.DataSource,
            eventsOptions,
            knowledgeApp.GetTestServer().CreateHandler(),
            writeAllowed: true);
        switchingHandler.SetTarget(eventsAfterRestart.GetTestServer().CreateHandler());

        using var retried = await PromoteAsync(knowledgeClient, proposalId, eventsToken: "events-token");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        using var retryResult = JsonDocument.Parse(await retried.Content.ReadAsStringAsync());
        Assert.True(retryResult.RootElement.GetProperty("isDuplicate").GetBoolean());
        Assert.Equal(receiptId, retryResult.RootElement.GetProperty("outboxEventId").GetGuid());
        Assert.Equal("DELIVERED", retryResult.RootElement.GetProperty("delivery").GetString());
        var firstAcknowledgment = retryResult.RootElement.GetProperty("deliveryAcknowledgment").Clone();
        Assert.Equal(receiptId, firstAcknowledgment.GetProperty("receiptId").GetGuid());
        Assert.Equal(1, firstAcknowledgment.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, firstAcknowledgment.GetProperty("eventVersion").GetInt32());
        Assert.Equal("project-a", firstAcknowledgment.GetProperty("projectId").GetString());
        Assert.Equal(1, firstAcknowledgment.GetProperty("sequence").GetInt64());
        Assert.Equal("Bearer events-token", switchingHandler.LastAuthorization);
        Assert.Equal("Bearer caller-token", switchingHandler.LastKnowledgeAuthorization);
        await AssertOutboxStateAsync(database, receiptId, delivered: true);
        await AssertFactAndInboxCountAsync(postgres.DataSource, eventsSchema, count: 1);
        await AssertTokensNotPersistedAsync(
            database, postgres.DataSource, eventsSchema, receiptId, "caller-token", "events-token");

        await eventsAfterRestart.DisposeAsync();
        var restartedEvents = await CreateEventsAppAsync(
            postgres.DataSource,
            eventsOptions,
            knowledgeApp.GetTestServer().CreateHandler(),
            writeAllowed: true);
        using (var client = restartedEvents.GetTestClient())
        using (var replayRequest = new HttpRequestMessage(
                   HttpMethod.Post, "/internal/project-facts/accepted-effects")
               {
                   Content = JsonContent.Create(
                       new AcceptedEffectDeliveryRequest(
                           receiptId,
                           AcceptedEffectContractVersions.CurrentSchemaVersion,
                           AcceptedEffectContractVersions.CurrentEventVersion),
                       options: JsonOptions)
               })
        {
            replayRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");
            replayRequest.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-a");
            using var replayed = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            using var replayedAck = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());
            Assert.Equal(firstAcknowledgment.GetRawText(), replayedAck.RootElement.GetRawText());
        }

        using var stableRetry = await PromoteAsync(knowledgeClient, proposalId);
        Assert.Equal(HttpStatusCode.OK, stableRetry.StatusCode);
        using var stableResult = JsonDocument.Parse(await stableRetry.Content.ReadAsStringAsync());
        Assert.Equal(receiptId, stableResult.RootElement.GetProperty("outboxEventId").GetGuid());
        Assert.Equal("DELIVERED", stableResult.RootElement.GetProperty("delivery").GetString());

        await restartedEvents.DisposeAsync();
        await using var cleanup = await postgres.DataSource.OpenConnectionAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{eventsSchema}\" CASCADE", cleanup);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ReleaseFailureKeepsPromotionPendingAndLogsOnlyBoundedDiagnostics()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var logs = new CapturingLoggerProvider();
        var switchingHandler = new SwitchingHandler();
        switchingHandler.SetTarget(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        await using var knowledgeApp = await CreateKnowledgeAppAsync(database, switchingHandler, logs);
        using var client = knowledgeApp.GetTestClient();
        var proposalId = await CreateProposalAsync(client);
        await InstallOutboxReleaseFailureAsync(postgres.DataSource, database.Options.Schema);

        using var promoted = await PromoteAsync(client, proposalId);
        using var result = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
        var receiptId = result.RootElement.GetProperty("outboxEventId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);
        Assert.Equal("PENDING", result.RootElement.GetProperty("delivery").GetString());
        Assert.Equal("events_unavailable", result.RootElement.GetProperty("deliveryCode").GetString());
        Assert.Contains(logs.Entries, entry =>
            entry.Contains("FailureCode=events_unavailable", StringComparison.Ordinal) &&
            entry.Contains("FailureType=PostgresException", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain("caller-token", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", entry, StringComparison.Ordinal);
            Assert.DoesNotContain(receiptId.ToString("D"), entry, StringComparison.Ordinal);
        });
        await AssertOutboxStateAsync(database, receiptId, delivered: false);
    }

    [Fact]
    public async Task ReusingReceiptIdWithChangedReceiptReturnsConflict()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var eventsSchema = $"project_conflicts_{Guid.NewGuid():N}";
        var eventsOptions = new AcceptedEffectRuntimeOptions(
            Issuer, "events-tests", new Uri("https://knowledge.test/"),
            new Uri("https://projects.test/"), eventsSchema);
        await EventsAndSessionsMigrator.MigrateAsync(postgres.DataSource, eventsSchema);

        var switchingHandler = new SwitchingHandler();
        switchingHandler.SetTarget(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        await using var knowledgeApp = await CreateKnowledgeAppAsync(database, switchingHandler);
        using var knowledgeClient = knowledgeApp.GetTestClient();
        var proposalId = await CreateProposalAsync(knowledgeClient);
        using var promoted = await PromoteAsync(knowledgeClient, proposalId);
        using var result = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
        var receiptId = result.RootElement.GetProperty("outboxEventId").GetGuid();
        using var getReceipt = new HttpRequestMessage(
            HttpMethod.Get, $"/internal/accepted-effects/{receiptId:D}");
        getReceipt.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");
        using var receiptResponse = await knowledgeClient.SendAsync(getReceipt);
        var receipt = await receiptResponse.Content.ReadFromJsonAsync<AcceptedEffectReceipt>(JsonOptions);
        Assert.NotNull(receipt);

        var mutableReceipt = new MutableReceiptHandler(receipt!);
        var eventApp = await CreateEventsAppAsync(
            postgres.DataSource, eventsOptions, mutableReceipt, writeAllowed: true);
        using var client = eventApp.GetTestClient();

        using var first = await SendEffectAsync(client, receiptId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        mutableReceipt.Receipt = receipt with { AcceptedAt = receipt.AcceptedAt.AddSeconds(1) };
        using var conflicting = await SendEffectAsync(client, receiptId);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);

        await eventApp.DisposeAsync();
        await using var cleanup = await postgres.DataSource.OpenConnectionAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{eventsSchema}\" CASCADE", cleanup);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task UnexpectedJournalFailureLogsOnlyBoundedDiagnostics()
    {
        var receiptId = Guid.NewGuid();
        var options = new AcceptedEffectRuntimeOptions(
            Issuer, "events-tests", new Uri("https://knowledge.test/"),
            new Uri("https://projects.test/"), "unused_events_schema");
        var receipt = new AcceptedEffectReceipt(
            receiptId, 1, 1, "project-a", "run-a", Guid.NewGuid(), Guid.NewGuid(), 1,
            Issuer.AbsoluteUri, "actor-a", "tenant-a", "project-a", "run-a",
            ProjectAuthorityResourceType.Project, "project-a", 1, 1, 1, 1,
            "context-v1", DateTimeOffset.UtcNow);
        var logs = new CapturingLoggerProvider();
        await using var eventsApp = await CreateEventsAppAsync(
            postgres.DataSource,
            options,
            new MutableReceiptHandler(receipt),
            writeAllowed: true,
            journal: new FailingProjectFactJournal($"never-log-this {receiptId} caller-token"),
            loggerProvider: logs);
        using var client = eventsApp.GetTestClient();
        using var response = await SendEffectAsync(client, receiptId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(logs.Entries, entry =>
            entry.Contains("FailureCode=project_fact_unavailable", StringComparison.Ordinal) &&
            entry.Contains("FailureType=InvalidOperationException", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(receiptId.ToString("D"), entry, StringComparison.Ordinal);
            Assert.DoesNotContain("caller-token", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("never-log-this", entry, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EventsRejectsDifferentActorRunAndPurposeBoundTokens()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var eventsSchema = $"project_forgery_{Guid.NewGuid():N}";
        var eventsOptions = new AcceptedEffectRuntimeOptions(
            Issuer, "events-tests", new Uri("https://knowledge.test/"),
            new Uri("https://projects.test/"), eventsSchema);
        await EventsAndSessionsMigrator.MigrateAsync(postgres.DataSource, eventsSchema);

        var switchingHandler = new SwitchingHandler();
        switchingHandler.SetTarget(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        await using var knowledgeApp = await CreateKnowledgeAppAsync(database, switchingHandler);
        using var knowledgeClient = knowledgeApp.GetTestClient();
        var proposalId = await CreateProposalAsync(knowledgeClient);
        using var promoted = await PromoteAsync(knowledgeClient, proposalId);
        using var result = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
        var receiptId = result.RootElement.GetProperty("outboxEventId").GetGuid();

        var receiptHandler = new CountingHandler(knowledgeApp.GetTestServer().CreateHandler());
        var eventsApp = await CreateEventsAppAsync(
            postgres.DataSource,
            eventsOptions,
            receiptHandler,
            writeAllowed: true);
        using var client = eventsApp.GetTestClient();
        foreach (var token in new[]
        {
            "missing-issuer-token",
            "cross-issuer-token",
            "duplicate-bound-token",
            "cross-identity-token",
            "wrong-raw-issuer-token",
        })
        {
            using var invalidCaller = await SendEffectAsync(client, receiptId, token, "caller-token");
            Assert.True(
                invalidCaller.StatusCode == HttpStatusCode.Forbidden,
                $"{token}: {await invalidCaller.Content.ReadAsStringAsync()}");
        }
        Assert.Equal(0, receiptHandler.Requests);

        using var wrongActor = await SendEffectAsync(
            client, receiptId, token: "other-actor-token", knowledgeToken: "caller-token");
        using var wrongRun = await SendEffectAsync(
            client, receiptId, token: "other-run-token", knowledgeToken: "caller-token");
        using var purposeBound = await SendEffectAsync(
            client, receiptId, token: "purpose-token", knowledgeToken: "caller-token");

        Assert.True(
            wrongActor.StatusCode == HttpStatusCode.Forbidden,
            await wrongActor.Content.ReadAsStringAsync());
        Assert.True(
            wrongRun.StatusCode == HttpStatusCode.Forbidden,
            await wrongRun.Content.ReadAsStringAsync());
        Assert.True(
            purposeBound.StatusCode == HttpStatusCode.Forbidden,
            await purposeBound.Content.ReadAsStringAsync());
        foreach (var response in new[] { wrongActor, wrongRun, purposeBound })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("other-actor-token", body, StringComparison.Ordinal);
            Assert.DoesNotContain("other-run-token", body, StringComparison.Ordinal);
            Assert.DoesNotContain("purpose-token", body, StringComparison.Ordinal);
        }
        await AssertFactAndInboxCountAsync(postgres.DataSource, eventsSchema, count: 0);

        await eventsApp.DisposeAsync();
        await using var cleanup = await postgres.DataSource.OpenConnectionAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{eventsSchema}\" CASCADE", cleanup);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task EventsAcceptsUnboundCallerWithCurrentProjectWriteAuthority()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var eventsSchema = $"project_unbound_{Guid.NewGuid():N}";
        var eventsOptions = new AcceptedEffectRuntimeOptions(
            Issuer, "events-tests", new Uri("https://knowledge.test/"),
            new Uri("https://projects.test/"), eventsSchema);
        await EventsAndSessionsMigrator.MigrateAsync(postgres.DataSource, eventsSchema);

        var switchingHandler = new SwitchingHandler();
        switchingHandler.SetTarget(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        await using var knowledgeApp = await CreateKnowledgeAppAsync(database, switchingHandler);
        using var knowledgeClient = knowledgeApp.GetTestClient();
        var proposalId = await CreateProposalAsync(knowledgeClient);
        using var promoted = await PromoteAsync(knowledgeClient, proposalId);
        using var result = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
        var receiptId = result.RootElement.GetProperty("outboxEventId").GetGuid();
        using var getReceipt = new HttpRequestMessage(
            HttpMethod.Get, $"/internal/accepted-effects/{receiptId:D}");
        getReceipt.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");
        using var receiptResponse = await knowledgeClient.SendAsync(getReceipt);
        var receipt = await receiptResponse.Content.ReadFromJsonAsync<AcceptedEffectReceipt>(JsonOptions);
        Assert.NotNull(receipt);

        var owner = await CreateEventsAppAsync(
            postgres.DataSource,
            eventsOptions,
            new MutableReceiptHandler(receipt! with { BoundProjectId = null, BoundRunId = null }),
            writeAllowed: true,
            bindCaller: false);
        using var client = owner.GetTestClient();
        using var appended = await SendEffectAsync(client, receiptId, token: "unbound-token");

        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
        using var acknowledgment = JsonDocument.Parse(await appended.Content.ReadAsStringAsync());
        Assert.Equal(receiptId, acknowledgment.RootElement.GetProperty("receiptId").GetGuid());
        await AssertFactAndInboxCountAsync(postgres.DataSource, eventsSchema, count: 1);

        await owner.DisposeAsync();
        await using var cleanup = await postgres.DataSource.OpenConnectionAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{eventsSchema}\" CASCADE", cleanup);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> CreateProposalAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(new
            {
                kind = "proposal",
                type = "architectural",
                title = "Private decision",
                content = "never forward proposal content",
                rationale = "private rationale",
                importance = "medium",
                tags = new[] { "relay" }
            }, options: JsonOptions)
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-a");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "integration-proposal");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("record").GetProperty("recordId").GetGuid();
    }

    private static Task<HttpResponseMessage> PromoteAsync(
        HttpClient client,
        Guid proposalId,
        string? eventsToken = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/project-a/runs/run-a/agents/agent-a/proposals/{proposalId:D}/promote")
        {
            Content = JsonContent.Create(new { expectedRevision = 1 }, options: JsonOptions)
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");
        if (eventsToken is not null)
            request.Headers.TryAddWithoutValidation(
                "X-Agentweaver-Events-Authorization", $"Bearer {eventsToken}");
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-a");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "integration-promotion");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendEffectAsync(
        HttpClient client,
        Guid receiptId,
        string token = "caller-token",
        string? knowledgeToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/project-facts/accepted-effects")
        {
            Content = JsonContent.Create(new AcceptedEffectDeliveryRequest(
                receiptId,
                AcceptedEffectContractVersions.CurrentSchemaVersion,
                AcceptedEffectContractVersions.CurrentEventVersion), options: JsonOptions)
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (knowledgeToken is not null)
            request.Headers.TryAddWithoutValidation(
                "X-Agentweaver-Knowledge-Authorization", $"Bearer {knowledgeToken}");
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-a");
        return client.SendAsync(request);
    }

    private static async Task InstallProjectFactInsertFailureAsync(
        NpgsqlDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION "{schema}".reject_project_fact_insert()
            RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                RAISE EXCEPTION 'blocked project fact insert for transaction test';
                RETURN NEW;
            END
            $body$;
            CREATE TRIGGER reject_project_fact_insert
            BEFORE INSERT ON "{schema}".project_facts
            FOR EACH ROW EXECUTE FUNCTION "{schema}".reject_project_fact_insert();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InstallOutboxReleaseFailureAsync(
        NpgsqlDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION "{schema}".reject_outbox_release()
            RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF OLD.lease_token IS NOT NULL AND NEW.lease_token IS NULL THEN
                    RAISE EXCEPTION 'blocked outbox lease release for diagnostic test';
                END IF;
                RETURN NEW;
            END
            $body$;
            CREATE TRIGGER reject_outbox_release
            BEFORE UPDATE ON "{schema}".outbox_events
            FOR EACH ROW EXECUTE FUNCTION "{schema}".reject_outbox_release();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveProjectFactInsertFailureAsync(
        NpgsqlDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            DROP TRIGGER IF EXISTS reject_project_fact_insert ON "{schema}".project_facts;
            DROP FUNCTION IF EXISTS "{schema}".reject_project_fact_insert();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<WebApplication> CreateKnowledgeAppAsync(
        NativePostgresMemoryProviderTests.KnowledgeDatabase database,
        HttpMessageHandler eventsHandler,
        ILoggerProvider? loggerProvider = null)
    {
        var provider = database.Provider;
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [new ProviderRegistration(provider.Descriptor, true,
                database.Options.OptionsRevision, database.Options.OptionsSchemaVersion)],
            [new ProviderSelection(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)],
            [new ProviderOverridePermission(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)]).Value);
        var runtimeOptions = new KnowledgeRuntimeOptions(
            Issuer, "knowledge-tests", new Uri("https://projects.test/"),
            new Uri("https://events.test/"), "events-tests", database.Options, 100, 20, 1000);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (loggerProvider is not null)
            builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(new PostgresOutbox(database.DataSource, database.Options.Schema));
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(new ProviderResolver(catalog));
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<IMemoryProvider>(provider);
        builder.Services.AddSingleton<IReadOnlyDictionary<string, IMemoryProvider>>(
            new Dictionary<string, IMemoryProvider>(StringComparer.Ordinal)
            {
                [NativePostgresMemoryProvider.ProviderId] = provider
            });
        builder.Services.AddSingleton<ProjectsConfigClient>(services => new ProjectsConfigClient(
            new HttpClient(new KnowledgeProjectsHandler(database.Options))
            {
                BaseAddress = runtimeOptions.ProjectsConfigBaseAddress
            },
            services.GetRequiredService<IHttpContextAccessor>(),
            runtimeOptions));
        builder.Services.AddSingleton(new HttpClient(eventsHandler)
        {
            BaseAddress = runtimeOptions.EventsBaseAddress
        });
        builder.Services.AddSingleton<AcceptedEffectRelay>();
        builder.Services.AddScoped<KnowledgeProviderBindingService>();
        builder.Services.AddSingleton<MemoryContextCompiler>();
        builder.Services.AddScoped<KnowledgeApplicationService>();
        ConfigureJson(builder);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKnowledgeEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreateEventsAppAsync(
        NpgsqlDataSource dataSource,
        AcceptedEffectRuntimeOptions options,
        HttpMessageHandler knowledgeHandler,
        bool writeAllowed,
        bool bindCaller = true,
        IProjectFactJournal? journal = null,
        ILoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (loggerProvider is not null)
            builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton<IProjectFactJournal>(
            journal ?? new PostgresProjectFactJournal(dataSource, options));
        builder.Services.AddSingleton<KnowledgeAcceptedEffectReceiptClient>(services =>
            new KnowledgeAcceptedEffectReceiptClient(
                new HttpClient(knowledgeHandler) { BaseAddress = options.KnowledgeBaseAddress },
                services.GetRequiredService<IHttpContextAccessor>()));
        builder.Services.AddSingleton<ProjectsConfigAuthorizationClient>(services =>
            new ProjectsConfigAuthorizationClient(
                new HttpClient(new ProjectOwnerHandler(writeAllowed, bindCaller))
                {
                    BaseAddress = options.ProjectsConfigBaseAddress
                },
                services.GetRequiredService<IHttpContextAccessor>(),
                options));
        builder.Services.AddScoped<AcceptedEffectApplicationService>();
        ConfigureJson(builder);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAcceptedEffectEndpoints();
        await app.StartAsync();
        return app;
    }

    private static void ConfigureJson(WebApplicationBuilder builder) =>
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

    private static async Task AssertOutboxStateAsync(
        NativePostgresMemoryProviderTests.KnowledgeDatabase database,
        Guid receiptId,
        bool delivered)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT delivered_at IS NOT NULL FROM \"{database.Options.Schema}\".outbox_events WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", receiptId);
        Assert.Equal(delivered, (bool)(await command.ExecuteScalarAsync())!);
    }

    private static async Task AssertFactAndInboxCountAsync(
        NpgsqlDataSource dataSource,
        string schema,
        int count)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{schema}".project_facts),
                (SELECT count(*) FROM "{schema}".consumer_inbox_receipts
                    WHERE consumer_id = 'events-and-sessions.accepted-effects'),
                (SELECT count(*) FROM "{schema}".project_fact_streams),
                (SELECT count(*) FROM "{schema}".session_events)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(count, reader.GetInt64(0));
        Assert.Equal(count, reader.GetInt64(1));
        Assert.Equal(count, reader.GetInt64(2));
        Assert.Equal(0, reader.GetInt64(3));
    }

    private static async Task AssertTokensNotPersistedAsync(
        NativePostgresMemoryProviderTests.KnowledgeDatabase knowledge,
        NpgsqlDataSource eventsDataSource,
        string eventsSchema,
        Guid receiptId,
        params string[] tokens)
    {
        await using (var connection = await knowledge.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         $"SELECT payload::text FROM \"{knowledge.Options.Schema}\".outbox_events WHERE id = @id",
                         connection))
        {
            command.Parameters.AddWithValue("id", receiptId);
            var payload = (string)(await command.ExecuteScalarAsync())!;
            Assert.All(tokens, token => Assert.DoesNotContain(token, payload, StringComparison.Ordinal));
        }
        await using (var connection = await eventsDataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         $"SELECT receipt::text FROM \"{eventsSchema}\".project_facts WHERE receipt_id = @id",
                         connection))
        {
            command.Parameters.AddWithValue("id", receiptId);
            var receipt = (string)(await command.ExecuteScalarAsync())!;
            Assert.All(tokens, token => Assert.DoesNotContain(token, receipt, StringComparison.Ordinal));
        }
    }

    private sealed class ProjectOwnerHandler(bool writeAllowed, bool bindCaller) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/authorization/context")
                throw new InvalidOperationException("Unexpected project authority route.");
            var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
            permissions.Add(new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1));
            if (writeAllowed)
                permissions.Add(new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.WriteProjects, 2));
            var authority = new ProjectAuthorizationContextResponse(
                1, Issuer.AbsoluteUri, "actor-a", "tenant-a", 3,
                bindCaller ? "project-a" : null,
                bindCaller ? "run-a" : null,
                [new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project, "project-a", permissions.ToImmutable())]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(authority, options: JsonOptions)
            });
        }

    }

    private sealed class KnowledgeProjectsHandler(NativePostgresMemoryOptions options) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/authorization/context" => new ProjectAuthorizationContextResponse(
                    1, Issuer.AbsoluteUri, "actor-a", "tenant-a", 3, "project-a", "run-a",
                    [new EffectiveProjectAuthorization(
                        ProjectAuthorityResourceType.Project,
                        "project-a",
                        [
                            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1),
                            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.WriteProjects, 2),
                            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadRunSelection, 1)
                        ])]),
                "/api/projects/project-a/runs/run-a/selection" => new ProjectRunSelectionResponse(
                    "project-a", "run-a", 1, 1, 1, "context-v1",
                    [new EffectiveProviderSelection(
                        ProviderCardinality.Exclusive,
                        ProviderSeam.Memory,
                        [new EffectiveProviderCandidate(
                            ProviderSeam.Memory,
                            NativePostgresMemoryProvider.ProviderId,
                            NativePostgresMemoryProvider.AdapterVersion.ToString(),
                            options.OptionsSchemaVersion,
                            options.OptionsRevision,
                            ProviderHostingPattern.RemoteService,
                            MemoryProviderCapabilities.All.ToImmutableArray(),
                            MemoryProviderCapabilities.All.ToImmutableArray())])],
                    new ProjectRunLimitSnapshot(1000)),
                _ => throw new InvalidOperationException("Unexpected Projects & Config route.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            });
        }
    }

    private sealed class MutableReceiptHandler(AcceptedEffectReceipt receipt) : HttpMessageHandler
    {
        private int _requests;

        public AcceptedEffectReceipt Receipt { get; set; } = receipt;
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Receipt, options: JsonOptions)
            });
        }
    }

    private sealed class FailingProjectFactJournal(string message) : IProjectFactJournal
    {
        public Task<ProjectFactAcknowledgment> AppendAcceptedEffectAsync(
            AcceptedEffectReceipt receipt,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ProjectFactAcknowledgment>(new InvalidOperationException(message));
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class SwitchingHandler : HttpMessageHandler
    {
        private HttpMessageInvoker? _target;
        public string? LastAuthorization { get; private set; }
        public string? LastKnowledgeAuthorization { get; private set; }

        public void SetTarget(HttpMessageHandler handler)
        {
            _target?.Dispose();
            _target = new HttpMessageInvoker(handler, disposeHandler: true);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastKnowledgeAuthorization = request.Headers.TryGetValues(
                "X-Agentweaver-Knowledge-Authorization", out var values)
                ? values.SingleOrDefault()
                : null;
            return (_target ?? throw new InvalidOperationException("The downstream target is not configured."))
                .SendAsync(request, cancellationToken);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyCollection<string> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            ConcurrentQueue<string> entries,
            string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    entries.Enqueue($"{logLevel} {categoryName} {formatter(state, exception)}");
            }
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = AuthenticationHeaderValue.TryParse(
                Request.Headers.Authorization.ToString(), out var authorization)
                ? authorization.Parameter ?? string.Empty
                : string.Empty;
            var subject = token.Contains("other-actor-token", StringComparison.Ordinal)
                ? "actor-b"
                : "actor-a";
            var runId = token.Contains("other-run-token", StringComparison.Ordinal)
                ? "run-b"
                : "run-a";
            var claims = new List<Claim>
            {
                IssuedClaim(
                    "sub",
                    subject,
                    token == "missing-issuer-token" ? string.Empty : Issuer.AbsoluteUri),
                IssuedClaim("tenant_id", "tenant-a", Issuer.AbsoluteUri)
            };
            if (token != "unbound-token" && token != "cross-identity-token")
            {
                claims.Add(IssuedClaim(
                    "project_id",
                    "project-a",
                    token == "cross-issuer-token" ? "https://other-identity.test/" : Issuer.AbsoluteUri));
                claims.Add(IssuedClaim("run_id", runId, Issuer.AbsoluteUri));
            }
            if (token == "duplicate-bound-token")
                claims.Add(IssuedClaim("project_id", "project-a", Issuer.AbsoluteUri));
            if (token == "wrong-raw-issuer-token")
                claims.Add(IssuedClaim("iss", "https://other-identity.test/", Issuer.AbsoluteUri));
            if (token.Contains("purpose-token", StringComparison.Ordinal))
                claims.Add(IssuedClaim("purpose", "secret-redemption", Issuer.AbsoluteUri));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            if (token == "cross-identity-token")
                principal.AddIdentity(new ClaimsIdentity(
                    [
                        IssuedClaim("project_id", "project-a", Issuer.AbsoluteUri),
                        IssuedClaim("run_id", runId, Issuer.AbsoluteUri)
                    ],
                    Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }

        private static Claim IssuedClaim(string type, string value, string issuer) =>
            new(type, value, ClaimValueTypes.String, issuer);
    }

}
