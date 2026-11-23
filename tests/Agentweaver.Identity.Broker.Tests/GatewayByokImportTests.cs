extern alias ProjectsConfig;
extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class GatewayByokImportTests(PostgresContainerFixture postgres)
{
    [Fact]
    public void ImportContractAcceptsTheEmittedGatewayOpenApiDocument()
    {
        var openApi = typeof(GatewayHost::Agentweaver.Gateway.GatewayEndpoints).Assembly
            .GetType("Agentweaver.Gateway.GatewayOpenApi")!;
        var emitted = openApi.GetMethod(
            "CreateDocument",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        var document = JsonNode.Parse(JsonSerializer.Serialize(emitted))!.AsObject();

        ByokImportContract.Require(document, []);
    }

    [Fact]
    public async Task CliReplaysAgainstOriginalOwnerAndRejectsAnotherDatabase()
    {
        var firstConnection = await postgres.CreateMigratedDatabaseAsync();
        var secondConnection = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var a = await ProjectsConfigResourceServer.StartAsync(firstConnection, key, enableProviderReferences: true);
        await using var b = await ProjectsConfigResourceServer.StartAsync(secondConnection, key, enableProviderReferences: true);
        var tenant = "tenant-" + Guid.NewGuid().ToString("N");
        var actor = "actor-" + Guid.NewGuid().ToString("N");
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        async Task<(DbContextOptions<ProjectsConfigDbContext> Options, string Id, Guid MembershipId, Guid GrantId, long Revision)> Prepare(ProjectsConfigResourceServer owner)
        {
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(owner.PrivilegedFixtureDataSource, c => c.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            var membership = await authority.GrantMembershipAsync(issuer, actor, tenant, "fixture");
            var grant = await authority.AssignRoleAsync(membership.MembershipId, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
            await using var connection = await owner.PrivilegedFixtureDataSource.OpenConnectionAsync();
            await using var query = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection);
            return (options, ((Guid)(await query.ExecuteScalarAsync())!).ToString("D"),
                membership.MembershipId, grant.AssignmentId, grant.Revision);
        }
        var first = await Prepare(a);
        var second = await Prepare(b);
        Assert.NotEqual(first.Id, second.Id);
        var token = Token(key, actor);
        var ownerRequests = new ConcurrentQueue<OwnerRequest>();
        void ObserveOwner(string owner, HttpRequestMessage request)
        {
            ownerRequests.Enqueue(new OwnerRequest(
                owner, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var selected) ? selected.Single() : null));
        }
        await using var gatewayA = await GatewayResourceServer.StartAsync(
            key, a.CreateHandler, (_, _, _) => throw new InvalidOperationException(),
            observeProjectsRequest: request => ObserveOwner("A", request));
        await using var gatewayB = await GatewayResourceServer.StartAsync(
            key, b.CreateHandler, (_, _, _) => throw new InvalidOperationException(),
            observeProjectsRequest: request => ObserveOwner("B", request));
        using var routing = new GatewayRouter(gatewayA.Client, gatewayB.Client);
        using var http = new HttpClient(routing, disposeHandler: false);
        ICliAuthentication auth = new FixtureAuthentication(() => Token(key, actor));
        var directory = Path.Combine(AppContext.BaseDirectory, "byok-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var overlay = Path.Combine(directory, "overlay.json");
            var context = Path.Combine(directory, "context.json");
            await File.WriteAllTextAsync(overlay, """
                {"kind":"agentweaver.model-bindings-overlay","schemaVersion":1,"baseRevision":"base",
                 "baseModelBindings":{},"revision":"next","modelBindings":{
                 "ImportAlpha":{"ModelId":"model","SourceMode":"Byok","Provider":{"Type":"openai","BaseUrl":"https://model.example/v1","WireApi":"responses"},"SupportedReasoningEfforts":["low"]},
                 "ImportZulu":{"ModelId":"model","SourceMode":"Byok","Provider":{"Type":"openai","BaseUrl":"https://model.example/v1","WireApi":"responses"},"SupportedReasoningEfforts":["high"]}},
                 "rateCard":{"id":"price","version":"2026-01-01"},"modelRates":{"model":{"input":"1","output":"2"}}}
                """);
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(overlay))).ToLowerInvariant();
            var target = new[] { "--gateway-url", "https://gateway.test/", "--broker-issuer", issuer,
                "--api-audience", "https://api.test", "--tenant-id", tenant };
            async Task<(int Exit, JsonNode? Output)> Run(string[] args, bool loseOutput = false)
            {
                TextWriter output = loseOutput ? new FailingOutput() : new StringWriter();
                var error = new StringWriter();
                var exit = await CliApplication.RunAsync(args,
                    new CliRuntimeOptions(HttpClient: http, Authentication: auth, Output: output, Error: error));
                var text = output.ToString() ?? string.Empty;
                return (exit, text.StartsWith('{') ? JsonNode.Parse(text) : null);
            }
            Assert.Equal(0, (await Run(["models", "byok", "import-context", "--output", context, .. target])).Exit);
            var captured = JsonNode.Parse(await File.ReadAllTextAsync(context))!.AsObject();
            Assert.Equal(4, captured.Count);
            Assert.Equal(issuer.TrimEnd('/'), captured["issuer"]!.GetValue<string>());
            Assert.Equal(tenant, captured["tenantId"]!.GetValue<string>());
            Assert.Equal(actor, captured["actorId"]!.GetValue<string>());
            Assert.Equal(first.Id, captured["installationId"]!.GetValue<string>());
            var firstTarget = Assert.Single(routing.TargetResponses);
            Assert.Equal("A", firstTarget.Owner);
            Assert.Equal(200, firstTarget.Status);
            Assert.True(firstTarget.NoStore);
            Assert.Equal(issuer, firstTarget.Body!["issuer"]!.GetValue<string>());
            Assert.Equal(actor, firstTarget.Body["actorId"]!.GetValue<string>());
            Assert.Equal(tenant, firstTarget.Body["tenantId"]!.GetValue<string>());
            Assert.Equal(first.Id, firstTarget.Body["installationId"]!.GetValue<string>());
            Assert.Contains(ownerRequests, request => request.Owner == "A" &&
                request.Path == "/api/authorization/target-context" &&
                request.Bearer == firstTarget.Bearer && request.Tenant == tenant);
            string[] import = ["models", "byok", "import", "--model-bindings", overlay, "--file-sha256", digest,
                "--expected-context", context, .. target];
            routing.LoseNextResponse = true;
            var lost = await Run(import);
            Assert.Equal(1, lost.Exit);
            Assert.Equal("unknown", lost.Output!["stop"]!["outcome"]!.GetValue<string>());
            var original = Assert.Single(routing.PostBodies);
            var firstPost = Assert.Single(routing.PostRequests);
            var precedingTarget = routing.TargetResponses.Last();
            Assert.Equal("A", precedingTarget.Owner);
            Assert.NotEqual(firstTarget.Bearer, precedingTarget.Bearer);
            Assert.Equal(precedingTarget.Bearer, firstPost.Bearer);
            Assert.Equal(tenant, firstPost.Tenant);
            Assert.Contains(ownerRequests, request => request.Owner == "A" &&
                request.Path == "/api/authorization/target-context" &&
                request.Bearer == firstPost.Bearer && request.Tenant == tenant);
            Assert.Contains(ownerRequests, request => request.Owner == "A" &&
                request.Path == "/api/platform/byok-providers" &&
                request.Bearer == firstPost.Bearer && request.Tenant == tenant);
            await using (var db = new ProjectsConfigDbContext(first.Options))
                Assert.Equal(1, await db.ModelProviderReferenceReservations.CountAsync());

            var otherTenant = "tenant-" + Guid.NewGuid().ToString("N");
            var tenantAuthority = new ProjectsConfigPrivilegedAuthorityStore(first.Options, TimeProvider.System);
            var otherMembership = await tenantAuthority.GrantMembershipAsync(issuer, actor, otherTenant, "fixture");
            await tenantAuthority.AssignRoleAsync(otherMembership.MembershipId, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
            var otherTarget = new[] { "--gateway-url", "https://gateway.test/", "--broker-issuer", issuer,
                "--api-audience", "https://api.test", "--tenant-id", otherTenant };
            var otherTenantResult = await Run(["models", "byok", "import", "--model-bindings", overlay,
                "--file-sha256", digest, "--expected-context", context, .. otherTarget]);
            Assert.Equal("expected_context_mismatch", otherTenantResult.Output!["stop"]!["code"]!.GetValue<string>());
            Assert.Single(routing.PostBodies);
            var tenantContext = routing.TargetResponses.Last();
            Assert.Equal(("A", 200), (tenantContext.Owner, tenantContext.Status));
            Assert.Equal(otherTenant, tenantContext.Body!["tenantId"]!.GetValue<string>());
            Assert.Equal(otherTenant, tenantContext.Tenant);

            routing.ContextOnB = true;
            var wrong = await Run(import);
            Assert.Equal("expected_context_mismatch", wrong.Output!["stop"]!["code"]!.GetValue<string>());
            Assert.Single(routing.PostBodies);
            var redirectedTarget = routing.TargetResponses.Last();
            Assert.Equal(("B", 200), (redirectedTarget.Owner, redirectedTarget.Status));
            Assert.Equal(second.Id, redirectedTarget.Body!["installationId"]!.GetValue<string>());
            await using (var db = new ProjectsConfigDbContext(second.Options))
            {
                Assert.Empty(await db.ModelProviderReferenceReservations.ToListAsync());
                Assert.Empty(await db.ModelProviderDrafts.ToListAsync());
                Assert.Empty(await db.ModelProviderDraftRevisions.ToListAsync());
            }

            routing.ContextOnB = false;
            routing.PostOnB = true;
            var crossed = await Run(import);
            Assert.Equal("target_installation_mismatch", crossed.Output!["stop"]!["code"]!.GetValue<string>());
            Assert.Equal(first.Id, JsonNode.Parse(routing.PostBodies[1])!["targetInstallationId"]!.GetValue<string>());
            var crossedPost = routing.PostRequests[1];
            Assert.Equal(("B", 409), (crossedPost.Owner, crossedPost.Status));
            Assert.Equal(routing.TargetResponses.Last().Bearer, crossedPost.Bearer);
            Assert.Contains(ownerRequests, request => request.Owner == "B" &&
                request.Path == "/api/platform/byok-providers" && request.Bearer == crossedPost.Bearer);
            await using (var db = new ProjectsConfigDbContext(second.Options))
            {
                Assert.Empty(await db.ModelProviderReferenceReservations.ToListAsync());
                Assert.Empty(await db.ModelProviderDrafts.ToListAsync());
            }

            routing.PostOnB = false;
            var recovered = await Run(import);
            Assert.Equal(0, recovered.Exit);
            Assert.Equal(2, recovered.Output!["completed"]!.AsArray().Count);
            Assert.Equal(original, routing.PostBodies[2]);
            await using (var db = new ProjectsConfigDbContext(first.Options))
            {
                Assert.Equal(2, await db.ModelProviderReferenceReservations.CountAsync());
                Assert.All(await db.ModelProviderDrafts.ToListAsync(), row => Assert.Equal(ModelProviderDraftState.Draft, row.State));
            }
            await using (var disabled = await ProjectsConfigResourceServer.StartAsync(firstConnection, key))
            await using (var disabledGateway = await GatewayResourceServer.StartAsync(
                key, disabled.CreateHandler, (_, _, _) => throw new InvalidOperationException()))
            using (var disabledRouting = new GatewayRouter(disabledGateway.Client, gatewayB.Client))
            using (var disabledClient = new HttpClient(disabledRouting, disposeHandler: false))
            {
                async Task<(int Exit, JsonNode Output)> RunDisabled(string file, string sha)
                {
                    var output = new StringWriter();
                    var exit = await CliApplication.RunAsync(
                        ["models", "byok", "import", "--model-bindings", file, "--file-sha256", sha,
                            "--expected-context", context, .. target],
                        new CliRuntimeOptions(HttpClient: disabledClient, Authentication: auth,
                            Output: output, Error: new StringWriter()));
                    return (exit, JsonNode.Parse(output.ToString())!);
                }
                Assert.Equal(0, (await RunDisabled(overlay, digest)).Exit);
                var newOverlay = JsonNode.Parse(await File.ReadAllTextAsync(overlay))!.AsObject();
                var newBindings = newOverlay["modelBindings"]!.AsObject();
                var newBinding = newBindings["ImportAlpha"]!.DeepClone();
                newBindings.Clear();
                newBindings["DisabledReference"] = newBinding;
                var newPath = Path.Combine(directory, "disabled.json");
                await File.WriteAllTextAsync(newPath, newOverlay.ToJsonString());
                var newDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(newPath))).ToLowerInvariant();
                var unavailable = await RunDisabled(newPath, newDigest);
                Assert.Equal(1, unavailable.Exit);
                Assert.Equal("unknown", unavailable.Output["stop"]!["outcome"]!.GetValue<string>());
                await using var db = new ProjectsConfigDbContext(first.Options);
                Assert.Equal(2, await db.ModelProviderReferenceReservations.CountAsync());
            }
            Assert.All(await Task.WhenAll(Run(import), Run(import)), result => Assert.Equal(0, result.Exit));
            Assert.Equal(1, (await Run(import, loseOutput: true)).Exit);
            var beforeWrongTarget = routing.PostBodies.Count;
            routing.ContextOnB = true;
            Assert.Equal("expected_context_mismatch", (await Run(import)).Output!["stop"]!["code"]!.GetValue<string>());
            Assert.Equal(beforeWrongTarget, routing.PostBodies.Count);
            routing.ContextOnB = false;
            Assert.Equal(0, (await Run(import)).Exit);

            var authorityB = new ProjectsConfigPrivilegedAuthorityStore(second.Options, TimeProvider.System);
            await authorityB.RevokeRoleAssignmentAsync(second.GrantId, second.Revision, "fixture");
            routing.PostOnB = true;
            var denied = await Run(import);
            Assert.Equal("unknown", denied.Output!["stop"]!["outcome"]!.GetValue<string>());
            await using (var db = new ProjectsConfigDbContext(second.Options))
                Assert.Empty(await db.ModelProviderReferenceReservations.ToListAsync());
            routing.PostOnB = false;

            await authorityB.AssignRoleAsync(second.MembershipId, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
            routing.ContextOnB = true;
            routing.PostOnB = true;
            var contextB = Path.Combine(directory, "context-b.json");
            Assert.Equal(0, (await Run(["models", "byok", "import-context", "--output", contextB, .. target])).Exit);
            var separate = await Run(["models", "byok", "import", "--model-bindings", overlay, "--file-sha256", digest,
                "--expected-context", contextB, .. target]);
            Assert.Equal(0, separate.Exit);
            Assert.NotEqual(recovered.Output!["completed"]![0]!["providerId"]!.GetValue<string>(),
                separate.Output!["completed"]![0]!["providerId"]!.GetValue<string>());
            await using (var db = new ProjectsConfigDbContext(second.Options))
                Assert.Equal(2, await db.ModelProviderReferenceReservations.CountAsync());
            routing.ContextOnB = false;
            routing.PostOnB = false;

            var otherActor = "other-" + Guid.NewGuid().ToString("N");
            var authorityA = new ProjectsConfigPrivilegedAuthorityStore(first.Options, TimeProvider.System);
            var member = await authorityA.GrantMembershipAsync(issuer, otherActor, tenant, "fixture");
            var otherGrant = await authorityA.AssignRoleAsync(member.MembershipId, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
            var otherToken = Token(key, otherActor);
            auth = new FixtureAuthentication(() => Token(key, otherActor));
            var postCountBeforeActorSwitch = routing.PostBodies.Count;
            var actorSwitch = await Run(import);
            Assert.Equal("expected_context_mismatch", actorSwitch.Output!["stop"]!["code"]!.GetValue<string>());
            Assert.Equal(postCountBeforeActorSwitch, routing.PostBodies.Count);
            var actualOtherContext = routing.TargetResponses.Last();
            Assert.Equal(otherActor, actualOtherContext.Body!["actorId"]!.GetValue<string>());
            var changedContext = JsonNode.Parse(await File.ReadAllTextAsync(context))!.AsObject();
            changedContext["actorId"] = otherActor;
            await File.WriteAllTextAsync(context, changedContext.ToJsonString());
            var foreign = await Run(import);
            Assert.Equal("model_provider_draft_conflict", foreign.Output!["stop"]!["code"]!.GetValue<string>());
            Assert.Equal(JsonNode.Parse(original)!["operationId"]!.GetValue<string>(),
                JsonNode.Parse(routing.PostBodies.Last())!["operationId"]!.GetValue<string>());
            await using (var db = new ProjectsConfigDbContext(first.Options))
                Assert.Equal(2, await db.ModelProviderReferenceReservations.CountAsync());

            var raceOverlay = JsonNode.Parse(await File.ReadAllTextAsync(overlay))!.AsObject();
            var raceBindings = raceOverlay["modelBindings"]!.AsObject();
            var raceBinding = raceBindings["ImportAlpha"]!.DeepClone();
            raceBindings.Clear();
            raceBindings["RaceAlpha"] = raceBinding;
            var racePath = Path.Combine(directory, "race.json");
            await File.WriteAllTextAsync(racePath, raceOverlay.ToJsonString());
            var raceDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(racePath))).ToLowerInvariant();
            var actorOneContext = changedContext.DeepClone().AsObject();
            actorOneContext["actorId"] = actor;
            var actorOnePath = Path.Combine(directory, "actor-one-context.json");
            await File.WriteAllTextAsync(actorOnePath, actorOneContext.ToJsonString());
            using var oneShim = new GatewayRouter(gatewayA.Client, gatewayB.Client);
            using var twoShim = new GatewayRouter(gatewayA.Client, gatewayB.Client);
            using var oneClient = new HttpClient(oneShim, disposeHandler: false);
            using var twoClient = new HttpClient(twoShim, disposeHandler: false);
            async Task<(int Exit, JsonNode Result)> Race(HttpClient client, string bearerToken, string contextFile)
            {
                var output = new StringWriter();
                var exit = await CliApplication.RunAsync(
                    ["models", "byok", "import", "--model-bindings", racePath, "--file-sha256", raceDigest,
                        "--expected-context", contextFile, .. target],
                    new CliRuntimeOptions(HttpClient: client, Authentication: new FixtureAuthentication(() => bearerToken),
                        Output: output, Error: new StringWriter()));
                return (exit, JsonNode.Parse(output.ToString())!);
            }
            var raceResults = await Task.WhenAll(Race(oneClient, token, actorOnePath), Race(twoClient, otherToken, context));
            Assert.Single(raceResults, result => result.Exit == 0);
            var loser = Assert.Single(raceResults, result => result.Exit == 1);
            Assert.Equal("model_provider_draft_conflict", loser.Result["stop"]!["code"]!.GetValue<string>());
            Assert.Equal(JsonNode.Parse(Assert.Single(oneShim.PostBodies))!["operationId"]!.GetValue<string>(),
                JsonNode.Parse(Assert.Single(twoShim.PostBodies))!["operationId"]!.GetValue<string>());
            await using (var db = new ProjectsConfigDbContext(first.Options))
                Assert.Equal(3, await db.ModelProviderReferenceReservations.CountAsync());

            var flowOverlay = JsonNode.Parse(await File.ReadAllTextAsync(overlay))!.AsObject();
            var flowBindings = flowOverlay["modelBindings"]!.AsObject();
            var firstBinding = flowBindings["ImportAlpha"]!.DeepClone();
            var secondBinding = flowBindings["ImportZulu"]!.DeepClone();
            flowBindings.Clear();
            flowBindings["FlowAlpha"] = firstBinding;
            flowBindings["FlowZulu"] = secondBinding;
            var flowPath = Path.Combine(directory, "flow.json");
            await File.WriteAllTextAsync(flowPath, flowOverlay.ToJsonString());
            var flowDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(flowPath))).ToLowerInvariant();
            var tokenCount = 0;
            auth = new FixtureAuthentication(() =>
                Token(key, Interlocked.Increment(ref tokenCount) < 3 ? actor : otherActor));
            var beforeFlow = routing.PostBodies.Count;
            var flow = await Run(["models", "byok", "import", "--model-bindings", flowPath,
                "--file-sha256", flowDigest, "--expected-context", actorOnePath, .. target]);
            Assert.Equal(1, flow.Exit);
            Assert.Single(flow.Output!["completed"]!.AsArray());
            Assert.Equal("FlowZulu", flow.Output["stop"]!["reference"]!.GetValue<string>());
            Assert.Equal("expected_context_mismatch", flow.Output["stop"]!["code"]!.GetValue<string>());
            Assert.Equal(beforeFlow + 1, routing.PostBodies.Count);
            Assert.Equal(otherActor, routing.TargetResponses.Last().Body!["actorId"]!.GetValue<string>());

            var tenantFlow = JsonNode.Parse(await File.ReadAllTextAsync(flowPath))!.AsObject();
            var tenantBindings = tenantFlow["modelBindings"]!.AsObject();
            var tenantFirst = tenantBindings["FlowAlpha"]!.DeepClone();
            var tenantSecond = tenantBindings["FlowZulu"]!.DeepClone();
            tenantBindings.Clear();
            tenantBindings["TenantAlpha"] = tenantFirst;
            tenantBindings["TenantZulu"] = tenantSecond;
            var tenantPath = Path.Combine(directory, "tenant-flow.json");
            await File.WriteAllTextAsync(tenantPath, tenantFlow.ToJsonString());
            var tenantDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tenantPath))).ToLowerInvariant();
            var tenantTokens = 0;
            auth = new FixtureAuthentication(() => Token(key, actor,
                Interlocked.Increment(ref tenantTokens) < 3 ? null : otherTenant));
            var beforeTenantFlow = routing.PostBodies.Count;
            var tenantFlowResult = await Run(["models", "byok", "import", "--model-bindings", tenantPath,
                "--file-sha256", tenantDigest, "--expected-context", actorOnePath, .. target]);
            Assert.Equal(1, tenantFlowResult.Exit);
            Assert.Single(tenantFlowResult.Output!["completed"]!.AsArray());
            Assert.Equal("TenantZulu", tenantFlowResult.Output["stop"]!["reference"]!.GetValue<string>());
            Assert.Equal("rejected", tenantFlowResult.Output["stop"]!["outcome"]!.GetValue<string>());
            Assert.Equal(beforeTenantFlow + 1, routing.PostBodies.Count);

            await authorityA.RevokeRoleAssignmentAsync(otherGrant.AssignmentId, otherGrant.Revision, "fixture");
            auth = new FixtureAuthentication(() => Token(key, otherActor));
            var beforeRevocation = routing.PostBodies.Count;
            var revoked = await Run(import);
            Assert.Equal(1, revoked.Exit);
            Assert.Equal("rejected", revoked.Output!["stop"]!["outcome"]!.GetValue<string>());
            Assert.Equal(beforeRevocation, routing.PostBodies.Count);
            Assert.Equal(403, routing.TargetResponses.Last().Status);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Token(SecurityKey key, string actor, string? tenantAssertion = null)
    {
        var claims = new List<Claim>
        {
            new("sub", actor), new("scope", "api.read projects.admin"), new("jti", Guid.NewGuid().ToString("N")),
        };
        if (tenantAssertion is not null) claims.Add(new Claim("tenant_id", tenantAssertion));
        var token = new JwtSecurityToken(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test", claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class FixtureAuthentication(Func<string> token) : ICliAuthentication
    {
        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            Assert.Equal(InteractionPolicy.Never, interaction);
            Assert.Null(identity.ProjectId);
            Assert.Null(identity.RunId);
            return Task.FromResult(token());
        }
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FailingOutput : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => throw new IOException("lost output");
    }

    private sealed record OwnerRequest(string Owner, string Path, string? Bearer, string? Tenant);
    private sealed record TargetResponse(
        string Owner, int Status, string? Bearer, string? Tenant, JsonObject? Body, bool NoStore);
    private sealed record PostRequest(string Owner, int Status, string? Bearer, string? Tenant);

    private sealed class GatewayRouter(HttpClient a, HttpClient b) : HttpMessageHandler
    {
        public bool ContextOnB { get; set; }
        public bool PostOnB { get; set; }
        public bool LoseNextResponse { get; set; }
        public List<string> PostBodies { get; } = [];
        public List<PostRequest> PostRequests { get; } = [];
        public List<TargetResponse> TargetResponses { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var bearer = request.Headers.Authorization?.Parameter;
            var tenant = request.Headers.TryGetValues("X-Agentweaver-Tenant", out var selected)
                ? selected.Single() : null;
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                copy.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (request.Method == HttpMethod.Post)
                    lock (PostBodies) PostBodies.Add(body);
            }
            var useB = request.Method == HttpMethod.Post ? PostOnB : ContextOnB;
            var owner = useB ? "B" : "A";
            var result = await (useB ? b : a).SendAsync(copy, ct);
            if (path == "/api/v1/authorization/target-context")
            {
                var body = result.IsSuccessStatusCode
                    ? JsonNode.Parse(await result.Content.ReadAsStringAsync(ct))!.AsObject() : null;
                lock (TargetResponses)
                    TargetResponses.Add(new TargetResponse(owner, (int)result.StatusCode, bearer, tenant,
                        body, result.Headers.CacheControl?.NoStore == true));
            }
            if (request.Method == HttpMethod.Post)
            {
                lock (PostRequests)
                    PostRequests.Add(new PostRequest(owner, (int)result.StatusCode, bearer, tenant));
            }
            if (request.Method == HttpMethod.Post && LoseNextResponse && result.IsSuccessStatusCode)
            {
                LoseNextResponse = false;
                result.Dispose();
                throw new HttpRequestException("Response lost after owner commit.");
            }
            return result;
        }
    }
}
