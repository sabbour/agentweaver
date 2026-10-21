using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>
/// Exercises marketplace route validation that completes before the caller is resolved from the database.
/// </summary>
public sealed class SkillMarketplaceEndpointValidationTests : IAsyncLifetime
{
    private const string ProjectId = "project-validation";
    private const string SourceId = "2d9af6df-9131-4d34-b30a-6c6d2fe9d5dd";
    private const string SourcesPath = "/api/projects/" + ProjectId + "/skill-marketplaces/sources";

    // The data source points at a closed local port; these routes must reject input before opening it.
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await SkillMarketplaceEndpointHost.StartAsync(
            _dataSource,
            _ => throw new InvalidOperationException("Validation tests must not call GitHub."),
            objectStore: null);
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task Unauthenticated_requests_are_challenged_before_the_route_runs()
    {
        using var response = await _client.GetAsync(SourcesPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("?includeRemoved=true&page=1")]
    [InlineData("?sort=name")]
    public async Task List_rejects_unsupported_query_parameters(string query)
    {
        using var response = await SendAsync(HttpMethod.Get, SourcesPath + query);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_marketplace_request");
    }

    [Fact]
    public async Task Update_maps_a_malformed_source_identifier_to_not_found()
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            SourcesPath + "/not-a-guid",
            new { expectedRevision = 1, repository = "contoso/skills" });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "marketplace_source_not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?expectedRevision=one")]
    [InlineData("?expectedRevision=1&expectedRevision=2")]
    [InlineData("?expectedRevision=1&force=true")]
    public async Task Delete_requires_exactly_one_numeric_expected_revision(string query)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"{SourcesPath}/{SourceId}{query}");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_marketplace_request");
    }

    [Theory]
    [InlineData("", "Expected source revision is required.")]
    [InlineData("?expectedSourceRevision=1&sort=name", "The browse query contains an unsupported or repeated parameter.")]
    [InlineData("?expectedSourceRevision=1&query=a&query=b", "The browse query contains an unsupported or repeated parameter.")]
    public async Task Browse_rejects_missing_unsupported_or_repeated_parameters(string query, string detail)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{SourcesPath}/{SourceId}/browse{query}");

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_marketplace_request");
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Browse_maps_a_malformed_source_identifier_to_not_found()
    {
        using var response = await SendAsync(HttpMethod.Get, SourcesPath + "/not-a-guid/browse?expectedSourceRevision=1");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "marketplace_source_not_found");
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("import")]
    public async Task Preview_and_import_reject_query_parameters(string operation)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{SourceId}/{operation}?selectedPath=skills/one",
            SkillMarketplaceEndpointHost.OperationBody(operation));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_marketplace_request");
        Assert.Equal("Query parameters are not supported for this operation.", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("import")]
    public async Task Preview_and_import_map_a_malformed_source_identifier_to_not_found(string operation)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/not-a-guid/{operation}",
            SkillMarketplaceEndpointHost.OperationBody(operation));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "marketplace_source_not_found");
    }

    [Fact]
    public async Task Definitions_reject_query_parameters()
    {
        using var response = await SendAsync(
            HttpMethod.Get, "/api/projects/" + ProjectId + "/skill-marketplaces/definitions?name=x");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_marketplace_request");
    }

    [Fact]
    public async Task Repeated_tenant_selectors_are_forbidden_before_membership_lookup()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SourcesPath);
        request.Headers.Add(SkillMarketplaceEndpointHost.SubjectHeader, "subject-1");
        request.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, ["tenant-a", "tenant-b"]);

        using var response = await _client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SkillMarketplaceEndpointHost.SubjectHeader, "subject-1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code) =>
        SkillMarketplaceEndpointHost.AssertProblemAsync(response, status, code);
}

