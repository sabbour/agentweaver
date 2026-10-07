extern alias GatewayHost;
extern alias EventsHost;
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
using EventsOwner = EventsHost::Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
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
        Assert.Contains(projectOwnerRequests, request =>
            request.Uri.AbsolutePath == $"/api/projects/{project.ProjectId}" &&
            QueryHelpers.ParseQuery(request.Uri.Query)["runId"].SingleOrDefault() == runId &&
            request.Authorization == "Bearer " + runToken &&
            request.Tenant == TenantId);
        Assert.Contains(ownerRequests, request =>
            request.Owner == GatewayOwner.Events &&
            QueryHelpers.ParseQuery(request.Uri.Query).TryGetValue("cursor", out var cursorValues) &&
            cursorValues.SingleOrDefault() == events.FirstCursor);
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

    private static string CreateGatewayTestToken(
        X509Certificate2 certificate,
        string audience,
        DateTime expiresAt)
    {
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience,
            [new System.Security.Claims.Claim("sub", "gateway-negative-test")],
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
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
