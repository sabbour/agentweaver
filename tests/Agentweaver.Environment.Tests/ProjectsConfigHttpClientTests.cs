using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class ProjectsConfigHttpClientTests
{
    [Fact]
    public async Task ReadsFreshAuthorizationAndEffectiveSelectionUsingTheCurrentCaller()
    {
        const string token = "validated.jwt.token";
        var authorizationResponse = Json(ProjectAuthorizationContext());
        var selectionResponse = Json(Selection());
        Assert.Contains("\"resourceType\":\"project\"", authorizationResponse, StringComparison.Ordinal);
        Assert.Contains("\"permission\":\"readRunSelection\"", authorizationResponse, StringComparison.Ordinal);
        Assert.Contains("\"purpose\":\"sourceControl\"", selectionResponse, StringComparison.Ordinal);
        Assert.Contains("\"destinationKind\":\"fqdn\"", selectionResponse, StringComparison.Ordinal);
        Assert.Contains("\"protocol\":\"tcp\"", selectionResponse, StringComparison.Ordinal);
        var handler = new RecordingHandler(
            authorizationResponse,
            selectionResponse);
        var client = new ProjectsConfigHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://projects-config.example")
        });

        var caller = new CurrentCallerRequest(token, "tenant-a");
        var authorization = await client.GetAuthorizationContextAsync(caller, CancellationToken.None);
        var selection = await client.GetRunSelectionAsync(
            caller, "project-a", "run-a", CancellationToken.None);

        Assert.Equal(1, authorization.ContractVersion);
        Assert.Equal("actor-a", authorization.ActorId);
        Assert.Equal("tenant-a", authorization.TenantId);
        Assert.Equal(
            ProjectAuthorityResourceType.Project,
            authorization.EffectiveAuthority.Single().ResourceType);
        Assert.Contains(
            authorization.EffectiveAuthority.Single().Permissions,
            grant => grant.Permission == ProjectAuthorizationPermission.ReadRunSelection);
        Assert.Contains(
            authorization.EffectiveAuthority.Single().Permissions,
            grant => grant.Permission == ProjectAuthorizationPermission.WriteProjects);
        Assert.Equal("project-a", selection.ProjectId);
        Assert.Equal("run-a", selection.RunId);
        Assert.Equal(NetworkEgressPurpose.SourceControl, selection.RequiredEgress.Single().Purpose);
        Assert.Equal(NetworkEgressDestinationKind.Fqdn, selection.RequiredEgress.Single().DestinationKind);
        Assert.Equal(EgressProtocol.Tcp, selection.RequiredEgress.Single().Protocol);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer validated.jwt.token", request.Authorization);
            Assert.Equal("tenant-a", request.TenantSelector);
            Assert.True(request.NoStore);
            Assert.DoesNotContain("validated.jwt.token", request.Path);
        });
        Assert.Equal("/api/authorization/context", handler.Requests[0].Path);
        Assert.Equal("/api/projects/project-a/runs/run-a/selection", handler.Requests[1].Path);
        Assert.Equal("CurrentCallerRequest [token redacted]", caller.ToString());
    }

    [Fact]
    public async Task MissingNoStoreOrWrongTenantFailsClosed()
    {
        var missingNoStore = new ProjectsConfigHttpClient(new HttpClient(
            new RecordingHandler(Json(ProjectAuthorizationContext()), Json(Selection()), noStore: false))
        {
            BaseAddress = new Uri("https://projects-config.example")
        });
        await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            missingNoStore.GetAuthorizationContextAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                CancellationToken.None));

        var wrongTenant = new ProjectsConfigHttpClient(new HttpClient(
            new RecordingHandler(Json(ProjectAuthorizationContext()), Json(Selection())))
        {
            BaseAddress = new Uri("https://projects-config.example")
        });
        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            wrongTenant.GetAuthorizationContextAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-b"),
                CancellationToken.None));
        Assert.Equal("tenant_selector_mismatch", exception.Code);
    }

    [Fact]
    public async Task TenantAndProjectRunBindingsMustBeValidatedBeforeReturningSelection()
    {
        var wrongProjectHandler = new RecordingHandler(
            Json(ProjectAuthorizationContext()),
            Json(Selection() with { ProjectId = "other-project" }));
        var client = new ProjectsConfigHttpClient(new HttpClient(wrongProjectHandler)
        {
            BaseAddress = new Uri("https://projects-config.example")
        });

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            client.GetRunSelectionAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a",
                "run-a",
                CancellationToken.None));
        Assert.Equal("run_selection_mismatch", exception.Code);
        Assert.Single(wrongProjectHandler.Requests);
    }

    [Fact]
    public async Task ProjectsSelectionDenialIsReportedAsMissingRunSelectionAuthority()
    {
        var handler = new RecordingHandler(
            Json(ProjectAuthorizationContext()),
            Json(Selection()),
            selectionStatus: HttpStatusCode.Forbidden);
        var client = new ProjectsConfigHttpClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://projects-config.example")
        });

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            client.GetRunSelectionAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a",
                "run-a",
                CancellationToken.None));

        Assert.Equal("run_selection_not_authorized", exception.Code);
        Assert.Single(handler.Requests);

        var wrongAudienceHandler = new RecordingHandler(
            Json(ProjectAuthorizationContext()),
            Json(Selection()),
            authorizationStatus: HttpStatusCode.Unauthorized);
        var wrongAudienceClient = new ProjectsConfigHttpClient(new HttpClient(wrongAudienceHandler)
        {
            BaseAddress = new Uri("https://projects-config.example")
        });
        var audienceFailure = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            wrongAudienceClient.GetAuthorizationContextAsync(
                new CurrentCallerRequest("wrong-audience.jwt.token", "tenant-a"),
                CancellationToken.None));

        Assert.Equal("authorization_context_denied", audienceFailure.Code);
        Assert.Single(wrongAudienceHandler.Requests);
    }

    private static string Json<T>(T value)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(value, options);
    }

    private static ProjectAuthorizationContextResponse ProjectAuthorizationContext() =>
        new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://identity.example",
            "actor-a",
            "tenant-a",
            2,
            null,
            null,
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    [
                        new ProjectAuthorizationPermissionGrant(
                            ProjectAuthorizationPermission.ReadRunSelection,
                            3),
                        new ProjectAuthorizationPermissionGrant(
                            ProjectAuthorizationPermission.WriteProjects,
                            4)
                    ])
            ]);

    private static EffectiveNetworkPolicySelection Selection()
    {
        var rule = new NetworkEgressRule(
            NetworkEgressPurpose.SourceControl,
            NetworkEgressDestinationKind.Fqdn,
            "api.github.com",
            443,
            EgressProtocol.Tcp);
        return new EffectiveNetworkPolicySelection(
            "project-a",
            "run-a",
            1,
            1,
            1,
            "selection-1",
            [],
            [rule],
            null,
            [rule],
            [rule]);
    }

    private sealed class RecordingHandler(
        string authorizationResponse,
        string selectionResponse,
        bool noStore = true,
        HttpStatusCode authorizationStatus = HttpStatusCode.OK,
        HttpStatusCode selectionStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isContextRequest = request.RequestUri!.AbsolutePath == "/api/authorization/context";
            var body = isContextRequest
                ? authorizationResponse
                : selectionResponse;
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues(
                    ProjectAuthorizationContextContract.TenantSelectorHeader,
                    out var values) ? values.Single() : null,
                request.Headers.CacheControl?.NoStore == true));
            var response = new HttpResponseMessage(
                isContextRequest ? authorizationStatus : selectionStatus)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (noStore)
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoStore = true
                };
            return await Task.FromResult(response);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? Authorization,
        string? TenantSelector,
        bool NoStore);
}
