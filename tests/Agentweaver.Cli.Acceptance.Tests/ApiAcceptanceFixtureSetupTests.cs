using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class ApiAcceptanceFixtureSetupTests
{
    private const string ProjectId = "11111111-1111-4111-8111-111111111111";
    private const string OwnerAssignmentId = "22222222-2222-4222-8222-222222222222";
    private const string OrchestratorAssignmentId = "33333333-3333-4333-8333-333333333333";
    private const string ActorId = "actor-1";
    private const string Issuer = "https://issuer.example/";
    private const string TenantId = "tenant-1";

    [Fact]
    public async Task NativeSetupUsesAssignmentListContractAndCleansUpBeforeRunSetup()
    {
        await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
        string address = gateway.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage response = await http.GetAsync(new Uri(new Uri(address), "/openapi/v1.json"));
        response.EnsureSuccessStatusCode();
        JsonObject openApi = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        JsonObject environment = new()
        {
            ["baseUrl"] = address,
            ["issuer"] = Issuer,
            ["audience"] = "agentweaver",
            ["tenantId"] = TenantId,
            ["actorId"] = ActorId,
        };
        AcceptanceManifest manifest = new(
            new JsonObject(), environment, new JsonObject(), new JsonObject(),
            "native-material-recovery", "input.json", ".");
        ScriptedClient setupApi = new(openApi);
        ScriptedAuthentication authentication = new();
        ScriptedClientFactory clients = new();

        AcceptanceFixtureSetupException setupError = await Assert.ThrowsAsync<AcceptanceFixtureSetupException>(
            () => ApiAcceptanceFixtureSetup.CreateAsync(
                manifest, "44444444-4444-4444-8444-444444444444", setupApi, clients, authentication,
                InteractionPolicy.Never, CancellationToken.None));

        Assert.Contains("getProjectConfiguration operation returned HTTP 503",
            setupError.InnerException?.Message ?? setupError.Message);
        Assert.True(setupApi.Operations.Contains("getProjectConfiguration"), setupError.ToString());
        Assert.DoesNotContain("createProjectRun", setupApi.Operations);
        Assert.DoesNotContain("acceptRunSelection", setupApi.Operations);
        Assert.False(setupError.State.RunSetupAttempted);
        Assert.True(setupError.State.SetupComplete);

        JsonObject cleanup = await ApiAcceptanceFixtureSetup.CleanupAsync(
            setupApi, null, null, setupError.State, ActorId, CancellationToken.None);

        Assert.Equal("cleaned", ApiAcceptanceSupport.GetString(cleanup, "state"));
        Assert.Equal("archived", ApiAcceptanceSupport.GetString(cleanup, "projectReadback"));
        Assert.Equal("empty", ApiAcceptanceSupport.GetString(cleanup, "assignmentsReadback"));
        Assert.Contains("revokeProjectRoleAssignment", setupApi.Operations);
    }

    [Fact]
    public async Task CleanupRetainsAuthorityWhenRunSetupWasAttemptedButIdentityIsUnknown()
    {
        AcceptanceFixtureState state = new("44444444-4444-4444-8444-444444444444")
        {
            SetupAttempted = true,
            SetupComplete = true,
            RunSetupAttempted = true,
            VerifyCleanupReadback = true,
            Setup = new JsonObject { ["projectId"] = ProjectId },
        };
        ScriptedClient setupApi = new(new JsonObject());

        JsonObject cleanup = await ApiAcceptanceFixtureSetup.CleanupAsync(
            setupApi, null, null, state, ActorId, CancellationToken.None);

        Assert.Equal("indeterminate", ApiAcceptanceSupport.GetString(cleanup, "state"));
        Assert.Contains("reconcile", ApiAcceptanceSupport.GetString(cleanup, "reason"));
        Assert.Empty(setupApi.Operations);
    }

    private sealed class ScriptedClient(JsonObject openApi) : IGatewayClient
    {
        private readonly JsonObject _openApi = (JsonObject)openApi.DeepClone();
        private bool _archived;
        private bool _assignmentsRevoked;

        public CliIdentity Identity { get; } = new(
            new CliTarget(new Uri("https://gateway.example/"), new Uri(Issuer), "test", "agentweaver", TenantId),
            ["openid", "api.read", "offline_access"]);

        public IReadOnlyList<JsonObject> Calls => [];
        public List<string> Operations { get; } = [];

        public Task<JsonObject> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult((JsonObject)_openApi.DeepClone());

        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) =>
            Task.FromResult(AuthorizationContext());

        public Task<GatewayResponse> InvokeAsync(string operationId, GatewayInvocation invocation, CancellationToken ct)
        {
            Operations.Add(operationId);
            (int status, JsonNode? body) = operationId switch
            {
                "createP1InvocationSetup" => (200, new JsonObject
                {
                    ["invocationId"] = invocation.PathParameters!["invocationId"],
                    ["issuer"] = Issuer,
                    ["subject"] = ActorId,
                    ["createdByActorId"] = ActorId,
                    ["tenantId"] = TenantId,
                    ["membershipId"] = "55555555-5555-4555-8555-555555555555",
                    ["membershipRevision"] = 1L,
                    ["projectId"] = ProjectId,
                    ["projectRevision"] = 1L,
                    ["projectConfigurationRevision"] = 1L,
                    ["assignmentId"] = OrchestratorAssignmentId,
                    ["assignmentRevision"] = 1L,
                    ["authorityAuditEventId"] = "66666666-6666-4666-8666-666666666666",
                    ["ownerAssignmentId"] = OwnerAssignmentId,
                    ["ownerAssignmentRevision"] = 1L,
                    ["ownerAuthorityAuditEventId"] = "77777777-7777-4777-8777-777777777777",
                }),
                "getProject" => (200, Project()),
                "listProjectRoleAssignments" => (200, Assignments()),
                "getProjectConfiguration" => (503, new JsonObject { ["code"] = "fixture_config_unavailable" }),
                "revokeProjectRoleAssignment" => (200, Revoke(invocation)),
                "updateProject" => (200, Archive()),
                _ => throw new InvalidOperationException($"Unexpected operation {operationId}."),
            };
            return Task.FromResult(new GatewayResponse(status, body, new Dictionary<string, string>(), new JsonObject()));
        }

        public Task<GatewayResponse> InvokeRawAsync(
            string method,
            string url,
            byte[]? body,
            IReadOnlyDictionary<string, string>? headers,
            bool outputRequested,
            Func<string, ValueTask>? onEventChunk,
            CancellationToken ct) =>
            throw new NotSupportedException();

        private static JsonObject AuthorizationContext() => new()
        {
            ["contractVersion"] = 1L,
            ["issuer"] = Issuer,
            ["actorId"] = ActorId,
            ["tenantId"] = TenantId,
            ["boundProjectId"] = null,
            ["boundRunId"] = null,
            ["effectiveAuthority"] = new JsonArray(),
        };

        private JsonObject Project() => new()
        {
            ["projectId"] = ProjectId,
            ["name"] = "p1-44444444-4444-4444-8444-444444444444",
            ["state"] = _archived ? "archived" : "active",
            ["revision"] = _archived ? 2L : 1L,
            ["configurationRevision"] = 1L,
        };

        private JsonObject Assignments()
        {
            if (_assignmentsRevoked)
                return new JsonObject { ["projectId"] = ProjectId, ["assignments"] = new JsonArray() };

            return new JsonObject
            {
                ["projectId"] = ProjectId,
                ["assignments"] = new JsonArray(
                    new JsonObject
                    {
                        ["assignmentId"] = OwnerAssignmentId,
                        ["principalId"] = ActorId,
                        ["role"] = "owner",
                        ["revision"] = 1L,
                        ["createdBy"] = ActorId,
                        ["createdAt"] = "2026-11-09T15:00:00Z",
                    },
                    new JsonObject
                    {
                        ["assignmentId"] = OrchestratorAssignmentId,
                        ["principalId"] = ActorId,
                        ["role"] = "orchestrator",
                        ["revision"] = 1L,
                        ["createdBy"] = ActorId,
                        ["createdAt"] = "2026-11-09T15:00:00Z",
                    }),
            };
        }

        private JsonObject Revoke(GatewayInvocation invocation)
        {
            string assignmentId = invocation.PathParameters!["assignmentId"];
            string role = assignmentId == OwnerAssignmentId ? "owner" : "orchestrator";
            if (role == "owner")
                _assignmentsRevoked = true;
            return new JsonObject
            {
                ["assignmentId"] = assignmentId,
                ["projectId"] = ProjectId,
                ["principalId"] = ActorId,
                ["role"] = role,
                ["state"] = "revoked",
                ["revision"] = 2L,
                ["grantedBy"] = ActorId,
                ["revokedBy"] = ActorId,
            };
        }

        private JsonObject Archive()
        {
            _archived = true;
            return Project();
        }
    }

    private sealed class ScriptedClientFactory : IGatewayClientFactory
    {
        public IGatewayClient Create(CliIdentity identity) =>
            throw new InvalidOperationException("Run clients must not be created before run setup.");
    }

    private sealed class ScriptedAuthentication : ICliAuthentication
    {
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct) =>
            throw new InvalidOperationException("Run authorization must not be requested before run setup.");
    }
}