/// <summary>
/// Drives marketplace source management, browse, preview, and import through HTTP against migrated PostgreSQL.
/// </summary>
public sealed class SkillMarketplaceEndpointPostgresTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>, IAsyncLifetime
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly byte[] SkillMarkdown = Encoding.UTF8.GetBytes(
        "---\nname: pinned-skill\ndescription: A controlled endpoint test skill.\n---\nFollow the approved instructions.");
    private static readonly byte[] SkillResource = Encoding.UTF8.GetBytes("Pinned reference bytes.");

    private readonly ConcurrentQueue<string> _gitHubRequests = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await SkillMarketplaceEndpointHost.StartAsync(
            fixture.DataSource,
            RespondFromGitHub,
            new InMemoryObjectStore());
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Owner_manages_a_source_through_create_list_update_and_remove()
    {
        var owner = await SeedOwnerAsync();
        var sourcesPath = SourcesPath(owner.ProjectId);

        using var created = await SendAsync(owner, HttpMethod.Post, sourcesPath, new
        {
            repository = "https://github.com/Contoso/Skills.git",
            requestedRef = "release/1.0",
            subpath = "/skills/",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertNoStore(created);
        var source = await ReadJsonAsync(created);
        var sourceId = source.GetProperty("sourceId").GetGuid();
        Assert.Equal($"{sourcesPath}/{sourceId:D}", created.Headers.Location?.OriginalString);
        Assert.Equal("skills", source.GetProperty("name").GetString());
        Assert.Equal("contoso/skills", source.GetProperty("repository").GetString());
        Assert.Equal("skills", source.GetProperty("subpath").GetString());
        Assert.Equal(1, source.GetProperty("revision").GetInt64());
        Assert.Equal("active", source.GetProperty("state").GetString());
        Assert.False(source.TryGetProperty("createdByActorId", out _));

        using var listed = await SendAsync(owner, HttpMethod.Get, sourcesPath);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        AssertNoStore(listed);
        Assert.Equal(sourceId, Assert.Single((await ReadJsonAsync(listed)).EnumerateArray()).GetProperty("sourceId").GetGuid());

        using var updated = await SendAsync(owner, HttpMethod.Put, $"{sourcesPath}/{sourceId:D}", new
        {
            expectedRevision = 1,
            name = "Team skills",
            repository = "contoso/skills",
            requestedRef = "main",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedView = await ReadJsonAsync(updated);
        Assert.Equal("Team skills", updatedView.GetProperty("name").GetString());
        Assert.Equal("main", updatedView.GetProperty("requestedRef").GetString());
        Assert.Equal(2, updatedView.GetProperty("revision").GetInt64());

        using var stale = await SendAsync(owner, HttpMethod.Put, $"{sourcesPath}/{sourceId:D}", new
        {
            expectedRevision = 1,
            repository = "contoso/skills",
        });
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            stale, HttpStatusCode.Conflict, "marketplace_source_revision_conflict");

        using var removed = await SendAsync(owner, HttpMethod.Delete, $"{sourcesPath}/{sourceId:D}?expectedRevision=2");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("removed", (await ReadJsonAsync(removed)).GetProperty("state").GetString());

        using var activeOnly = await SendAsync(owner, HttpMethod.Get, sourcesPath);
        Assert.Empty((await ReadJsonAsync(activeOnly)).EnumerateArray());
        using var withRemoved = await SendAsync(owner, HttpMethod.Get, sourcesPath + "?includeRemoved=true");
        var tombstone = Assert.Single((await ReadJsonAsync(withRemoved)).EnumerateArray());
        Assert.Equal("removed", tombstone.GetProperty("state").GetString());
        Assert.Equal(3, tombstone.GetProperty("revision").GetInt64());

        using var removedAgain = await SendAsync(owner, HttpMethod.Put, $"{sourcesPath}/{sourceId:D}", new
        {
            expectedRevision = 3,
            repository = "contoso/skills",
        });
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            removedAgain, HttpStatusCode.NotFound, "marketplace_source_not_found");
    }

    [Fact]
    public async Task Browse_preview_and_import_use_the_resolved_commit_and_record_the_receipt()
    {
        var owner = await SeedOwnerAsync();
        var sourcesPath = SourcesPath(owner.ProjectId);
        using var created = await SendAsync(owner, HttpMethod.Post, sourcesPath, new
        {
            name = "Controlled skills",
            repository = "contoso/skills",
            requestedRef = "release/1.0",
            subpath = "skills",
        });
        var sourceId = (await ReadJsonAsync(created)).GetProperty("sourceId").GetGuid();
        var sourcePath = $"{sourcesPath}/{sourceId:D}";

        using var browsed = await SendAsync(owner, HttpMethod.Get, sourcePath + "/browse?expectedSourceRevision=1&query=pinned&page=1&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, browsed.StatusCode);
        AssertNoStore(browsed);
        var page = await ReadJsonAsync(browsed);
        Assert.Equal(sourceId, page.GetProperty("sourceId").GetGuid());
        Assert.Equal(CommitSha, page.GetProperty("resolvedCommitSha").GetString());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(5, page.GetProperty("pageSize").GetInt32());
        var candidate = Assert.Single(page.GetProperty("candidates").EnumerateArray());
        Assert.Equal("skills/pinned", candidate.GetProperty("location").GetString());

        using var previewed = await SendAsync(owner, HttpMethod.Post, sourcePath + "/preview", new
        {
            expectedSourceRevision = 1,
            resolvedCommitSha = CommitSha,
            selectedPath = "skills/pinned",
        });
        Assert.Equal(HttpStatusCode.OK, previewed.StatusCode);
        var preview = await ReadJsonAsync(previewed);
        Assert.Equal("pinned-skill", preview.GetProperty("name").GetString());
        Assert.Equal("A controlled endpoint test skill.", preview.GetProperty("description").GetString());
        Assert.Equal(1, preview.GetProperty("resourceCount").GetInt32());
        // totalBytes counts semantic content (name, description, instructions, resources), not raw SKILL.md bytes.
        Assert.Equal(
            Encoding.UTF8.GetByteCount("pinned-skill" + "A controlled endpoint test skill." + "Follow the approved instructions.") +
                SkillResource.Length,
            preview.GetProperty("totalBytes").GetInt64());
        var digest = preview.GetProperty("contentDigest").GetString()!;

        using var imported = await SendAsync(
            owner,
            HttpMethod.Post,
            sourcePath + "/import",
            SkillMarketplaceEndpointHost.ImportBody(1, "skills/pinned", "endpoint-import-" + owner.ProjectId, digest));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        AssertNoStore(imported);
        var receipt = await ReadJsonAsync(imported);
        Assert.Equal(digest, receipt.GetProperty("contentDigest").GetString());
        Assert.Equal("pinned-skill", receipt.GetProperty("name").GetString());
        Assert.Equal(1, receipt.GetProperty("revision").GetInt64());

        using var wrongDigest = await SendAsync(
            owner,
            HttpMethod.Post,
            sourcePath + "/import",
            SkillMarketplaceEndpointHost.ImportBody(1, "skills/pinned", "endpoint-wrong-" + owner.ProjectId, new string('0', 64)));
        await SkillMarketplaceEndpointHost.AssertProblemAsync(wrongDigest, HttpStatusCode.Conflict, "skill_content_conflict");

        using var invalidSelection = await SendAsync(owner, HttpMethod.Post, sourcePath + "/preview", new
        {
            expectedSourceRevision = 1,
            resolvedCommitSha = CommitSha,
            selectedPath = "skills/missing",
        });
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            invalidSelection, HttpStatusCode.BadRequest, "invalid_marketplace_request");

        using var staleBrowse = await SendAsync(owner, HttpMethod.Get, sourcePath + "/browse?expectedSourceRevision=2");
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            staleBrowse, HttpStatusCode.Conflict, "marketplace_source_revision_conflict");

        Assert.All(_gitHubRequests.Where(uri => !uri.Contains("/commits/", StringComparison.Ordinal)),
            uri => Assert.Contains(CommitSha, uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callers_without_project_authority_are_denied()
    {
        var owner = await SeedOwnerAsync();
        var sourcesPath = SourcesPath(owner.ProjectId);
        var outsider = owner with { Subject = "outsider-" + Guid.NewGuid().ToString("N") };

        using var unknownActor = await SendAsync(outsider, HttpMethod.Get, sourcesPath);
        await SkillMarketplaceEndpointHost.AssertProblemAsync(unknownActor, HttpStatusCode.Forbidden, "forbidden");

        using var missingSource = await SendAsync(
            owner, HttpMethod.Post, $"{sourcesPath}/{Guid.NewGuid():D}/preview", new
            {
                expectedSourceRevision = 1,
                resolvedCommitSha = CommitSha,
                selectedPath = "skills/pinned",
            });
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            missingSource, HttpStatusCode.NotFound, "marketplace_source_not_found");

        using var malformedDelete = await SendAsync(owner, HttpMethod.Delete, sourcesPath + "/not-a-guid?expectedRevision=1");
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            malformedDelete, HttpStatusCode.NotFound, "marketplace_source_not_found");

        var readOnly = owner with { Scope = ProjectAuthorizationOwner.ApiReadScope };
        using var readOnlyCreate = await SendAsync(readOnly, HttpMethod.Post, sourcesPath, new { repository = "contoso/skills" });
        await SkillMarketplaceEndpointHost.AssertProblemAsync(readOnlyCreate, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Definitions_mark_the_added_source_and_removal_stops_browse_preview_and_import()
    {
        var owner = await SeedOwnerAsync();
        var definitionsPath = $"/api/projects/{owner.ProjectId}/skill-marketplaces/definitions";
        var sourcesPath = SourcesPath(owner.ProjectId);

        using var before = await SendAsync(owner, HttpMethod.Get, definitionsPath);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        AssertNoStore(before);
        var definitions = (await ReadJsonAsync(before)).EnumerateArray().ToArray();
        Assert.Equal(new[] { "Azure Skills", "GitHub Awesome Copilot" },
            definitions.Select(item => item.GetProperty("name").GetString()!).ToArray());
        Assert.All(definitions, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("sourceId").ValueKind));
        var awesome = definitions[1];

        // Add the suggested marketplace with the values the definition returned, as the UI does.
        using var created = await SendAsync(owner, HttpMethod.Post, sourcesPath, new
        {
            name = awesome.GetProperty("name").GetString(),
            repository = awesome.GetProperty("repository").GetString(),
            requestedRef = awesome.GetProperty("requestedRef").GetString(),
            subpath = awesome.GetProperty("subpath").GetString(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sourceId = (await ReadJsonAsync(created)).GetProperty("sourceId").GetGuid();

        using var after = await SendAsync(owner, HttpMethod.Get, definitionsPath);
        var marked = (await ReadJsonAsync(after)).EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, marked[0].GetProperty("sourceId").ValueKind);
        Assert.Equal(sourceId, marked[1].GetProperty("sourceId").GetGuid());

        var sourcePath = $"{sourcesPath}/{sourceId:D}";
        using var removed = await SendAsync(owner, HttpMethod.Delete, sourcePath + "?expectedRevision=1");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        // After removal, each content route must return 404 before it calls GitHub. The empty request
        // log below proves that no upstream call was made.
        using var browse = await SendAsync(owner, HttpMethod.Get, sourcePath + "/browse?expectedSourceRevision=1");
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            browse, HttpStatusCode.NotFound, "marketplace_source_not_found");
        using var preview = await SendAsync(owner, HttpMethod.Post, sourcePath + "/preview", new
        {
            expectedSourceRevision = 1,
            resolvedCommitSha = CommitSha,
            selectedPath = "skills/pinned",
        });
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            preview, HttpStatusCode.NotFound, "marketplace_source_not_found");
        using var import = await SendAsync(
            owner,
            HttpMethod.Post,
            sourcePath + "/import",
            SkillMarketplaceEndpointHost.ImportBody(1, "skills/pinned", "removed-" + owner.ProjectId, new string('0', 64)));
        await SkillMarketplaceEndpointHost.AssertProblemAsync(
            import, HttpStatusCode.NotFound, "marketplace_source_not_found");

        using var cleared = await SendAsync(owner, HttpMethod.Get, definitionsPath);
        Assert.All((await ReadJsonAsync(cleared)).EnumerateArray(),
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("sourceId").ValueKind));
        Assert.Empty(_gitHubRequests);

        // A caller with no project authority can't read the suggested list for that project.
        var outsider = owner with { Subject = "outsider-" + Guid.NewGuid().ToString("N") };
        using var denied = await SendAsync(outsider, HttpMethod.Get, definitionsPath);
        await SkillMarketplaceEndpointHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
    }

    private static string SourcesPath(string projectId) => $"/api/projects/{projectId}/skill-marketplaces/sources";

    private async Task<TestCaller> SeedOwnerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = suffix;
        var tenantId = "tenant-" + suffix;
        var subject = "owner-" + suffix;
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var membership = await authority.GrantMembershipAsync(
            SkillMarketplaceEndpointHost.Issuer, subject, tenantId, "fixture");
        await using (var db = new ProjectsConfigDbContext(options))
        {
            await db.Projects.AddAsync(new ProjectRecord
            {
                ProjectId = projectId,
                TenantId = tenantId,
                CreatedByActorId = subject,
                Name = "Endpoint project",
                State = ProjectLifecycleState.Active,
                Revision = 1,
                ConfigurationRevision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await authority.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Owner,
            "fixture");
        return new TestCaller(projectId, tenantId, subject, SkillMarketplaceEndpointHost.DefaultScope);
    }

    private async Task<HttpResponseMessage> SendAsync(
        TestCaller caller,
        HttpMethod method,
        string path,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SkillMarketplaceEndpointHost.SubjectHeader, caller.Subject);
        request.Headers.Add(SkillMarketplaceEndpointHost.ScopeHeader, caller.Scope);
        request.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, caller.TenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement.Clone();

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.True(response.Headers.CacheControl?.NoStore, "Marketplace responses must not be cached.");

    /// <summary>Serves a fixed GitHub commit, tree, and raw file set for the <c>contoso/skills</c> source.</summary>
    private HttpResponseMessage RespondFromGitHub(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        _gitHubRequests.Enqueue(uri.ToString());
        if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
            return Json($$"""{"sha":"{{CommitSha}}"}""");
        if (uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith($"/git/trees/{CommitSha}", StringComparison.Ordinal))
            return Json(
                $$"""{"truncated":false,"tree":[{"path":"skills/pinned/SKILL.md","type":"blob","mode":"100644","size":{{SkillMarkdown.Length}}},{"path":"skills/pinned/docs/reference.md","type":"blob","mode":"100644","size":{{SkillResource.Length}}}]}""");
        if (uri.Host == "raw.githubusercontent.com" &&
            uri.AbsolutePath.EndsWith($"/{CommitSha}/skills/pinned/SKILL.md", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SkillMarkdown) };
        if (uri.Host == "raw.githubusercontent.com" &&
            uri.AbsolutePath.EndsWith($"/{CommitSha}/skills/pinned/docs/reference.md", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SkillResource) };
        throw new InvalidOperationException($"Unexpected marketplace request: {uri}");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record TestCaller(string ProjectId, string TenantId, string Subject, string Scope);

    /// <summary>Immutable in-memory object store so marketplace imports can persist skill bundles.</summary>
    private sealed class InMemoryObjectStore : IObjectStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            if (!_objects.TryAdd(key.Value, buffer.ToArray()))
                throw new InvalidOperationException("The immutable test object already exists.");
            return Task.CompletedTask;
        }

        public Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ObjectRead?>(_objects.TryGetValue(key.Value, out var bytes)
                ? new ObjectRead(new MemoryStream(bytes, writable: false), bytes.LongLength, static () => { })
                : null);

        public Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_objects.TryRemove(key.Value, out _));
    }
}

