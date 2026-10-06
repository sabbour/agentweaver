extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using EventsHost::Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private static readonly JsonSerializerOptions CoordinationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending()
    {
        using var signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(signingCertificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, signingKey);

        var platformAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "coordination-admin", null, null, ["platform_admin"]);
        var platformAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(platformAdminToken).Claims, "sub");
        var platformAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, platformAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        using var createProject = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
        {
            Content = JsonContent.Create(new CreateProjectRequest { Name = "Coordination integration" })
        };
        AddBearerAndTenant(createProject, platformAdminToken, TenantId);
        using var createdProject = await projects.Client.SendAsync(createProject);
        Assert.Equal(HttpStatusCode.Created, createdProject.StatusCode);
        var project = await createdProject.Content.ReadFromJsonAsync<ProjectSummary>(AuthorizationJsonOptions);
        Assert.NotNull(project);

        var bootstrapToken = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], "coordination-runner", null, null, ["orchestrator"]);
        var runnerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(bootstrapToken).Claims, "sub");
        var runnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, runnerSubject, TenantId);
        var runnerRole = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await CreateRunBindingGrantAsync(runnerSubject, project.ProjectId, RunId);
        var runToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "coordination-runner",
            project.ProjectId,
            RunId,
            ["platform_admin"]);
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(runToken).Claims.ToArray();
        Assert.Contains(claims, claim => claim.Type == "aud" && claim.Value == "https://api.test");
        Assert.Contains(claims, claim => claim.Type == "project_id" && claim.Value == project.ProjectId);
        Assert.Contains(claims, claim => claim.Type == "run_id" && claim.Value == RunId);

        using var updateDefaults = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
        {
            Content = JsonContent.Create(new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = new PlatformRuntimeDefaults
                {
                    ModelSelection = new ModelSelectionSettings("platform-model"),
                    EgressBaseline = [],
                    RunLimits = new CopilotRunLimits
                    {
                        MaxModelTurns = 12,
                        MaxToolCalls = 100,
                        MaxChildren = 4,
                        MaxConcurrentChildren = 2,
                        MaxWallTimeSeconds = 3600,
                        MaxPromptTokens = 20000
                    }
                }
            })
        };
        AddBearerAndTenant(updateDefaults, platformAdminToken, TenantId);
        using var updatedDefaults = await projects.Client.SendAsync(updateDefaults);
        Assert.Equal(HttpStatusCode.OK, updatedDefaults.StatusCode);

        using var acceptSelection = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
        {
            Content = JsonContent.Create(new AcceptRunSelectionRequest
            {
                ExpectedProjectConfigRevision = project.ConfigurationRevision,
                ExpectedPlatformRuntimeRevision = 1,
                Context = new RunSelectionContext
                {
                    Revision = "provider-catalog-v1",
                    AvailableModelSelectionReferences = ImmutableHashSet.Create(
                        StringComparer.Ordinal, "platform-model")
                }
            })
        };
        AddBearerAndTenant(acceptSelection, runToken, TenantId);
        using var acceptedSelection = await projects.Client.SendAsync(acceptSelection);
        Assert.Equal(HttpStatusCode.OK, acceptedSelection.StatusCode);

        var ownerSchema = "coordination_it_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "events_it_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        using var serviceEnvironment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["ConnectionStrings__Orchestrator"] = _connectionString,
            ["Orchestrator__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["Orchestrator__Schema"] = ownerSchema,
            ["ConnectionStrings__EventsAndSessions"] = _connectionString,
            ["EventsAndSessions__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["EventsAndSessions__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["EventsAndSessions__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["EventsAndSessions__Provider__ResourceId"] = "postgres-test",
            ["EventsAndSessions__Provider__DatabaseName"] = databaseName,
            ["EventsAndSessions__Provider__ResourceGeneration"] = "1",
            ["EventsAndSessions__Provider__Schema"] = eventsSchema,
            ["EventsAndSessions__Provider__OptionsRevision"] = "integration-v1",
            ["EventsAndSessions__Messaging__OptionsRevision"] = "integration-messaging-v1",
            ["ProjectsConfig__AuthorizationContext__OwnerBaseAddress"] = "https://projects.test/",
            ["ProjectsConfig__AuthorizationContext__Audience"] = "https://api.test",
            ["EventsAndSessions__Authorization__OwnerBaseAddress"] = "https://events.test/",
            ["EventsAndSessions__Authorization__Audience"] = "https://api.test",
            ["EventsAndSessions__OrchestratorOwner__OwnerBaseAddress"] = "https://orchestrator.test/",
            ["EventsAndSessions__OrchestratorOwner__Audience"] = "https://api.test",
            ["Identity__Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
            ["Identity__Audience"] = "https://api.test"
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsAndSessionsMigrator.MigrateAsync(migrations, eventsSchema);
        }

        EventsIntegrationFactory? eventsFactoryReference = null;
        var admissions = new List<(HttpStatusCode Status, bool NoStore, string Body)>();
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString,
            ownerSchema,
            signingKey,
            projects.CreateHandler,
            () => new CapturingHandler(
                eventsFactoryReference!.Server.CreateHandler(),
                (status, noStore, body) => admissions.Add((status, noStore, body))));
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString,
            eventsSchema,
            signingKey,
            projects.CreateHandler,
            () => orchestratorFactory.Server.CreateHandler());
        eventsFactoryReference = eventsFactory;

        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test")
        });

        using var rootResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/root",
            runToken,
            new AcceptRootRequest("root"));
        await AssertStatusAsync(rootResponse, HttpStatusCode.Created);
        var root = await ReadJsonAsync<AcceptedRoot>(rootResponse);
        Assert.Equal("root", root.RootSessionId);

        using var childResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
            runToken,
            new RegisterChildRequest("child"));
        await AssertStatusAsync(childResponse, HttpStatusCode.Created);
        var child = await ReadJsonAsync<RegisteredChild>(childResponse);

        using var childReplay = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
            runToken,
            new RegisterChildRequest("child"));
        await AssertStatusAsync(childReplay, HttpStatusCode.Created);
        Assert.Equal(child, await ReadJsonAsync<RegisteredChild>(childReplay));

        using var events = eventsFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://events.test")
        });
        using var forgedAdmission = await SendJsonAsync(
            events,
            HttpMethod.Post,
            "/internal/addressed-messages/admit",
            runToken,
            new OwnerOutboundMessage(
                Guid.NewGuid(),
                new SessionIdentity(project.ProjectId, RunId, "root"),
                child.Identity,
                "forged-message",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.Question,
                AddressedMessageKind.Question,
                Payload("""{"text":"forged"}"""),
                root.ExecutionFence,
                child.ExecutionFence,
                Guid.NewGuid(),
                null,
                child.PendingRequestId,
                null,
                null,
                null));
        Assert.Equal(HttpStatusCode.BadGateway, forgedAdmission.StatusCode);

        using var sendQuestion = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/messages",
            runToken,
            new CoordinationMessageRequest(
                child.Identity.SessionId,
                "root-question",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.Question,
                AddressedMessageKind.Question,
                Payload("""{"text":"question"}"""),
                RequestId: child.PendingRequestId));
        Assert.True(sendQuestion.StatusCode == HttpStatusCode.OK,
            $"Events admission response: {string.Join("; ", admissions)}; " +
            await sendQuestion.Content.ReadAsStringAsync());
        var sentQuestion = await ReadJsonAsync<CoordinationMessageResult>(sendQuestion);

        using var childBindingResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/owner-binding",
            runToken,
            [TenantId]);
        await AssertStatusAsync(childBindingResponse, HttpStatusCode.OK);
        Assert.True(childBindingResponse.Headers.CacheControl?.NoStore);
        var childBinding = await ReadJsonAsync<CoordinationSessionBinding>(childBindingResponse);
        Assert.False(childBinding.PendingWake);

        using var boundaryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/turn-boundary",
            runToken,
            new TurnBoundaryRequest(child.ExecutionFence, childBinding.StateVersion));
        await AssertStatusAsync(boundaryResponse, HttpStatusCode.OK);
        var boundary = await ReadJsonAsync<TurnBoundaryResult>(boundaryResponse);
        var presented = Assert.IsType<AddressedMessageEnvelope>(boundary.PresentedMessage);
        Assert.NotEqual(Guid.Empty, sentQuestion.OwnerMessageId);
        Assert.Equal("child", presented.Recipient.SessionId);
        Assert.Equal(AddressedMessageStatus.Delivered, presented.Status);
        Assert.Equal("active", boundary.ExecutionState);

        using var boundaryRetryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/turn-boundary",
            runToken,
            new TurnBoundaryRequest(child.ExecutionFence, childBinding.StateVersion));
        await AssertStatusAsync(boundaryRetryResponse, HttpStatusCode.OK);
        var boundaryRetry = await ReadJsonAsync<TurnBoundaryResult>(boundaryRetryResponse);
        Assert.Equal(boundary.StateVersion, boundaryRetry.StateVersion);
        Assert.Equal(boundary.PresentedMessage?.MessageId, boundaryRetry.PresentedMessage?.MessageId);

        using var acknowledgmentResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/messages/{presented.MessageId:D}/acknowledge",
            runToken,
            new AddressedMessageClaimFenceRequest(presented.ClaimFence));
        await AssertStatusAsync(acknowledgmentResponse, HttpStatusCode.OK);
        var acknowledged = await ReadJsonAsync<AddressedMessageEnvelope>(acknowledgmentResponse);
        Assert.Equal(AddressedMessageStatus.Acknowledged, acknowledged.Status);
        Assert.NotNull(acknowledged.AcknowledgedAt);
        Assert.Equal("pending", await ReadGateStateAsync(_connectionString, ownerSchema, child.PendingRequestId));

        using var sendReply = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/messages",
            runToken,
            new CoordinationMessageRequest(
                "root",
                "child-reply",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.NeedsInput,
                AddressedMessageKind.Question,
                Payload("""{"text":"reply"}"""),
                ThreadId: presented.ThreadId,
                ReplyToId: sentQuestion.OwnerMessageId,
                RequestId: child.PendingRequestId,
                ReplyCorrelationId: child.PendingRequestId));
        await AssertStatusAsync(sendReply, HttpStatusCode.OK);
        Assert.Equal("input_available",
            await ReadGateStateAsync(_connectionString, ownerSchema, child.PendingRequestId));

        using var rootBindingResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/owner-binding",
            runToken,
            [TenantId]);
        await AssertStatusAsync(rootBindingResponse, HttpStatusCode.OK);
        var rootBinding = await ReadJsonAsync<CoordinationSessionBinding>(rootBindingResponse);
        Assert.True(rootBinding.PendingWake);

        using var rootBoundaryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/turn-boundary",
            runToken,
            new TurnBoundaryRequest(root.ExecutionFence, rootBinding.StateVersion));
        await AssertStatusAsync(rootBoundaryResponse, HttpStatusCode.OK);
        var rootBoundary = await ReadJsonAsync<TurnBoundaryResult>(rootBoundaryResponse);
        var replyPresented = Assert.IsType<AddressedMessageEnvelope>(rootBoundary.PresentedMessage);
        Assert.Equal(AddressedMessageStatus.Delivered, replyPresented.Status);
        var notification = Assert.Single(rootBoundary.ParentNotifications);
        Assert.Equal("child", notification.ChildSessionId);
        Assert.Equal(AddressedMessagePurpose.NeedsInput, notification.Purpose);
        Assert.True(notification.WakesParent);

        var rootEventsBasePath =
            $"/internal/addressed-messages/projects/{project.ProjectId}/runs/{RunId}/sessions/root";
        using var outOfBoundaryClaim = await SendAsync(
            events, HttpMethod.Post, $"{rootEventsBasePath}/claim", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Conflict, outOfBoundaryClaim.StatusCode);
        using var outOfBoundaryPresentation = await SendJsonAsync(
            events,
            HttpMethod.Post,
            $"{rootEventsBasePath}/messages/{replyPresented.MessageId:D}/present",
            runToken,
            new AddressedMessageClaimFenceRequest(replyPresented.ClaimFence));
        Assert.Equal(HttpStatusCode.Conflict, outOfBoundaryPresentation.StatusCode);

        using var replyAcknowledgment = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/messages/{replyPresented.MessageId:D}/acknowledge",
            runToken,
            new AddressedMessageClaimFenceRequest(replyPresented.ClaimFence));
        await AssertStatusAsync(replyAcknowledgment, HttpStatusCode.OK);
        Assert.Equal("input_available",
            await ReadGateStateAsync(_connectionString, ownerSchema, child.PendingRequestId));

        using var replyJournalResponse = await SendAsync(
            events,
            HttpMethod.Get,
            "/internal/sessions/child/events",
            runToken,
            [TenantId]);
        await AssertStatusAsync(replyJournalResponse, HttpStatusCode.OK);
        using var replyJournal = JsonDocument.Parse(await replyJournalResponse.Content.ReadAsStringAsync());
        var replyJournalEvent = Assert.Single(replyJournal.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("addressedMessage", replyJournalEvent.GetProperty("kind").GetString());
        Assert.Equal(
            replyPresented.MessageId,
            replyJournalEvent.GetProperty("payload").GetProperty("messageId").GetGuid());

        using var notificationAcknowledgment = await SendAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/notifications/{notification.NotificationId:D}/acknowledge",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.NoContent, notificationAcknowledgment.StatusCode);

        using var senderReplay = await SendAsync(
            events,
            HttpMethod.Get,
            "/internal/sessions/root/events",
            runToken,
            [TenantId]);
        await AssertStatusAsync(senderReplay, HttpStatusCode.OK);
        using var journal = JsonDocument.Parse(await senderReplay.Content.ReadAsStringAsync());
        var journalEvent = Assert.Single(journal.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("addressedMessage", journalEvent.GetProperty("kind").GetString());
        Assert.Equal(
            presented.MessageId,
            journalEvent.GetProperty("payload").GetProperty("messageId").GetGuid());

        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerRole.AssignmentId,
            1);
        using var revokedBinding = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/owner-binding",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, revokedBinding.StatusCode);
    }

    private static async Task<string> ReadGateStateAsync(
        string connectionString,
        string schema,
        string requestId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT gate_state FROM \"{schema}\".coordination_requests WHERE request_id = @request",
            connection);
        command.Parameters.AddWithValue("request", requestId);
        return (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected the addressed request gate.");
    }

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        T body)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: CoordinationJsonOptions)
        };
        AddBearerAndTenant(request, token, TenantId);
        return await client.SendAsync(request);
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task AssertStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(CoordinationJsonOptions)
        ?? throw new InvalidOperationException($"Expected a {typeof(T).Name} response.");

    private static string TestTokenFile => Path.Combine(Path.GetTempPath(), "agentweaver-test-token.jwt");

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        public TemporaryEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            _original = values.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var (key, value) in values)
                Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in _original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class CapturingHandler(
        HttpMessageHandler innerHandler,
        Action<HttpStatusCode, bool, string> capture) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/internal/addressed-messages/admit", StringComparison.Ordinal) == true)
            {
                var originalContent = response.Content;
                var bytes = await originalContent.ReadAsByteArrayAsync(cancellationToken);
                var replacementContent = new ByteArrayContent(bytes);
                foreach (var header in originalContent.Headers)
                    replacementContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = replacementContent;
                originalContent.Dispose();
                capture(
                    response.StatusCode,
                    response.Headers.CacheControl?.NoStore == true,
                    Encoding.UTF8.GetString(bytes));
            }
            return response;
        }
    }

    private static void AddJwtBearer(IServiceCollection services, SecurityKey key)
    {
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
        });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                    ValidateAudience = true,
                    ValidAudience = "https://api.test",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                };
            });
    }

    private sealed class OrchestratorIntegrationFactory(
        string connectionString,
        string schema,
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandler,
        Func<HttpMessageHandler> eventsHandler)
        : WebApplicationFactory<OrchestratorHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Orchestrator"] = connectionString,
                    ["Orchestrator:Database:WorkloadIdentity:TenantId"] = Guid.NewGuid().ToString(),
                    ["Orchestrator:Database:WorkloadIdentity:ClientId"] = Guid.NewGuid().ToString(),
                    ["Orchestrator:Database:WorkloadIdentity:TokenFilePath"] = TestTokenFile,
                    ["Orchestrator:Schema"] = schema,
                    ["Identity:Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
                    ["Identity:Audience"] = "https://api.test",
                    ["ProjectsConfig:AuthorizationContext:OwnerBaseAddress"] = "https://projects.test/",
                    ["ProjectsConfig:AuthorizationContext:Audience"] = "https://api.test",
                    ["EventsAndSessions:Authorization:OwnerBaseAddress"] = "https://events.test/",
                    ["EventsAndSessions:Authorization:Audience"] = "https://api.test"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(NpgsqlDataSource.Create(connectionString));
                AddJwtBearer(services, signingKey);
                services.AddHttpClient<OrchestratorHost::Agentweaver.Orchestrator.ProjectsRunSelectionClient>()
                    .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                services.AddHttpClient<OrchestratorHost::Agentweaver.Orchestrator.EventsAddressedMessageClient>()
                    .ConfigurePrimaryHttpMessageHandler(eventsHandler);
            });
        }
    }

    private sealed class EventsIntegrationFactory(
        string connectionString,
        string schema,
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandler,
        Func<HttpMessageHandler> orchestratorHandler)
        : WebApplicationFactory<EventsHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:EventsAndSessions"] = connectionString,
                    ["EventsAndSessions:Database:WorkloadIdentity:TenantId"] = Guid.NewGuid().ToString(),
                    ["EventsAndSessions:Database:WorkloadIdentity:ClientId"] = Guid.NewGuid().ToString(),
                    ["EventsAndSessions:Database:WorkloadIdentity:TokenFilePath"] = TestTokenFile,
                    ["EventsAndSessions:Provider:ResourceId"] = "postgres-test",
                    ["EventsAndSessions:Provider:DatabaseName"] = databaseName,
                    ["EventsAndSessions:Provider:ResourceGeneration"] = "1",
                    ["EventsAndSessions:Provider:Schema"] = schema,
                    ["EventsAndSessions:Provider:OptionsRevision"] = "integration-v1",
                    ["EventsAndSessions:Messaging:OptionsRevision"] = "integration-messaging-v1",
                    ["Identity:Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
                    ["Identity:Audience"] = "https://api.test",
                    ["ProjectsConfig:AuthorizationContext:OwnerBaseAddress"] = "https://projects.test/",
                    ["ProjectsConfig:AuthorizationContext:Audience"] = "https://api.test",
                    ["EventsAndSessions:OrchestratorOwner:OwnerBaseAddress"] = "https://orchestrator.test/",
                    ["EventsAndSessions:OrchestratorOwner:Audience"] = "https://api.test"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(NpgsqlDataSource.Create(connectionString));
                AddJwtBearer(services, signingKey);
                services.AddHttpClient<
                        EventsHost::Agentweaver.EventsAndSessions.IProjectsAuthorizationContextClient,
                        EventsHost::Agentweaver.EventsAndSessions.ProjectsAuthorizationContextClient>()
                    .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                services.AddHttpClient<
                        EventsHost::Agentweaver.EventsAndSessions.ICoordinationOwnerClient,
                        EventsHost::Agentweaver.EventsAndSessions.CoordinationOwnerClient>()
                    .ConfigurePrimaryHttpMessageHandler(orchestratorHandler);
            });
        }
    }
}
