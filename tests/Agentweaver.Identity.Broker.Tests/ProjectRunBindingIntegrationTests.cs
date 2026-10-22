extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    /// <summary>Proves fresh interactive selection supports silent binding and first-message start through the Gateway.</summary>
    [Fact]
    public async Task CreatedChatSilentlyBindsAndStartsWithProjectsOwnerProof()
    {
        var signingKey = new X509SecurityKey(_runBindingSigningCertificate);
        // Owners use TestServer transport; the Broker independently queries the same store over pinned TLS.
        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);
        var adminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "chat-admin", null, null, []);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var project = await CreateProjectsTestProjectAsync(projects.Client, adminToken, TenantId, "Silent chat");
        using (var defaultsRequest = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
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
                        MaxPromptTokens = 20_000,
                    },
                },
            }),
        })
        {
            AddBearerAndTenant(defaultsRequest, adminToken, TenantId);
            using var defaultsResponse = await projects.Client.SendAsync(defaultsRequest);
            Assert.Equal(HttpStatusCode.OK, defaultsResponse.StatusCode);
        }

        // Keep this browser's session cookie and permanent consent while the top-level binding changes.
        using var browser = _brokerFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        _fakeIdp.Subject = "chat-orchestrator";
        _fakeIdp.TenantIds = [TenantId];
        _fakeIdp.Roles = [];
        const string scopes = "openid profile email api.read offline_access projects.orchestrator";
        var (unboundVerifier, unboundChallenge) = Pkce.Create();
        var unboundCode = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            browser, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, scopes, unboundChallenge);
        var unboundTokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            browser, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, unboundCode, unboundVerifier);
        var unboundToken = unboundTokens.GetProperty("access_token").GetString()!;
        var unboundClaims = new JwtSecurityTokenHandler().ReadJwtToken(unboundToken).Claims;
        Assert.DoesNotContain(unboundClaims, claim => claim.Type is "project_id" or "run_id");
        var subject = SingleClaim(unboundClaims, "sub");
        var orchestratorMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, orchestratorMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Orchestrator);

        var ownerSchema = "chat_owner_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "chat_events_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        using var environment = new TemporaryEnvironment(new Dictionary<string, string?>
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
            ["Identity__Audience"] = "https://api.test",
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsHost::Agentweaver.EventsAndSessions.EventsAndSessionsMigrator
                .MigrateAsync(migrations, eventsSchema);
        }
        EventsIntegrationFactory? eventsReference = null;
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => eventsReference!.Server.CreateHandler());
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => orchestratorFactory.Server.CreateHandler());
        eventsReference = eventsFactory;
        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test"),
        });
        await using var gateway = GatewayProductionResourceServer.Start(
            signingKey, (request, token) => ForwardOwnerRequestAsync(request, projects.Client, token),
            orchestratorOwner: (request, token) => ForwardOwnerRequestAsync(request, orchestrator, token),
            identityAudience: "https://api.test");

        using var create = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{project.ProjectId}/runs")
        {
            Content = JsonContent.Create(new CreateProjectRunRequest("Start this fresh chat.", project.ConfigurationRevision)),
        };
        AddBearerAndTenant(create, unboundToken, TenantId);
        create.Headers.Add("Idempotency-Key", "silent-chat-create");
        using var created = await gateway.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pending = await created.Content.ReadFromJsonAsync<CreateProjectRunResponse>(OrchestratorJsonOptions);
        Assert.NotNull(pending);
        Assert.Equal("awaiting_binding", pending.State);
        Assert.Equal(project.ProjectId, pending.Binding.ProjectId);
        Assert.Equal(pending.RunId, pending.Binding.RunId);

        // A single authorize request returns directly to the callback, without login or consent UI.
        var promptsBeforeBinding = _fakeIdp.AuthorizationPrompts.Count;
        var (boundVerifier, boundChallenge) = Pkce.Create();
        using var authorize = await BrokerFlowDriver.StartAuthorizeAsync(
            browser, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, scopes, boundChallenge, "S256",
            state: pending.RunId, projectId: project.ProjectId, runId: pending.RunId, prompt: "none");
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var callback = Assert.IsType<Uri>(authorize.Headers.Location);
        Assert.Equal(IdentityBrokerWebApplicationFactory.TestClientRedirectUri, callback.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.False(query.ContainsKey("error"));
        Assert.False(string.IsNullOrEmpty(query["code"]));
        Assert.Equal(pending.RunId, query["state"].ToString());
        Assert.Equal(promptsBeforeBinding, _fakeIdp.AuthorizationPrompts.Count);
        var boundTokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            browser, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, query["code"].ToString(), boundVerifier);
        var boundToken = boundTokens.GetProperty("access_token").GetString()!;
        var boundJwt = new JwtSecurityTokenHandler().ReadJwtToken(boundToken);
        var claims = boundJwt.Claims;
        var boundIssuer = boundJwt.Issuer;
        Assert.Equal(project.ProjectId, SingleClaim(claims, "project_id"));
        Assert.Equal(pending.RunId, SingleClaim(claims, "run_id"));
        Assert.Equal(BrokerRunBindingAuthority.ProjectsAuthorityKind,
            SingleClaim(claims, BrokerRunBindingAuthority.AuthorityKindClaim));
        Assert.False(string.IsNullOrEmpty(SingleClaim(claims, "aw:run_binding_selection_hash")));

        using var started = await SendAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start", boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var result = await started.Content.ReadFromJsonAsync<StartProjectRunResponse>(OrchestratorJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("started", result.State);
        Assert.Equal(pending.RootSessionId, result.RootSessionId);

        var runMessagePath = $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/messages";
        using (var missingKey = await SendRunMessageAsync(boundToken, runMessagePath, null, "Status?"))
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using (var unboundDenied = await SendRunMessageAsync(unboundToken, runMessagePath, "user-message-1", "Status?"))
            Assert.Equal(HttpStatusCode.Forbidden, unboundDenied.StatusCode);
        using (var wrongProject = await SendRunMessageAsync(
            boundToken, $"/api/v1/projects/{project.ProjectId}-other/runs/{pending.RunId}/messages",
            "wrong-project", "Status?"))
            Assert.Equal(HttpStatusCode.Forbidden, wrongProject.StatusCode);
        using (var wrongRun = await SendRunMessageAsync(
            boundToken, $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}-other/messages",
            "wrong-run", "Status?"))
            Assert.Equal(HttpStatusCode.Forbidden, wrongRun.StatusCode);
        using (var spoofedSender = new HttpRequestMessage(HttpMethod.Post, runMessagePath)
        {
            Content = new StringContent(
                """{"text":"Status?","senderIssuer":"https://spoofed.test","senderSubject":"someone-else"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        })
        {
            AddBearerAndTenant(spoofedSender, boundToken, TenantId);
            spoofedSender.Headers.Add("Idempotency-Key", "spoofed-sender");
            using var response = await gateway.Client.SendAsync(spoofedSender);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var genericSelfMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/messages")
        {
            Content = JsonContent.Create(new
            {
                recipientSessionId = pending.RootSessionId,
                idempotencyKey = "generic-self",
                deliveryMode = "immediate",
                purpose = "steering",
                kind = "text",
                payload = new { text = "Status?" },
            }),
        })
        {
            AddBearerAndTenant(genericSelfMessage, boundToken, TenantId);
            using var response = await gateway.Client.SendAsync(genericSelfMessage);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using (var activeSessionCountCommand = new NpgsqlCommand($"""
            SELECT count(*) FROM "{ownerSchema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run
              AND lifecycle_state = 'active'
            """, connection))
        {
            activeSessionCountCommand.Parameters.AddWithValue("project", project.ProjectId);
            activeSessionCountCommand.Parameters.AddWithValue("run", pending.RunId);
            var activeSessionCount = (long)(await activeSessionCountCommand.ExecuteScalarAsync())!;
            Assert.Equal(1L, activeSessionCount);
        }

        using var userMessage = await SendRunMessageAsync(boundToken, runMessagePath, "user-message-1", "Status?");
        Assert.Equal(HttpStatusCode.OK, userMessage.StatusCode);
        var userReceipt = await userMessage.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(OrchestratorJsonOptions);
        Assert.NotNull(userReceipt);
        Assert.Equal(project.ProjectId, userReceipt.ProjectId);
        Assert.Equal(pending.RunId, userReceipt.RunId);
        Assert.Equal(pending.RootSessionId, userReceipt.RootSessionId);
        Assert.Equal(pending.RootSessionId, userReceipt.RecipientSessionId);
        Assert.Equal("accepted", userReceipt.Status);
        Assert.Equal(boundIssuer, userReceipt.Sender.Issuer);
        Assert.Equal(subject, userReceipt.Sender.Subject);
        Assert.NotEqual(Guid.Empty, userReceipt.MessageId);
        Assert.True(userReceipt.ThreadSequence > 0);

        using var userMessageReplay = await SendRunMessageAsync(
            boundToken, runMessagePath, "user-message-1", "Status?");
        Assert.Equal(HttpStatusCode.OK, userMessageReplay.StatusCode);
        var replayedUserReceipt =
            await userMessageReplay.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(OrchestratorJsonOptions);
        Assert.Equal(userReceipt, replayedUserReceipt);

        using (var idempotencyConflict = await SendRunMessageAsync(
            boundToken, runMessagePath, "user-message-1", "Different text"))
            Assert.Equal(HttpStatusCode.Conflict, idempotencyConflict.StatusCode);

        var messageTargetSessionId = $"message-target-{Guid.NewGuid():N}";
        using (var registerTarget = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/children")
        {
            Content = JsonContent.Create(new { sessionId = messageTargetSessionId }),
        })
        {
            AddBearerAndTenant(registerTarget, boundToken, TenantId);
            using var response = await gateway.Client.SendAsync(registerTarget);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        using var explicitTargetMessage = await SendRunMessageAsync(
            boundToken, runMessagePath, "user-message-2", "Worker status?", messageTargetSessionId);
        Assert.Equal(HttpStatusCode.OK, explicitTargetMessage.StatusCode);
        var explicitTargetReceipt =
            await explicitTargetMessage.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(OrchestratorJsonOptions);
        Assert.NotNull(explicitTargetReceipt);
        Assert.Equal(pending.RootSessionId, explicitTargetReceipt.RootSessionId);
        Assert.Equal(messageTargetSessionId, explicitTargetReceipt.RecipientSessionId);
        Assert.Equal(userReceipt.Sender, explicitTargetReceipt.Sender);

        using (var targetStatusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/status"))
        {
            AddBearerAndTenant(targetStatusRequest, boundToken, TenantId);
            using var targetStatusResponse = await orchestrator.SendAsync(targetStatusRequest);
            Assert.Equal(HttpStatusCode.OK, targetStatusResponse.StatusCode);
            var targetStatus =
                await targetStatusResponse.Content.ReadFromJsonAsync<SessionStatusSnapshot>(OrchestratorJsonOptions);
            Assert.NotNull(targetStatus);

            using var turnBoundaryRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/turn-boundary")
            {
                Content = JsonContent.Create(
                    new TurnBoundaryRequest(targetStatus.ExecutionFence, targetStatus.StateVersion),
                    options: OrchestratorJsonOptions),
            };
            AddBearerAndTenant(turnBoundaryRequest, boundToken, TenantId);
            using var turnBoundaryResponse = await orchestrator.SendAsync(turnBoundaryRequest);
            Assert.Equal(HttpStatusCode.OK, turnBoundaryResponse.StatusCode);
            var turnBoundary =
                await turnBoundaryResponse.Content.ReadFromJsonAsync<TurnBoundaryResult>(OrchestratorJsonOptions);
            Assert.NotNull(turnBoundary);
            var presentedMessage = Assert.IsType<AddressedMessageEnvelope>(turnBoundary.PresentedMessage);
            Assert.Equal(explicitTargetReceipt.MessageId, presentedMessage.MessageId);
            Assert.Equal(AddressedMessageStatus.Delivered, presentedMessage.Status);

            using var replayAfterPresentation = await SendRunMessageAsync(
                boundToken, runMessagePath, "user-message-2", "Worker status?", messageTargetSessionId);
            Assert.Equal(HttpStatusCode.OK, replayAfterPresentation.StatusCode);
            var presentedReplayReceipt =
                await replayAfterPresentation.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(
                    OrchestratorJsonOptions);
            Assert.Equal(explicitTargetReceipt, presentedReplayReceipt);

            using var acknowledgeRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/messages/{presentedMessage.MessageId:D}/acknowledge")
            {
                Content = JsonContent.Create(
                    new AddressedMessageClaimFenceRequest(presentedMessage.ClaimFence),
                    options: OrchestratorJsonOptions),
            };
            AddBearerAndTenant(acknowledgeRequest, boundToken, TenantId);
            using var acknowledgeResponse = await orchestrator.SendAsync(acknowledgeRequest);
            Assert.Equal(HttpStatusCode.OK, acknowledgeResponse.StatusCode);
            var acknowledgedMessage =
                await acknowledgeResponse.Content.ReadFromJsonAsync<AddressedMessageEnvelope>(OrchestratorJsonOptions);
            Assert.NotNull(acknowledgedMessage);
            Assert.Equal(AddressedMessageStatus.Acknowledged, acknowledgedMessage.Status);

            using var replayAfterAcknowledgment = await SendRunMessageAsync(
                boundToken, runMessagePath, "user-message-2", "Worker status?", messageTargetSessionId);
            Assert.Equal(HttpStatusCode.OK, replayAfterAcknowledgment.StatusCode);
            var acknowledgmentReplayReceipt =
                await replayAfterAcknowledgment.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(
                    OrchestratorJsonOptions);
            Assert.Equal(explicitTargetReceipt, acknowledgmentReplayReceipt);
        }

        // Replay start and check the real Events journal, not a mocked owner receipt.
        using var replay = await SendAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start", boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var journal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
            """, connection);
        journal.Parameters.AddWithValue("project", project.ProjectId);
        journal.Parameters.AddWithValue("run", pending.RunId);
        journal.Parameters.AddWithValue("key", $"{pending.RunId}:first");
        Assert.Equal(1L, await journal.ExecuteScalarAsync());

        await using (var userMessageJournal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
              AND sender_session_id = @root AND recipient_session_id = @root
              AND sender_issuer = @issuer AND sender_identity_hash = @subjectHash
              AND payload ->> 'text' = @text
            """, connection))
        {
            userMessageJournal.Parameters.AddWithValue("project", project.ProjectId);
            userMessageJournal.Parameters.AddWithValue("run", pending.RunId);
            userMessageJournal.Parameters.AddWithValue("key", "user-message-1");
            userMessageJournal.Parameters.AddWithValue("root", pending.RootSessionId);
            userMessageJournal.Parameters.AddWithValue("issuer", boundIssuer);
            userMessageJournal.Parameters.AddWithValue("subjectHash",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{boundIssuer}\0{subject}"))));
            userMessageJournal.Parameters.AddWithValue("text", "Status?");
            Assert.Equal(1L, await userMessageJournal.ExecuteScalarAsync());
        }
        await using (var acknowledgedTargetJournal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
              AND recipient_session_id = @recipient AND status = 'Acknowledged'
            """, connection))
        {
            acknowledgedTargetJournal.Parameters.AddWithValue("project", project.ProjectId);
            acknowledgedTargetJournal.Parameters.AddWithValue("run", pending.RunId);
            acknowledgedTargetJournal.Parameters.AddWithValue("key", "user-message-2");
            acknowledgedTargetJournal.Parameters.AddWithValue("recipient", messageTargetSessionId);
            Assert.Equal(1L, await acknowledgedTargetJournal.ExecuteScalarAsync());
        }
        await using (var ownerMessage = new NpgsqlCommand($"""
            SELECT count(*) FROM "{ownerSchema}".coordination_messages
            WHERE project_id = @project AND run_id = @run AND message_id = @message
              AND writer_issuer = @issuer AND writer_subject = @subject
            """, connection))
        {
            ownerMessage.Parameters.AddWithValue("project", project.ProjectId);
            ownerMessage.Parameters.AddWithValue("run", pending.RunId);
            ownerMessage.Parameters.AddWithValue("message", userReceipt.OwnerMessageId);
            ownerMessage.Parameters.AddWithValue("issuer", boundIssuer);
            ownerMessage.Parameters.AddWithValue("subject", subject);
            Assert.Equal(1L, await ownerMessage.ExecuteScalarAsync());
        }

        using (var childSteering = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/messages")
        {
            Content = JsonContent.Create(new CoordinationMessageRequest(
                pending.RootSessionId,
                "child-to-root-steering",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.Steering,
                AddressedMessageKind.Text,
                System.Text.Json.JsonSerializer.SerializeToElement(new { text = "Worker status?" })),
                options: OrchestratorJsonOptions),
        })
        {
            AddBearerAndTenant(childSteering, boundToken, TenantId);
            using var response = await orchestrator.SendAsync(childSteering);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Project Contributors can cancel another actor's run without obtaining that actor's run-bound token.
        var contributorToken = await IssueTokenAsync(
            "projects.orchestrator", [TenantId], "chat-contributor", null, null, []);
        var contributorSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(contributorToken).Claims, "sub");
        var contributorMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, contributorSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, contributorMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Contributor);
        var viewerToken = await IssueTokenAsync(
            "projects.orchestrator", [TenantId], "chat-viewer", null, null, []);
        var viewerSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(viewerToken).Claims, "sub");
        var viewerMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, viewerSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, viewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Viewer);
        using (var deniedCreate = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{project.ProjectId}/runs")
        {
            Content = JsonContent.Create(new CreateProjectRunRequest("Not an Orchestrator.", project.ConfigurationRevision)),
        })
        {
            AddBearerAndTenant(deniedCreate, contributorToken, TenantId);
            deniedCreate.Headers.Add("Idempotency-Key", "contributor-create");
            using var response = await gateway.Client.SendAsync(deniedCreate);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("project_orchestrator_role_required_acceptRunSelection",
                await response.Content.ReadAsStringAsync());
        }
        using (var denied = await CancelChatAsync(viewerToken, "viewer-cancel"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var boundDenied = await CancelChatAsync(boundToken, "bound-cancel"))
            Assert.Equal(HttpStatusCode.Forbidden, boundDenied.StatusCode);
        using (var cancelled = await CancelChatAsync(contributorToken, "contributor-cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var receipt = await cancelled.Content.ReadFromJsonAsync<CancelProjectRunResponse>(OrchestratorJsonOptions);
            Assert.NotNull(receipt);
            Assert.False(receipt.AlreadyTerminal);
        }
        using (var terminal = await CancelChatAsync(contributorToken, "terminal-cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, terminal.StatusCode);
            var receipt = await terminal.Content.ReadFromJsonAsync<CancelProjectRunResponse>(OrchestratorJsonOptions);
            Assert.NotNull(receipt);
            Assert.True(receipt.AlreadyTerminal);
        }

        // Exercise the production Gateway and both fresh Projects checks for each cancellation.
        async Task<HttpResponseMessage> CancelChatAsync(string token, string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/cancel")
            {
                Content = JsonContent.Create(new CancelProjectRunRequest()),
            };
            AddBearerAndTenant(request, token, TenantId);
            request.Headers.Add("Idempotency-Key", key);
            return await gateway.Client.SendAsync(request);
        }

        async Task<HttpResponseMessage> SendRunMessageAsync(
            string token,
            string path,
            string? key,
            string text,
            string? targetSessionId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new ProjectRunMessageRequest(text, targetSessionId)),
            };
            AddBearerAndTenant(request, token, TenantId);
            if (key is not null)
                request.Headers.Add("Idempotency-Key", key);
            return await gateway.Client.SendAsync(request);
        }
    }
}