/// <summary>
/// Hosts <see cref="SkillMarketplaceEndpoints"/> on a test server with the production service graph,
/// header-based test authentication, and a stubbed GitHub transport.
/// </summary>
internal static class SkillMarketplaceEndpointHost
{
    public const string Issuer = "https://marketplace-endpoints.test/";
    public const string SubjectHeader = "X-Test-Subject";
    public const string ScopeHeader = "X-Test-Scope";
    public const string DefaultScope = "api.read projects.admin";
    private const string Scheme = "TestHeaders";

    public static async Task<IHost> StartAsync(
        NpgsqlDataSource dataSource,
        Func<HttpRequestMessage, HttpResponseMessage> respondFromGitHub,
        IObjectStore? objectStore)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(Scheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(Scheme, _ => { });
                    services.AddAuthorization();
                    services.AddDbContext<ProjectsConfigDbContext>(options => options.UseNpgsql(
                        dataSource,
                        npgsql => npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
                    services.AddSingleton(CreateProviderCatalog());
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ProjectsConfigIdentityOptions(Issuer));
                    services.AddScoped<ProjectAuthorizationOwner>();
                    services.AddScoped<ProjectsConfigService>();
                    services.AddScoped<MarketplaceSourceStore>();
                    services.AddScoped<IProjectMarketplaceSourceStore>(
                        provider => provider.GetRequiredService<MarketplaceSourceStore>());
                    services.AddScoped<ProjectMarketplaceSourceService>();
                    services.AddSingleton(new SkillMarketplaceCatalog(SkillMarketplaceCatalog.DefaultDefinitions));
                    services.AddScoped<ProjectMarketplaceSkillContentService>();
                    services.AddScoped(provider => new SkillMarketplaceBrowseService(
                        new HttpClient(new StubGitHubHandler(respondFromGitHub)),
                        provider.GetRequiredService<IProjectMarketplaceSourceStore>()));
                    services.AddScoped<ISkillContentService>(provider => new SkillContentService(
                        provider.GetRequiredService<ProjectsConfigDbContext>(),
                        objectStore is null ? null : new SkillContentObjectStore(objectStore),
                        provider.GetRequiredService<ProjectsConfigService>(),
                        provider.GetRequiredService<TimeProvider>()));
                    services.ConfigureHttpJsonOptions(options =>
                    {
                        options.SerializerOptions.UnmappedMemberHandling =
                            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
                        options.SerializerOptions.Converters.Add(
                            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSkillMarketplaceEndpoints());
                });
            })
            .StartAsync();
    }

    /// <summary>Builds a strict preview or import body for the selected operation.</summary>
    public static object OperationBody(string operation) => operation == "preview"
        ? new { expectedSourceRevision = 1, resolvedCommitSha = "0123456789abcdef0123456789abcdef01234567", selectedPath = "skills/one" }
        : ImportBody(1, "skills/one", "key-1", new string('0', 64));

    public static object ImportBody(long sourceRevision, string selectedPath, string idempotencyKey, string digest) => new
    {
        expectedSourceRevision = sourceRevision,
        resolvedCommitSha = "0123456789abcdef0123456789abcdef01234567",
        selectedPath,
        idempotencyKey,
        expectedContentDigest = digest,
    };

    /// <summary>Asserts a problem response with the stable <c>code</c> extension and the no-store cache policy.</summary>
    public static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} but got {(int)response.StatusCode}: {body}");
        Assert.True(response.Headers.CacheControl?.NoStore, "Marketplace problems must not be cached.");
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(code, problem.GetProperty("title").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }

    private static ProviderCatalog CreateProviderCatalog()
    {
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                "marketplace-test",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.KubernetesController,
                ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")),
            true,
            "marketplace-options-v1",
            1);
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, "marketplace-test")],
            []).Value);
    }

    /// <summary>Authenticates a request from test headers; requests without a subject header stay anonymous.</summary>
    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers[SubjectHeader].ToString();
            if (string.IsNullOrEmpty(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var scope = Request.Headers[ScopeHeader].ToString();
            var identity = new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("scope", string.IsNullOrEmpty(scope) ? DefaultScope : scope),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class StubGitHubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
