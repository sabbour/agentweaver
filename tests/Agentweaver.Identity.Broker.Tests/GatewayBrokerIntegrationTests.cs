extern alias GatewayHost;
extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using GatewayRouteCatalog = GatewayHost::Agentweaver.Gateway.GatewayRouteCatalog;
using EventsOwner = EventsHost::Agentweaver.EventsAndSessions;
using OrchestratorHost::Agentweaver.Orchestrator;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private static readonly JsonSerializerOptions OrchestratorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));

        var tenantAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "gateway-tenant-admin", null, null, ["platform_admin"]);
        var tenantAdminSubject = SingleClaim(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(tenantAdminToken).Claims,
            "sub");
        var tenantAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, tenantAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            tenantAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, tenantAdminToken, TenantId, "Gateway event authorization");

        const string runId = "gateway-events-run";
        const string runSubject = "gateway-events-viewer";
        var bootstrapToken = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], runSubject, null, null, ["orchestrator"]);
        var subject = SingleClaim(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(bootstrapToken).Claims,
            "sub");
        var membership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, subject, TenantId);
        var viewerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            membership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Viewer);
        await using var events = await GatewayEventsJournalFixture.CreateAsync(
            projects.PrivilegedFixtureDataSource, project.ProjectId, runId, subject);
        await CreateRunBindingGrantAsync(subject, project.ProjectId, runId);
        var runToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            runSubject,
            project.ProjectId,
            runId,
            ["orchestrator"]);
        var contextToken = tenantAdminToken;

        var ownerRequests = new ConcurrentQueue<(GatewayOwner Owner, Uri Uri, string? Authorization, string? Tenant)>();
        var projectOwnerRequests = new ConcurrentQueue<(Uri Uri, string? Authorization, string? Tenant)>();
        var revocationPageArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revocationPageRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRevocationPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> HandleOwnerRequestAsync(
            GatewayOwner owner,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Gateway sent an owner request without a URI.");
            request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
            ownerRequests.Enqueue((
                owner,
                uri,
                request.Headers.Authorization?.ToString(),
                tenantValues?.SingleOrDefault()));

            if (owner == GatewayOwner.Orchestrator)
            {
                if (uri.AbsolutePath.EndsWith(
                    "/coordination/sessions/gateway-session/status", StringComparison.Ordinal))
                {
                    var status = new SessionStatusSnapshot(
                        new SessionIdentity(project.ProjectId, runId, "gateway-session"),
                        null,
                        "gateway-session",
                        CoordinationSessionKind.Coordinator,
                        false,
                        CoordinationActivityState.Idle,
                        null,
                        CoordinationLifecycleState.Active,
                        3,
                        7,
                        [new SessionStatusBlocker(
                            CoordinationBlockerKind.AwaitingInput,
                            "gateway-question",
                            ["continue"],
                            true,
                            "Continue?")],
                        "available",
                        "none",
                        new SessionInterruptionIntentSnapshot(
                            CoordinationInterruptionIntentState.None,
                            null,
                            null),
                        new OwnerRunExecutionSnapshot("running", 1, null, null));
                    return JsonResponse(
                        HttpStatusCode.OK,
                        JsonSerializer.Serialize(status, OrchestratorJsonOptions));
                }
                if (uri.AbsolutePath.EndsWith("/coordination/sessions/gateway-session/spawn", StringComparison.Ordinal))
                    return JsonResponse(
                        HttpStatusCode.Accepted,
                        """{"operation":"accepted","runId":"gateway-events-run"}""");
                if (uri.AbsolutePath.EndsWith("/decisions/gates/gateway-request/answer", StringComparison.Ordinal))
                    return JsonResponse(
                        HttpStatusCode.Conflict,
                        """{"type":"about:blank","title":"stale_gate","status":409,"code":"stale_gate"}""");
                if (uri.AbsolutePath.EndsWith("/coordination/status", StringComparison.Ordinal))
                    throw new HttpRequestException("Controlled owner outage.");
            }

            if (owner != GatewayOwner.Events)
                return JsonResponse(HttpStatusCode.NotImplemented, """{"code":"unexpected_owner_request"}""");

            var query = QueryHelpers.ParseQuery(uri.Query);
            var cursor = query.TryGetValue("cursor", out var cursorValues)
                ? cursorValues.SingleOrDefault()
                : null;
            if (cursor is "gateway-cursor-io-before-start" or "gateway-cursor-io-after-start-next")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ThrowingOwnerBodyStream()),
                };
            if (cursor == "gateway-cursor-io-after-start")
                return JsonResponse(
                    HttpStatusCode.OK,
                    """{"events":[{"eventId":"00000000-0000-0000-0000-000000000002"}],"nextCursor":"gateway-cursor-io-after-start-next"}""");
            if (cursor == "gateway-cursor-invalid-owner")
                return JsonResponse(
                    HttpStatusCode.OK,
                    """{"events":[{"eventId":"00000000-0000-0000-0000-000000000001"}],"nextCursor":"gateway-cursor-invalid-owner"}""");
            if (cursor == events.FirstCursor && revocationPageArmed.Task.IsCompleted)
            {
                revocationPageRequested.TrySetResult();
                await releaseRevocationPage.Task.WaitAsync(cancellationToken);
            }

            var limit = query.TryGetValue("limit", out var limitValues) &&
                int.TryParse(limitValues.SingleOrDefault(), out var requestedLimit)
                ? requestedLimit
                : 100;
            var page = await events.ReplayRunAsync(project.ProjectId, runId, cursor, limit, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, GatewayEventsJournalFixture.SerializePage(page));
        }

        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(certificate),
            projects.CreateHandler,
            HandleOwnerRequestAsync,
            request =>
            {
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
                projectOwnerRequests.Enqueue((
                    request.RequestUri
                        ?? throw new InvalidOperationException("Gateway sent a Projects request without a URI."),
                    request.Headers.Authorization?.ToString(),
                    tenantValues?.SingleOrDefault()));
            });

        using var openApi = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using (var openApiDocument = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync()))
        {
            var paths = openApiDocument.RootElement.GetProperty("paths");
            const string recoveryPath = "/api/v1/projects/{projectId}/runs/{runId}/coordination/recovery";
            Assert.True(
                paths.TryGetProperty(recoveryPath, out var recoveryPathDocument),
                $"OpenAPI omitted {recoveryPath}. Paths: {string.Join(", ", paths.EnumerateObject().Select(path => path.Name))}");
            var runRecovery = recoveryPathDocument.GetProperty("post");
            Assert.Equal(
                "Orchestrator",
                runRecovery.GetProperty("x-agentweaver-owner").GetString());
            Assert.False(runRecovery.GetProperty("x-agentweaver-accepted-only").GetBoolean());
            Assert.False(runRecovery.GetProperty("responses").TryGetProperty("202", out _));
            foreach (var acceptedOnlyPath in new[]
            {
                "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{parentSessionId}/spawn",
                "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/idle-subscriptions",
                "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/messages",
            })
            {
                var acceptedOnly = paths.GetProperty(acceptedOnlyPath).GetProperty("post");
                Assert.True(acceptedOnly.GetProperty("x-agentweaver-accepted-only").GetBoolean());
                Assert.True(acceptedOnly.GetProperty("responses").TryGetProperty("202", out _));
            }
            foreach (var completedOperationPath in new[]
            {
                "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/fork",
                "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/request_assembly",
            })
            {
                var completedOperation = paths.GetProperty(completedOperationPath).GetProperty("post");
                Assert.False(completedOperation.GetProperty("x-agentweaver-accepted-only").GetBoolean());
                Assert.False(completedOperation.GetProperty("responses").TryGetProperty("202", out _));
            }
            var liveEvents = paths.GetProperty(
                $"/api/v1/projects/{{projectId}}/runs/{{runId}}/events/live").GetProperty("get");
            Assert.Equal("server-sent-events", liveEvents.GetProperty("x-agentweaver-stream").GetString());
            Assert.Equal(
                "Bearer",
                liveEvents.GetProperty("security")[0].EnumerateObject().Single().Name);
            var liveEventResponses = liveEvents.GetProperty("responses");
            Assert.True(
                liveEventResponses.GetProperty("200").GetProperty("content")
                    .TryGetProperty("text/event-stream", out _));
            Assert.True(liveEventResponses.TryGetProperty("400", out _));
            var authorizationContext = paths.GetProperty("/api/v1/authorization/context").GetProperty("get");
            Assert.Equal(
                "getAuthorizationContext",
                authorizationContext.GetProperty("operationId").GetString());
            Assert.Equal(
                "Projects",
                authorizationContext.GetProperty("x-agentweaver-owner").GetString());
            Assert.False(authorizationContext.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant")
                .GetProperty("required").GetBoolean());
            Assert.False(paths.TryGetProperty("/api/connections/copilot-user/v1/begin", out _));
            Assert.False(paths.TryGetProperty("/api/auth/github/repo-app/authorizations", out _));
            Assert.False(paths.TryGetProperty("/api/github/repository-selections", out _));
            Assert.False(paths.TryGetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/github-app-installations/authorizations",
                out _));
            Assert.DoesNotContain(
                paths.EnumerateObject(),
                path => path.Name.Contains("webhook-relay", StringComparison.Ordinal));
            var sourceControlIssue = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/issues")
                .GetProperty("post");
            Assert.Equal("createSourceControlIssue", sourceControlIssue.GetProperty("operationId").GetString());
            Assert.Equal("Orchestrator", sourceControlIssue.GetProperty("x-agentweaver-owner").GetString());
            Assert.Equal(
                "#/components/schemas/SourceControlIssueRequest",
                sourceControlIssue.GetProperty("requestBody").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            var sourceControlPin = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/pin")
                .GetProperty("post");
            Assert.False(sourceControlPin.GetProperty("requestBody").GetProperty("required").GetBoolean());
            Assert.Equal(
                "#/components/schemas/SourceControlRepositoryPinRequest",
                sourceControlPin.GetProperty("requestBody").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.True(sourceControlPin.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant")
                .GetProperty("required").GetBoolean());
            var sourceControlReviews = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}" +
                "/pull-requests/{pullRequestNumber}/reviews").GetProperty("get");
            var pullRequestNumber = sourceControlReviews.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "pullRequestNumber");
            Assert.Equal("integer", pullRequestNumber.GetProperty("schema").GetProperty("type").GetString());
            Assert.Equal("int64", pullRequestNumber.GetProperty("schema").GetProperty("format").GetString());
            var mergeIntent = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/merge-intents")
                .GetProperty("post");
            Assert.True(mergeIntent.GetProperty("x-agentweaver-accepted-only").GetBoolean());
            Assert.True(mergeIntent.GetProperty("responses").TryGetProperty("202", out _));
            var readRecord = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}")
                .GetProperty("get");
            Assert.DoesNotContain(
                paths.EnumerateObject(),
                path => path.Name.Contains(":guid}", StringComparison.Ordinal));
            var recordIdParameter = readRecord.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "recordId");
            Assert.Equal(
                "uuid",
                recordIdParameter.GetProperty("schema").GetProperty("format").GetString());

            var schemas = openApiDocument.RootElement.GetProperty("components").GetProperty("schemas");
            Assert.False(schemas.TryGetProperty("OwnerJson", out _));
            var modelSelectionSettings = schemas.GetProperty("ModelSelectionSettings");
            Assert.DoesNotContain(
                "credentialReference",
                modelSelectionSettings.GetProperty("required").EnumerateArray()
                    .Select(property => property.GetString()));
            var credentialReference = modelSelectionSettings.GetProperty("properties")
                .GetProperty("credentialReference");
            var credentialReferenceVariants = credentialReference.GetProperty("anyOf").EnumerateArray().ToArray();
            Assert.Contains(
                credentialReferenceVariants,
                variant => variant.TryGetProperty("type", out var type) && type.GetString() == "null");
            Assert.Contains(
                credentialReferenceVariants,
                variant => variant.TryGetProperty("$ref", out var reference) &&
                    reference.GetString() == "#/components/schemas/SecretRef");
            var secretRef = schemas.GetProperty("SecretRef");
            Assert.Contains("id", secretRef.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()));
            Assert.Contains("version", secretRef.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()));
            AssertOpenApiReferencesResolve(openApiDocument.RootElement, schemas);
            var eventPage = await events.ReplayRunAsync(
                project.ProjectId, runId, null, 10, CancellationToken.None);
            using (var eventPayload = JsonDocument.Parse(GatewayEventsJournalFixture.SerializePage(eventPage)))
                AssertPayloadMatchesSchema(
                    eventPayload.RootElement,
                    schemas.GetProperty("SessionEventPage"),
                    schemas);

            var operations = paths.EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject())
                .ToDictionary(
                    operation => operation.Value.GetProperty("operationId").GetString()!,
                    operation => operation.Value,
                    StringComparer.Ordinal);
            Assert.Equal(GatewayRouteCatalog.Routes.Length, operations.Count);
            foreach (var route in GatewayRouteCatalog.Routes)
            {
                var operation = operations[route.OperationId];
                var requestBody = operation.GetProperty("requestBody");
                Assert.Equal(route.HasJsonBody, requestBody.ValueKind != JsonValueKind.Null);
                if (route.HasJsonBody)
                    Assert.True(requestBody.GetProperty("content").TryGetProperty("application/json", out _));

                var responses = operation.GetProperty("responses");
                Assert.Contains(responses.EnumerateObject(), response =>
                    response.Name.StartsWith("2", StringComparison.Ordinal));
                if (route.AcceptsOnly)
                    Assert.True(responses.TryGetProperty("202", out _));
                foreach (var response in responses.EnumerateObject().Where(response =>
                    response.Name.StartsWith("2", StringComparison.Ordinal) && response.Name != "204"))
                {
                    var content = response.Value.GetProperty("content");
                    if (route.IsRunEventStream)
                        Assert.Equal(
                            "string",
                            content.GetProperty("text/event-stream").GetProperty("schema")
                                .GetProperty("type").GetString());
                    else
                        Assert.True(content.TryGetProperty("application/json", out _));
                }
            }

            using (var projectReadRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/projects/{project.ProjectId}?runId={Uri.EscapeDataString(runId)}"))
            {
                AddBearerAndTenant(projectReadRequest, runToken, TenantId);
                using var projectRead = await gateway.Client.SendAsync(projectReadRequest);
                Assert.Equal(HttpStatusCode.OK, projectRead.StatusCode);
                using var projectPayload = JsonDocument.Parse(await projectRead.Content.ReadAsStringAsync());
                AssertPayloadMatchesSchema(
                    projectPayload.RootElement,
                    schemas.GetProperty("ProjectSummary"),
                    schemas);
            }

            using (var authorizationContextRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/authorization/context"))
            {
                AddBearerAndTenant(authorizationContextRequest, contextToken, TenantId);
                using var authorizationContextResponse = await gateway.Client.SendAsync(
                    authorizationContextRequest);
                Assert.Equal(HttpStatusCode.OK, authorizationContextResponse.StatusCode);
                AssertNoStore(authorizationContextResponse);
                using var authorizationContextPayload = JsonDocument.Parse(
                    await authorizationContextResponse.Content.ReadAsStringAsync());
                AssertPayloadMatchesSchema(
                    authorizationContextPayload.RootElement,
                    schemas.GetProperty("ProjectAuthorizationContext"),
                    schemas);
                Assert.Equal(
                    tenantAdminSubject,
                    authorizationContextPayload.RootElement.GetProperty("actorId").GetString());
                Assert.Equal(
                    TenantId,
                    authorizationContextPayload.RootElement.GetProperty("tenantId").GetString());
                Assert.Equal(
                    JsonValueKind.Null,
                    authorizationContextPayload.RootElement.GetProperty("boundProjectId").ValueKind);
                Assert.Equal(
                    JsonValueKind.Null,
                    authorizationContextPayload.RootElement.GetProperty("boundRunId").ValueKind);
            }
            var forwardedAuthorizationContext = Assert.Single(
                projectOwnerRequests,
                request => request.Uri.AbsolutePath == "/api/authorization/context");
            Assert.Equal("Bearer " + contextToken, forwardedAuthorizationContext.Authorization);
            Assert.Equal(TenantId, forwardedAuthorizationContext.Tenant);

            var projectRequestCountBeforeInvalidSelector = projectOwnerRequests.Count;
            using (var invalidContextRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/authorization/context"))
            {
                invalidContextRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", contextToken);
                invalidContextRequest.Headers.TryAddWithoutValidation(
                    "X-Agentweaver-Tenant",
                    [TenantId, TenantId]);
                using var invalidContextResponse = await gateway.Client.SendAsync(invalidContextRequest);
                Assert.Equal(HttpStatusCode.BadRequest, invalidContextResponse.StatusCode);
                using var invalidContextProblem = JsonDocument.Parse(
                    await invalidContextResponse.Content.ReadAsStringAsync());
                Assert.Equal(
                    "tenant_selector_invalid",
                    invalidContextProblem.RootElement.GetProperty("code").GetString());
            }
            Assert.Equal(projectRequestCountBeforeInvalidSelector, projectOwnerRequests.Count);

            using (var configurationRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/projects/{project.ProjectId}/configuration"))
            {
                AddBearerAndTenant(configurationRequest, tenantAdminToken, TenantId);
                using var configuration = await gateway.Client.SendAsync(configurationRequest);
                Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
                using var configurationPayload = JsonDocument.Parse(await configuration.Content.ReadAsStringAsync());
                AssertPayloadMatchesSchema(
                    configurationPayload.RootElement,
                    schemas.GetProperty("VersionedProjectConfiguration"),
                    schemas);
            }

            using (var sessionStatusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/projects/{project.ProjectId}/runs/{runId}/coordination/sessions/gateway-session/status"))
            {
                AddBearerAndTenant(sessionStatusRequest, runToken, TenantId);
                using var sessionStatus = await gateway.Client.SendAsync(sessionStatusRequest);
                Assert.Equal(HttpStatusCode.OK, sessionStatus.StatusCode);
                using var statusPayload = JsonDocument.Parse(await sessionStatus.Content.ReadAsStringAsync());
                AssertPayloadMatchesSchema(
                    statusPayload.RootElement,
                    schemas.GetProperty("SessionStatusSnapshot"),
                    schemas);
                Assert.Equal("idle", statusPayload.RootElement.GetProperty("activity").GetString());
                Assert.Equal(
                    "awaitingInput",
                    statusPayload.RootElement.GetProperty("blockers")[0].GetProperty("kind").GetString());
            }
        }

        var ownerRequestCountBeforeLiveSse = ownerRequests.Count;
        var projectRequestCountBeforeLiveSse = projectOwnerRequests.Count;
        using (var ioFailureRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/events/live?cursor=gateway-cursor-io-before-start"))
        {
            AddBearerAndTenant(ioFailureRequest, runToken, TenantId);
            using var ioFailure = await gateway.Client.SendAsync(ioFailureRequest);
            Assert.Equal(HttpStatusCode.BadGateway, ioFailure.StatusCode);
            Assert.Equal("application/problem+json", ioFailure.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await ioFailure.Content.ReadAsStringAsync());
            Assert.Equal("owner_unavailable", problem.RootElement.GetProperty("code").GetString());
        }

        using (var ioAfterStartRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/events/live?cursor=gateway-cursor-io-after-start"))
        using (var ioAfterStartCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            AddBearerAndTenant(ioAfterStartRequest, runToken, TenantId);
            using var ioAfterStart = await gateway.Client.SendAsync(
                ioAfterStartRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ioAfterStartCancellation.Token);
            Assert.Equal(HttpStatusCode.OK, ioAfterStart.StatusCode);
            await using var ioAfterStartStream =
                await ioAfterStart.Content.ReadAsStreamAsync(ioAfterStartCancellation.Token);
            using var ioAfterStartReader = new StreamReader(ioAfterStartStream);
            var firstIoEvent = await ReadSseEventLinesAsync(ioAfterStartReader, ioAfterStartCancellation.Token);
            Assert.Equal("id: gateway-cursor-io-after-start-next", firstIoEvent[0]);

            var trailingBody = new StringBuilder();
            try
            {
                var buffer = new char[256];
                int read;
                while ((read = await ioAfterStartReader.ReadAsync(buffer, ioAfterStartCancellation.Token)) > 0)
                    trailingBody.Append(buffer, 0, read);
            }
            catch (IOException)
            {
            }
            Assert.DoesNotContain("id: ", trailingBody.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("application/problem+json", trailingBody.ToString(), StringComparison.Ordinal);
            Assert.Contains(ownerRequests, request =>
                request.Owner == GatewayOwner.Events &&
                QueryHelpers.ParseQuery(request.Uri.Query)["cursor"].SingleOrDefault() ==
                    "gateway-cursor-io-after-start-next");
        }

        var callsBeforeUnauthenticatedRequests = ownerRequests.Count + projectOwnerRequests.Count;
        using (var missingBearer = await gateway.Client.GetAsync("/api/v1/projects"))
            Assert.Equal(HttpStatusCode.Unauthorized, missingBearer.StatusCode);
        using (var wrongAudienceRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects"))
        {
            wrongAudienceRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                CreateGatewayTestToken(certificate, "https://other-api.test", DateTime.UtcNow.AddMinutes(5)));
            using var wrongAudience = await gateway.Client.SendAsync(wrongAudienceRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
        }
        Assert.Equal(
            callsBeforeUnauthenticatedRequests,
            ownerRequests.Count + projectOwnerRequests.Count);

        using (var invalidEventsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/events/live?cursor=gateway-cursor-invalid-owner"))
        {
            AddBearerAndTenant(invalidEventsRequest, runToken, TenantId);
            using var invalidEvents = await gateway.Client.SendAsync(invalidEventsRequest);
            Assert.Equal(HttpStatusCode.BadGateway, invalidEvents.StatusCode);
            using var invalidEventsProblem = JsonDocument.Parse(await invalidEvents.Content.ReadAsStringAsync());
            Assert.Equal(
                "events_page_cursor_invalid",
                invalidEventsProblem.RootElement.GetProperty("code").GetString());
        }

        using (var acceptedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/coordination/sessions/gateway-session/spawn")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        })
        {
            AddBearerAndTenant(acceptedRequest, runToken, TenantId);
            using var accepted = await gateway.Client.SendAsync(acceptedRequest);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.Equal(
                """{"operation":"accepted","runId":"gateway-events-run"}""",
                await accepted.Content.ReadAsStringAsync());
        }

        using (var staleGateRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/coordination/sessions/gateway-session/decisions/gates/gateway-request/answer")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        })
        {
            AddBearerAndTenant(staleGateRequest, runToken, TenantId);
            using var staleGate = await gateway.Client.SendAsync(staleGateRequest);
            Assert.Equal(HttpStatusCode.Conflict, staleGate.StatusCode);
            Assert.Equal(
                """{"type":"about:blank","title":"stale_gate","status":409,"code":"stale_gate"}""",
                await staleGate.Content.ReadAsStringAsync());
        }

        using (var unavailableRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/coordination/status"))
        {
            AddBearerAndTenant(unavailableRequest, runToken, TenantId);
            using var unavailable = await gateway.Client.SendAsync(unavailableRequest);
            Assert.Equal(HttpStatusCode.BadGateway, unavailable.StatusCode);
            using var problem = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            Assert.Equal("owner_unavailable", problem.RootElement.GetProperty("code").GetString());
        }

        var spawnOwnerCall = Assert.Single(ownerRequests, request =>
            request.Owner == GatewayOwner.Orchestrator &&
            request.Uri.AbsolutePath.EndsWith("/coordination/sessions/gateway-session/spawn", StringComparison.Ordinal));
        Assert.Equal("Bearer " + runToken, spawnOwnerCall.Authorization);
        Assert.Equal(TenantId, spawnOwnerCall.Tenant);

        await using (var timeoutGateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(certificate),
            projects.CreateHandler,
            async (owner, _, cancellationToken) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The owner timeout was not enforced.");
            },
            ownerRequestTimeout: TimeSpan.FromMilliseconds(250)))
        using (var timeoutRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/coordination/status"))
        {
            AddBearerAndTenant(timeoutRequest, runToken, TenantId);
            using var timedOut = await timeoutGateway.Client.SendAsync(timeoutRequest);
            Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
            using var problem = JsonDocument.Parse(await timedOut.Content.ReadAsStringAsync());
            Assert.Equal("owner_timeout", problem.RootElement.GetProperty("code").GetString());
        }

        using (var replay = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/events?cursor={Uri.EscapeDataString(events.FirstCursor)}&limit=5"))
        {
            AddBearerAndTenant(replay, runToken, TenantId);
            using var replayResponse = await gateway.Client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
            var replayedEvents = replayDocument.RootElement.GetProperty("events");
            Assert.Equal(2, replayedEvents.GetArrayLength());
            Assert.Equal(events.SecondEvent.EventId, replayedEvents[0].GetProperty("eventId").GetGuid());
            Assert.Equal(events.ThirdEvent.EventId, replayedEvents[1].GetProperty("eventId").GetGuid());
        }

        var replayCall = Assert.Single(ownerRequests, request =>
            request.Owner == GatewayOwner.Events &&
            request.Uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) &&
            QueryHelpers.ParseQuery(request.Uri.Query).TryGetValue("limit", out var limit) &&
            limit.SingleOrDefault() == "5");
        Assert.Equal(
            events.FirstCursor,
            QueryHelpers.ParseQuery(replayCall.Uri.Query)["cursor"].SingleOrDefault());

        var concurrentReplay = await Task.WhenAll(
            ReplayEventIdsAsync(gateway.Client, project.ProjectId, runId, runToken, null),
            ReplayEventIdsAsync(gateway.Client, project.ProjectId, runId, runToken, events.FirstCursor));
        Assert.Equal(
            new[] { events.FirstEvent.EventId, events.SecondEvent.EventId, events.ThirdEvent.EventId },
            concurrentReplay[0]);
        Assert.Equal(
            new[] { events.SecondEvent.EventId, events.ThirdEvent.EventId },
            concurrentReplay[1]);

        await AssertSseEventAsync(
            gateway.Client, project.ProjectId, runId, runToken, null,
            events.FirstCursor, events.FirstEvent.EventId);
        await AssertSseEventAsync(
            gateway.Client, project.ProjectId, runId, runToken, events.FirstCursor,
            events.SecondCursor, events.SecondEvent.EventId);

        using var revokeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{runId}/events/live");
        AddBearerAndTenant(revokeRequest, runToken, TenantId);
        revocationPageArmed.TrySetResult();
        using var revokeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var revokedStream = await gateway.Client.SendAsync(
            revokeRequest, HttpCompletionOption.ResponseHeadersRead, revokeCancellation.Token);
        Assert.Equal(HttpStatusCode.OK, revokedStream.StatusCode);
        Assert.Equal("text/event-stream", revokedStream.Content.Headers.ContentType?.MediaType);
        await using var revokedEventStream =
            await revokedStream.Content.ReadAsStreamAsync(revokeCancellation.Token);
        using var revokedEventReader = new StreamReader(revokedEventStream);
        var firstStreamEvent = await ReadSseEventLinesAsync(revokedEventReader, revokeCancellation.Token);
        Assert.Equal(
            ["id: " + events.FirstCursor, "event: session-event"],
            firstStreamEvent.Take(2));
        Assert.StartsWith("data: ", firstStreamEvent[2], StringComparison.Ordinal);
        using (var firstEventDocument = JsonDocument.Parse(firstStreamEvent[2]["data: ".Length..]))
            Assert.Equal(events.FirstEvent.EventId, firstEventDocument.RootElement.GetProperty("eventId").GetGuid());

        await revocationPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource, viewerAssignment.AssignmentId, 1);
        releaseRevocationPage.TrySetResult();

        var remainingStream = new StringBuilder();
        try
        {
            var buffer = new char[256];
            int read;
            while ((read = await revokedEventReader.ReadAsync(buffer, revokeCancellation.Token)) > 0)
                remainingStream.Append(buffer, 0, read);
        }
        catch (IOException)
        {
        }
        Assert.DoesNotContain("id: ", remainingStream.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ordinal\":2", remainingStream.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("application/problem+json", remainingStream.ToString(), StringComparison.Ordinal);
        var liveSseProjectReads = projectOwnerRequests.ToArray()
            .Skip(projectRequestCountBeforeLiveSse)
            .Where(request =>
                request.Uri.AbsolutePath == $"/api/projects/{project.ProjectId}" &&
                QueryHelpers.ParseQuery(request.Uri.Query).TryGetValue("runId", out var runIds) &&
                runIds.SingleOrDefault() == runId)
            .ToArray();
        Assert.NotEmpty(liveSseProjectReads);
        Assert.All(liveSseProjectReads, request =>
        {
            Assert.Equal("Bearer " + runToken, request.Authorization);
            Assert.Equal(TenantId, request.Tenant);
        });

        var liveSseEventPages = ownerRequests.ToArray()
            .Skip(ownerRequestCountBeforeLiveSse)
            .Where(request =>
                request.Owner == GatewayOwner.Events &&
                QueryHelpers.ParseQuery(request.Uri.Query).TryGetValue("limit", out var limits) &&
                limits.SingleOrDefault() == "1")
            .ToArray();
        Assert.NotEmpty(liveSseEventPages);
        Assert.All(liveSseEventPages, request =>
        {
            Assert.Equal("Bearer " + runToken, request.Authorization);
            Assert.Equal(TenantId, request.Tenant);
        });
        Assert.Contains(ownerRequests, request =>
            request.Owner == GatewayOwner.Events &&
            QueryHelpers.ParseQuery(request.Uri.Query).TryGetValue("cursor", out var cursorValues) &&
            cursorValues.SingleOrDefault() == events.FirstCursor);
    }

    [Fact]
    public async Task GatewayAuthorizationContextRejectsRedirectsCacheableAndDuplicateOwnerContracts()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        const string validContext =
            """{"contractVersion":1,"issuer":"https://broker.test/","actorId":"gateway-negative-test","tenantId":"tenant-1","membershipRevision":1,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[]}""";
        var ownerResponses = new Queue<Func<HttpResponseMessage>>();
        ownerResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://owner-redirect.test/authorization/context") },
        });
        ownerResponses.Enqueue(() => JsonResponse(HttpStatusCode.OK, validContext));
        ownerResponses.Enqueue(() =>
        {
            var response = JsonResponse(
                HttpStatusCode.OK,
                """{"contractVersion":1,"issuer":"https://broker.test/","actorId":"gateway-negative-test","tenantId":"tenant-1","membershipRevision":1,"boundProjectId":null,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[]}""");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(certificate),
            () => new AuthorizationContextOwnerResponseHandler(_ => ownerResponses.Dequeue()()),
            (_, _, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.NotImplemented,
                """{"code":"unexpected_owner_request"}""")));
        var token = CreateGatewayTestToken(
            certificate, "https://api.test", DateTime.UtcNow.AddMinutes(5));

        foreach (var expectedCode in new[]
        {
            "owner_redirect_rejected",
            "owner_contract_invalid",
            "owner_contract_invalid",
        })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/authorization/context");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task GatewayAuthorizationContextMatchesCallerSubjectBoundsAndExplicitTenantSelector()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var unboundToken = CreateGatewayTestToken(
            certificate, "https://api.test", DateTime.UtcNow.AddMinutes(5));
        var boundToken = CreateGatewayTestToken(
            certificate,
            "https://api.test",
            DateTime.UtcNow.AddMinutes(5),
            new Claim("project_id", "project-1"),
            new Claim("run_id", "run-1"));
        var ownerResponses = new Queue<HttpResponseMessage>(
        [
            AuthorizationContextResponse("gateway-negative-test", "tenant-1", null, null),
            AuthorizationContextResponse("different-subject", "tenant-1", null, null),
            AuthorizationContextResponse("gateway-negative-test", "tenant-1", "project-1", null),
            AuthorizationContextResponse("gateway-negative-test", "tenant-1", "project-1", "run-1"),
            AuthorizationContextResponse("gateway-negative-test", "tenant-1", "project-2", "run-1"),
            AuthorizationContextResponse("gateway-negative-test", "tenant-1", "project-1", "run-2"),
            AuthorizationContextResponse("gateway-negative-test", "tenant-2", "project-1", "run-1"),
        ]);
        var forwardedTenants = new ConcurrentQueue<string?>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(certificate),
            () => new AuthorizationContextOwnerResponseHandler(request =>
            {
                forwardedTenants.Enqueue(
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var values)
                        ? values.Single()
                        : null);
                return ownerResponses.Dequeue();
            }),
            (_, _, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.NotImplemented,
                """{"code":"unexpected_owner_request"}""")));

        async Task AssertContextAsync(
            string token,
            string? tenantSelector,
            HttpStatusCode expectedStatus)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/authorization/context");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (tenantSelector is not null)
                request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenantSelector);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(expectedStatus, response.StatusCode);
            if (expectedStatus == HttpStatusCode.OK)
            {
                AssertNoStore(response);
                return;
            }
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("owner_contract_invalid", problem.RootElement.GetProperty("code").GetString());
        }

        await AssertContextAsync(unboundToken, null, HttpStatusCode.OK);
        await AssertContextAsync(unboundToken, null, HttpStatusCode.BadGateway);
        await AssertContextAsync(unboundToken, null, HttpStatusCode.BadGateway);
        await AssertContextAsync(boundToken, "tenant-1", HttpStatusCode.OK);
        await AssertContextAsync(boundToken, "tenant-1", HttpStatusCode.BadGateway);
        await AssertContextAsync(boundToken, "tenant-1", HttpStatusCode.BadGateway);
        await AssertContextAsync(boundToken, "tenant-1", HttpStatusCode.BadGateway);

        Assert.Equal(
            new string?[] { null, null, null, "tenant-1", "tenant-1", "tenant-1", "tenant-1" },
            forwardedTenants.ToArray());
        Assert.Empty(ownerResponses);

        static HttpResponseMessage AuthorizationContextResponse(
            string actorId,
            string tenantId,
            string? projectId,
            string? runId)
        {
            var body = JsonSerializer.Serialize(new
            {
                contractVersion = 1,
                issuer = "https://broker.test/",
                actorId,
                tenantId,
                membershipRevision = 1,
                boundProjectId = projectId,
                boundRunId = runId,
                effectiveAuthority = Array.Empty<object>(),
            });
            var response = JsonResponse(HttpStatusCode.OK, body);
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        }
    }

    [Fact]
    public async Task GatewayAbortsFiniteProxyWhenOwnerBodyTimesOutAfterResponseStarts()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var ownerReadBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(certificate),
            () => new HttpClientHandler(),
            (owner, _, _) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new BlockingOwnerBodyStream(ownerReadBlocked)),
                });
            },
            ownerRequestTimeout: TimeSpan.FromSeconds(2));

        var token = CreateGatewayTestToken(
            certificate, "https://api.test", DateTime.UtcNow.AddMinutes(5));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/gateway-project/runs/gateway-run/coordination/status");
        AddBearerAndTenant(request, token, TenantId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await gateway.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        var expectedPrefix = Encoding.UTF8.GetBytes("""{"partial":true}""");
        var receivedPrefix = new byte[expectedPrefix.Length];
        var received = 0;
        while (received < receivedPrefix.Length)
        {
            var read = await responseStream.ReadAsync(
                receivedPrefix.AsMemory(received), cancellation.Token);
            if (read == 0)
                break;
            received += read;
        }
        Assert.Equal(expectedPrefix.Length, received);
        Assert.Equal(expectedPrefix, receivedPrefix);
        await ownerReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var trailingBody = new MemoryStream();
        try
        {
            var buffer = new byte[256];
            int read;
            while ((read = await responseStream.ReadAsync(buffer, cancellation.Token)) > 0)
                trailingBody.Write(buffer, 0, read);
        }
        catch (IOException)
        {
        }
        Assert.Empty(trailingBody.ToArray());
    }

    private static async Task<Guid[]> ReplayEventIdsAsync(
        HttpClient client,
        string projectId,
        string runId,
        string token,
        string? cursor)
    {
        var query = cursor is null ? string.Empty : "?cursor=" + Uri.EscapeDataString(cursor);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{projectId}/runs/{runId}/events{query}");
        AddBearerAndTenant(request, token, TenantId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("events").EnumerateArray()
            .Select(item => item.GetProperty("eventId").GetGuid())
            .ToArray();
    }

    private static async Task AssertSseEventAsync(
        HttpClient client,
        string projectId,
        string runId,
        string token,
        string? cursor,
        string expectedCursor,
        Guid expectedEventId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/projects/{projectId}/runs/{runId}/events/live");
        AddBearerAndTenant(request, token, TenantId);
        if (cursor is not null)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);
        var lines = await ReadSseEventLinesAsync(reader, cancellation.Token);

        Assert.Equal(
            ["id: " + expectedCursor, "event: session-event"],
            lines.Take(2));
        Assert.StartsWith("data: ", lines[2], StringComparison.Ordinal);
        using var eventDocument = JsonDocument.Parse(lines[2]["data: ".Length..]);
        Assert.Equal(expectedEventId, eventDocument.RootElement.GetProperty("eventId").GetGuid());
        cancellation.Cancel();
    }

    private static async Task<string[]> ReadSseEventLinesAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            if (line.Length == 0)
                return lines.ToArray();
            lines.Add(line);
        }
    }

    private sealed class BlockingOwnerBodyStream(
        TaskCompletionSource readBlocked) : Stream
    {
        private static readonly byte[] FirstChunk = Encoding.UTF8.GetBytes("""{"partial":true}""");
        private bool _sentFirstChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sentFirstChunk)
            {
                _sentFirstChunk = true;
                FirstChunk.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(FirstChunk.Length);
            }

            return WaitForOwnerTimeoutAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask<int> WaitForOwnerTimeoutAsync(CancellationToken cancellationToken)
        {
            readBlocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static void AssertOpenApiReferencesResolve(JsonElement value, JsonElement schemas)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$ref", out var reference))
            {
                const string prefix = "#/components/schemas/";
                var referenceValue = reference.GetString();
                Assert.StartsWith(prefix, referenceValue, StringComparison.Ordinal);
                Assert.True(schemas.TryGetProperty(referenceValue![prefix.Length..], out _));
            }
            foreach (var property in value.EnumerateObject())
                AssertOpenApiReferencesResolve(property.Value, schemas);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AssertOpenApiReferencesResolve(item, schemas);
        }
    }

    private static void AssertPayloadMatchesSchema(
        JsonElement payload,
        JsonElement schema,
        JsonElement schemas)
    {
        if (payload.ValueKind == JsonValueKind.Null)
            return;
        if (schema.TryGetProperty("oneOf", out var variants))
        {
            var payloadKind = payload.GetProperty("kind").GetString();
            schema = variants.EnumerateArray().First(variant =>
            {
                var kindSchema = variant.GetProperty("properties").GetProperty("kind");
                return kindSchema.GetProperty("const").GetString() == payloadKind;
            });
        }
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            schema = alternatives.EnumerateArray().First(alternative =>
                !alternative.TryGetProperty("type", out var type) ||
                type.GetString() != "null");
        }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/components/schemas/";
            schema = schemas.GetProperty(reference.GetString()![prefix.Length..]);
        }
        if (schema.TryGetProperty("enum", out var enumValues))
            Assert.Contains(payload.GetString(), enumValues.EnumerateArray().Select(value => value.GetString()));
        if (schema.TryGetProperty("const", out var constant))
            Assert.Equal(constant.GetString(), payload.GetString());
        if (schema.TryGetProperty("type", out var type))
        {
            switch (type.GetString())
            {
                case "array":
                    Assert.Equal(JsonValueKind.Array, payload.ValueKind);
                    break;
                case "boolean":
                    Assert.True(payload.ValueKind is JsonValueKind.True or JsonValueKind.False);
                    break;
                case "integer":
                    Assert.Equal(JsonValueKind.Number, payload.ValueKind);
                    Assert.True(payload.TryGetInt64(out _));
                    break;
                case "number":
                    Assert.Equal(JsonValueKind.Number, payload.ValueKind);
                    break;
                case "object":
                    Assert.Equal(JsonValueKind.Object, payload.ValueKind);
                    break;
                case "string":
                    Assert.Equal(JsonValueKind.String, payload.ValueKind);
                    if (schema.TryGetProperty("format", out var format))
                    {
                        if (format.GetString() == "uuid")
                            Assert.True(Guid.TryParse(payload.GetString(), out _));
                        if (format.GetString() == "date-time")
                            Assert.True(DateTimeOffset.TryParse(payload.GetString(), out _));
                    }
                    break;
            }
        }
        if (payload.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var properties))
        {
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                Assert.True(payload.TryGetProperty(required.GetString()!, out _));
            foreach (var property in payload.EnumerateObject())
            {
                Assert.True(
                    properties.TryGetProperty(property.Name, out var propertySchema),
                    $"The OpenAPI schema does not describe serialized property '{property.Name}'.");
                AssertPayloadMatchesSchema(property.Value, propertySchema, schemas);
            }
        }
        if (payload.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in payload.EnumerateArray())
                AssertPayloadMatchesSchema(item, items, schemas);
    }

    private sealed class ThrowingOwnerBodyStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Controlled owner body failure."));
    }

    private sealed class AuthorizationContextOwnerResponseHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private static string CreateGatewayTestToken(
        X509Certificate2 certificate,
        string audience,
        DateTime expiresAt,
        params Claim[] additionalClaims)
    {
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience,
            [new Claim("sub", "gateway-negative-test"), .. additionalClaims],
            now.AddMinutes(-1),
            expiresAt,
            new SigningCredentials(new X509SecurityKey(certificate), SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class GatewayEventsJournalFixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions EventsJsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            },
        };

        private readonly NpgsqlDataSource _dataSource;
        private readonly string _schema;

        private GatewayEventsJournalFixture(NpgsqlDataSource dataSource, string schema)
        {
            _dataSource = dataSource;
            _schema = schema;
        }

        public EventsOwner.PostgresSessionsJournal Journal { get; private set; } = null!;
        public ClaimsPrincipal Principal { get; private set; } = null!;
        public SessionEventEnvelope FirstEvent { get; private set; } = null!;
        public SessionEventEnvelope SecondEvent { get; private set; } = null!;
        public SessionEventEnvelope ThirdEvent { get; private set; } = null!;
        public string FirstCursor { get; private set; } = null!;
        public string SecondCursor { get; private set; } = null!;

        public static async Task<GatewayEventsJournalFixture> CreateAsync(
            NpgsqlDataSource dataSource,
            string projectId,
            string runId,
            string subject)
        {
            var schema = "gateway_events_" + Guid.NewGuid().ToString("N");
            var fixture = new GatewayEventsJournalFixture(dataSource, schema);
            try
            {
                await EventsOwner.EventsAndSessionsMigrator.MigrateAsync(dataSource, schema);
                await fixture.InitializeAsync(projectId, runId, subject);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public static string SerializePage(SessionEventPage page) =>
            JsonSerializer.Serialize(page, EventsJsonOptions);

        public Task<SessionEventPage> ReplayRunAsync(
            string projectId,
            string runId,
            string? cursor,
            int limit,
            CancellationToken cancellationToken) =>
            Journal.ReplayRunAsync(
                Principal,
                new SessionRunEventPageRequest(projectId, runId, cursor, limit),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }

        private async Task InitializeAsync(string projectId, string runId, string subject)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(EventsOwner.SessionIdentityClaims.Subject, subject),
                new Claim(EventsOwner.SessionIdentityClaims.ProjectId, projectId),
                new Claim(EventsOwner.SessionIdentityClaims.RunId, runId),
            ], "gateway-events-integration"));

            var databaseName = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString).Database
                ?? throw new InvalidOperationException("The Events integration database name is required.");
            var options = new EventsOwner.PostgresSessionsProviderOptions(
                "gateway-events",
                databaseName,
                1,
                _schema,
                "gateway-events-v1",
                PollIntervalMilliseconds: 50);
            Journal = new EventsOwner.PostgresSessionsJournal(_dataSource, options);
            var provider = new EventsOwner.NativePostgresSessionsProvider();
            var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
                [provider.CreateRegistration(options)],
                [new ProviderSelection(ProviderSeam.Sessions, EventsOwner.NativePostgresSessionsProvider.ProviderId)],
                []).Value);
            var bindings = new EventsOwner.SessionsProviderBindingService(
                provider, catalog, new ProviderResolver(catalog), options, _dataSource, Journal);
            var binding = await bindings.ResolveAndPinAsync(Principal);
            await Journal.CreateSessionAsync(Principal, "gateway-session", binding);

            FirstEvent = (await AppendAsync(1)).Event;
            SecondEvent = (await AppendAsync(2)).Event;
            ThirdEvent = (await AppendAsync(3)).Event;

            var firstPage = await ReplayRunAsync(projectId, runId, null, 1, CancellationToken.None);
            var secondPage = await ReplayRunAsync(
                projectId, runId, firstPage.NextCursor, 1, CancellationToken.None);
            FirstCursor = firstPage.NextCursor
                ?? throw new InvalidOperationException("The first committed Events page omitted its cursor.");
            SecondCursor = secondPage.NextCursor
                ?? throw new InvalidOperationException("The second committed Events page omitted its cursor.");
            Assert.Equal(FirstEvent.EventId, Assert.Single(firstPage.Events).EventId);
            Assert.Equal(SecondEvent.EventId, Assert.Single(secondPage.Events).EventId);

            Journal = new EventsOwner.PostgresSessionsJournal(_dataSource, options);
            var replayAfterRestart = await ReplayRunAsync(
                projectId, runId, FirstCursor, 2, CancellationToken.None);
            Assert.Equal(
                new[] { SecondEvent.EventId, ThirdEvent.EventId },
                replayAfterRestart.Events.Select(item => item.EventId));

            Task<SessionAppendResult> AppendAsync(int ordinal) =>
                Journal.AppendAsync(
                    Principal,
                    "gateway-session",
                    new AppendSessionEvent(
                        Guid.NewGuid(),
                        SessionsContractVersions.CurrentSchemaVersion,
                        SessionsContractVersions.CurrentEventVersion,
                        new TurnSessionPayload(
                            "user",
                            new SessionObjectReference(
                                new ObjectKey($"turns/gateway-{ordinal}"),
                                "transcript",
                                128))));
        }
    }
}
