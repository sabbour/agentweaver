using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class SkillMarketplaceBrowseServiceTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Browse_filters_before_pagination_and_fetches_only_page_manifests_at_resolved_commit()
    {
        var requests = new ConcurrentQueue<string>();
        using var client = CreateClient(requests, request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                return JsonResponse($$"""{"sha":"{{CommitSha}}"}""");
            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse(
                    """
                    {
                      "truncated": false,
                      "tree": [
                        { "path": "skills/azure-storage/SKILL.md", "type": "blob" },
                        { "path": "skills/azure-storage/reference.md", "type": "blob" },
                        { "path": "skills/github-actions/SKILL.md", "type": "blob" },
                        { "path": "skills/azure-openai/SKILL.md", "type": "blob" }
                      ]
                    }
                    """);
            if (uri.Host == "raw.githubusercontent.com")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("---\ndescription: \"Azure OpenAI skill.\"\n---\n")
                };
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });
        var source = CreateSource();
        var store = new FakeSourceStore(source);
        var service = new SkillMarketplaceBrowseService(client, store);

        var page = await service.BrowseAsync(
            source.ProjectId,
            source,
            new MarketplaceBrowseRequest
            {
                ExpectedSourceRevision = source.Revision,
                Query = "azure",
                Page = 1,
                PageSize = 1,
            },
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(source.SourceId, page.SourceId);
        Assert.Equal(source.Revision, page.SourceRevision);
        Assert.Equal(source.RequestedRef, page.RequestedRef);
        Assert.Equal(CommitSha, page.ResolvedCommitSha);
        Assert.Equal(2, page.Total);
        Assert.True(page.HasMore);
        Assert.Equal("skills/azure-openai", Assert.Single(page.Candidates).Location);
        Assert.Equal("Azure OpenAI skill.", page.Candidates[0].Description);
        var rawRequests = requests.Where(value => value.Contains("raw.githubusercontent.com", StringComparison.Ordinal)).ToArray();
        Assert.Single(rawRequests);
        Assert.Contains($"{CommitSha}/skills/azure-openai/SKILL.md", rawRequests[0], StringComparison.Ordinal);
        Assert.DoesNotContain("reference.md", string.Join('\n', requests), StringComparison.Ordinal);
        Assert.DoesNotContain("github-actions", string.Join('\n', requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browse_indexes_a_root_skill_manifest()
    {
        var source = CreateSource() with { Subpath = null };
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                return JsonResponse($$"""{"sha":"{{CommitSha}}"}""");
            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse("""{"truncated":false,"tree":[{"path":"SKILL.md","type":"blob"}]}""");
            if (uri.Host == "raw.githubusercontent.com")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("---\ndescription: root skill\n---\n")
                };
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var page = await service.BrowseAsync(
            source.ProjectId,
            source,
            new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
            _ => Task.CompletedTask,
            CancellationToken.None);

        var candidate = Assert.Single(page.Candidates);
        Assert.Equal(string.Empty, candidate.Location);
        Assert.Equal("root", candidate.Name);
        Assert.Equal("root skill", candidate.Description);
    }

    [Fact]
    public async Task Read_selected_skill_files_uses_exact_commit_and_excludes_nested_skill_trees()
    {
        var requests = new ConcurrentQueue<string>();
        var markdown = Encoding.UTF8.GetBytes("---\nname: one\n---\nInstructions");
        var resource = Encoding.UTF8.GetBytes("reference bytes");
        var entries = new[]
        {
            new { path = "skills/one/SKILL.md", type = "blob", mode = "100644", size = (long)markdown.Length },
            new { path = "skills/one/references/api.md", type = "blob", mode = "100644", size = (long)resource.Length },
            new { path = "skills/one/nested/SKILL.md", type = "blob", mode = "100644", size = 10L },
            new { path = "skills/one/nested/extra.txt", type = "blob", mode = "100644", size = 5L },
            new { path = "skills/two/SKILL.md", type = "blob", mode = "100644", size = 10L },
        };
        using var client = CreateClient(requests, request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse(JsonSerializer.Serialize(new { truncated = false, tree = entries }));
            if (uri.Host == "raw.githubusercontent.com" &&
                uri.AbsolutePath.EndsWith($"/{CommitSha}/skills/one/SKILL.md", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(markdown) };
            if (uri.Host == "raw.githubusercontent.com" &&
                uri.AbsolutePath.EndsWith($"/{CommitSha}/skills/one/references/api.md", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(resource) };
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));
        var authorityChecks = 0;

        var result = await service.ReadSelectedSkillFilesAsync(
            source.ProjectId,
            source,
            source.Revision,
            CommitSha,
            "skills/one",
            _ =>
            {
                authorityChecks++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(markdown, result.SkillMarkdown.ToArray());
        var importedResource = Assert.Single(result.Resources);
        Assert.Equal("references/api.md", importedResource.RelativePath);
        Assert.Equal(resource, importedResource.Content.ToArray());
        Assert.Equal(3, authorityChecks);
        Assert.DoesNotContain(requests, request => request.Contains("/commits/", StringComparison.Ordinal));
        Assert.All(requests, request => Assert.Contains(CommitSha, request, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_selected_root_skill_files_reads_root_manifest()
    {
        var requests = new ConcurrentQueue<string>();
        var markdown = Encoding.UTF8.GetBytes("---\nname: root\n---\nInstructions");
        using var client = CreateClient(requests, request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse(JsonTree(
                    [new TestTreeEntry("SKILL.md", "blob", "100644", markdown.Length)]));
            if (uri.Host == "raw.githubusercontent.com" &&
                uri.AbsolutePath.EndsWith($"/{CommitSha}/SKILL.md", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(markdown) };
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });
        var source = CreateSource() with { Subpath = null };
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var result = await service.ReadSelectedSkillFilesAsync(
            source.ProjectId,
            source,
            source.Revision,
            CommitSha,
            string.Empty,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(markdown, result.SkillMarkdown.ToArray());
        Assert.Empty(result.Resources);
        Assert.Contains(requests, request => request.EndsWith($"/{CommitSha}/SKILL.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_selected_skill_files_rejects_resource_count_before_downloading_content()
    {
        var requests = new ConcurrentQueue<string>();
        var entries = Enumerable.Range(0, 65)
            .Select(index => new
            {
                path = $"skills/one/resources/file-{index:D2}.txt",
                type = "blob",
                mode = "100644",
                size = 0L,
            })
            .Prepend(new
            {
                path = "skills/one/SKILL.md",
                type = "blob",
                mode = "100644",
                size = 0L,
            })
            .ToArray();
        using var client = CreateClient(requests, request =>
            request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal)
                ? JsonResponse(JsonSerializer.Serialize(new { truncated = false, tree = entries }))
                : throw new InvalidOperationException("Oversized resource inventory must fail before raw downloads."));
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.ReadSelectedSkillFilesAsync(
                source.ProjectId,
                source,
                source.Revision,
                CommitSha,
                "skills/one",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_too_large", exception.Code);
        Assert.DoesNotContain(requests, request => request.Contains("raw.githubusercontent.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CommitSha, "outside/one")]
    [InlineData("not-a-full-commit", "skills/one")]
    public async Task Read_selected_skill_files_rejects_invalid_selection_before_upstream_reads(
        string resolvedCommitSha,
        string selectedPath)
    {
        var requests = new ConcurrentQueue<string>();
        using var client = CreateClient(requests, _ =>
            throw new InvalidOperationException("Invalid source selections must fail before GitHub reads."));
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.ReadSelectedSkillFilesAsync(
                source.ProjectId,
                source,
                source.Revision,
                resolvedCommitSha,
                selectedPath,
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("invalid_marketplace_request", exception.Code);
        Assert.Equal((int)HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Empty(requests);
    }

    [Theory]
    [InlineData("skills/one/SKILL.md", 512 * 1024 + 1)]
    [InlineData("skills/one/resources/large.bin", 256 * 1024 + 1)]
    public async Task Read_selected_skill_files_rejects_oversized_files_before_raw_downloads(
        string oversizedPath,
        long oversizedBytes)
    {
        var requests = new ConcurrentQueue<string>();
        var manifestBytes = string.Equals(oversizedPath, "skills/one/SKILL.md", StringComparison.Ordinal)
            ? oversizedBytes
            : 0;
        var entries = new List<TestTreeEntry>
        {
            new("skills/one/SKILL.md", "blob", "100644", manifestBytes),
        };
        if (!string.Equals(oversizedPath, "skills/one/SKILL.md", StringComparison.Ordinal))
            entries.Add(new TestTreeEntry(oversizedPath, "blob", "100644", oversizedBytes));
        using var client = CreateClient(requests, request =>
            request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal)
                ? JsonResponse(JsonTree(entries))
                : throw new InvalidOperationException("Oversized file must fail before raw downloads."));
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.ReadSelectedSkillFilesAsync(
                source.ProjectId,
                source,
                source.Revision,
                CommitSha,
                "skills/one",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_too_large", exception.Code);
        Assert.DoesNotContain(requests, request => request.Contains("raw.githubusercontent.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_selected_skill_files_rejects_symbolic_link_resources_before_raw_downloads()
    {
        var requests = new ConcurrentQueue<string>();
        var entries = new[]
        {
            new TestTreeEntry("skills/one/SKILL.md", "blob", "100644", 0),
            new TestTreeEntry("skills/one/resources/link", "blob", "120000", 1),
        };
        using var client = CreateClient(requests, request =>
            request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal)
                ? JsonResponse(JsonTree(entries))
                : throw new InvalidOperationException("Symlink resources must fail before raw downloads."));
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.ReadSelectedSkillFilesAsync(
                source.ProjectId,
                source,
                source.Revision,
                CommitSha,
                "skills/one",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("invalid_marketplace_response", exception.Code);
        Assert.DoesNotContain(requests, request => request.Contains("raw.githubusercontent.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_selected_skill_files_rejects_aggregate_resource_bytes_before_raw_downloads()
    {
        var requests = new ConcurrentQueue<string>();
        var entries = new List<TestTreeEntry>
        {
            new("skills/one/SKILL.md", "blob", "100644", 0),
        };
        entries.AddRange(Enumerable.Range(0, 5).Select(index =>
            new TestTreeEntry(
                $"skills/one/resources/file-{index}.bin",
                "blob",
                "100644",
                256 * 1024)));
        using var client = CreateClient(requests, request =>
            request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal)
                ? JsonResponse(JsonTree(entries))
                : throw new InvalidOperationException("Oversized resource aggregate must fail before raw downloads."));
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.ReadSelectedSkillFilesAsync(
                source.ProjectId,
                source,
                source.Revision,
                CommitSha,
                "skills/one",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_too_large", exception.Code);
        Assert.DoesNotContain(requests, request => request.Contains("raw.githubusercontent.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_selected_skill_files_rejects_source_change_during_raw_fetch()
    {
        var requests = new ConcurrentQueue<string>();
        var markdown = Encoding.UTF8.GetBytes("---\nname: one\n---\nInstructions");
        var entries = new[]
        {
            new { path = "skills/one/SKILL.md", type = "blob", mode = "100644", size = (long)markdown.Length },
        };
        var source = CreateSource();
        var store = new FakeSourceStore(source);
        using var client = CreateClient(requests, request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse(JsonSerializer.Serialize(new { truncated = false, tree = entries }));
            store.Current = source with { Revision = source.Revision + 1 };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(markdown) };
        });
        var service = new SkillMarketplaceBrowseService(client, store);

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.ReadSelectedSkillFilesAsync(
                source.ProjectId,
                source,
                source.Revision,
                CommitSha,
                "skills/one",
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_revision_conflict", exception.Code);
    }

    [Fact]
    public async Task Browse_rejects_a_stale_source_revision_before_upstream_reads()
    {
        using var client = CreateClient(new ConcurrentQueue<string>(), _ =>
            throw new InvalidOperationException("A stale source must not reach GitHub."));
        var source = CreateSource();
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision - 1 },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_revision_conflict", exception.Code);
        Assert.Equal((int)HttpStatusCode.Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task Browse_rejects_source_change_during_upstream_reads()
    {
        var source = CreateSource();
        var store = new FakeSourceStore(source);
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                return JsonResponse($$"""{"sha":"{{CommitSha}}"}""");
            if (request.RequestUri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse("""{"truncated":false,"tree":[{"path":"skills/one/SKILL.md","type":"blob"}]}""");
            store.Current = source with { Revision = source.Revision + 1 };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("---\ndescription: one\n---\n")
            };
        });
        var service = new SkillMarketplaceBrowseService(client, store);

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_revision_conflict", exception.Code);
    }

    [Fact]
    public async Task Browse_rechecks_current_actor_authority_before_returning_results()
    {
        var calls = 0;
        var source = CreateSource();
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                return JsonResponse($$"""{"sha":"{{CommitSha}}"}""");
            if (request.RequestUri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse("""{"truncated":false,"tree":[{"path":"skills/one/SKILL.md","type":"blob"}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("---\n---\n") };
        });
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
                _ =>
                {
                    calls++;
                    return calls < 3
                        ? Task.CompletedTask
                        : Task.FromException(MarketplaceSourceException.Forbidden());
                },
                CancellationToken.None));

        Assert.Equal("forbidden", exception.Code);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Browse_rechecks_actor_authority_after_a_blocking_source_read()
    {
        var source = CreateSource();
        var store = new FakeSourceStore(source);
        var getStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueGet = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeGetAsync = async cancellationToken =>
        {
            getStarted.TrySetResult(true);
            await continueGet.Task.WaitAsync(cancellationToken);
        };
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                return JsonResponse($$"""{"sha":"{{CommitSha}}"}""");
            if (uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse("""{"truncated":false,"tree":[{"path":"skills/one/SKILL.md","type":"blob"}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("---\n---\n") };
        });
        var service = new SkillMarketplaceBrowseService(client, store);
        var authorityRevoked = false;
        var calls = 0;
        var browseTask = service.BrowseAsync(
            source.ProjectId,
            source,
            new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
            _ =>
            {
                calls++;
                return authorityRevoked
                    ? Task.FromException(MarketplaceSourceException.Forbidden())
                    : Task.CompletedTask;
            },
            CancellationToken.None);

        await getStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        authorityRevoked = true;
        continueGet.TrySetResult(true);
        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() => browseTask);

        Assert.Equal("forbidden", exception.Code);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Read_selected_skill_files_rechecks_actor_authority_after_a_blocking_source_read()
    {
        var source = CreateSource();
        var markdown = Encoding.UTF8.GetBytes("---\nname: one\n---\nInstructions");
        var store = new FakeSourceStore(source);
        var getStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueGet = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeGetAsync = async cancellationToken =>
        {
            getStarted.TrySetResult(true);
            await continueGet.Task.WaitAsync(cancellationToken);
        };
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
                return JsonResponse(JsonTree(
                    [new TestTreeEntry("skills/one/SKILL.md", "blob", "100644", markdown.Length)]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(markdown) };
        });
        var service = new SkillMarketplaceBrowseService(client, store);
        var authorityRevoked = false;
        var calls = 0;
        var readTask = service.ReadSelectedSkillFilesAsync(
            source.ProjectId,
            source,
            source.Revision,
            CommitSha,
            "skills/one",
            _ =>
            {
                calls++;
                return authorityRevoked
                    ? Task.FromException(MarketplaceSourceException.Forbidden())
                    : Task.CompletedTask;
            },
            CancellationToken.None);

        await getStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        authorityRevoked = true;
        continueGet.TrySetResult(true);
        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() => readTask);

        Assert.Equal("forbidden", exception.Code);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("""{"truncated":true,"tree":[]}""", "marketplace_source_too_large")]
    [InlineData("not-json", "invalid_marketplace_response")]
    public async Task Browse_surfaces_truncated_or_malformed_index_responses(
        string treeBody,
        string expectedCode)
    {
        var source = CreateSource();
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
            request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                ? JsonResponse($$"""{"sha":"{{CommitSha}}"}""")
                : JsonResponse(treeBody));
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Browse_maps_upstream_timeout_and_unavailability_to_truthful_errors()
    {
        var source = CreateSource();
        using var timedOutClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var timeoutService = new SkillMarketplaceBrowseService(
            timedOutClient,
            new FakeSourceStore(source),
            TimeSpan.FromMilliseconds(20));

        var timeout = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            timeoutService.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_timeout", timeout.Code);

        using var unavailableClient = CreateClient(new ConcurrentQueue<string>(), _ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var unavailableService = new SkillMarketplaceBrowseService(
            unavailableClient,
            new FakeSourceStore(source));

        var unavailable = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            unavailableService.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("marketplace_source_unavailable", unavailable.Code);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task Browse_rejects_paths_with_traversal_segments()
    {
        var source = CreateSource();
        using var client = CreateClient(new ConcurrentQueue<string>(), request =>
            request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                ? JsonResponse($$"""{"sha":"{{CommitSha}}"}""")
                : JsonResponse("""{"truncated":false,"tree":[{"path":"skills/../SKILL.md","type":"blob"}]}"""));
        var service = new SkillMarketplaceBrowseService(client, new FakeSourceStore(source));

        var exception = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.BrowseAsync(
                source.ProjectId,
                source,
                new MarketplaceBrowseRequest { ExpectedSourceRevision = source.Revision },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("invalid_marketplace_response", exception.Code);
    }

    private static ProjectMarketplaceSourceRecord CreateSource() =>
        new()
        {
            ProjectId = "project-1",
            SourceId = Guid.Parse("2d9af6df-9131-4d34-b30a-6c6d2fe9d5dd"),
            Name = "Community skills",
            NormalizedName = "COMMUNITY SKILLS",
            Repository = "contoso/skills",
            RequestedRef = "release/1.0",
            Subpath = "skills",
            Revision = 7,
            State = ProjectMarketplaceSourceState.Active,
            CreatedByActorId = "actor-1",
            UpdatedByActorId = "actor-1",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static HttpClient CreateClient(
        ConcurrentQueue<string> requests,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        return new HttpClient(new DelegateHandler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }));
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static string JsonTree(IEnumerable<TestTreeEntry> entries) =>
        JsonSerializer.Serialize(new { truncated = false, tree = entries });

    private sealed record TestTreeEntry(string Path, string Type, string Mode, long Size);

    private sealed class FakeSourceStore(ProjectMarketplaceSourceRecord source) :
        IProjectMarketplaceSourceStore
    {
        public ProjectMarketplaceSourceRecord Current { get; set; } = source;
        public Func<CancellationToken, Task>? BeforeGetAsync { get; set; }

        public Task<IReadOnlyList<ProjectMarketplaceSourceRecord>> ListByProjectAsync(
            string projectId,
            bool includeRemoved,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ProjectMarketplaceSourceRecord> result =
                Current.ProjectId == projectId &&
                (includeRemoved || Current.State == ProjectMarketplaceSourceState.Active)
                    ? [Current]
                    : [];
            return Task.FromResult(result);
        }

        public async Task<ProjectMarketplaceSourceRecord?> GetAsync(
            string projectId,
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            if (BeforeGetAsync is not null)
                await BeforeGetAsync(cancellationToken);
            var result = Current.ProjectId == projectId && Current.SourceId == sourceId
                ? Current
                : null;
            return result;
        }

        public Task<ProjectMarketplaceSourceRecord> CreateAsync(
            ProjectMarketplaceSourceRecord marketplaceSource,
            CancellationToken cancellationToken)
        {
            Current = marketplaceSource;
            return Task.FromResult(marketplaceSource);
        }

        public Task<ProjectMarketplaceSourceRecord> UpdateAsync(
            string projectId,
            Guid sourceId,
            long expectedRevision,
            ProjectMarketplaceSourceDefinition definition,
            string actorId,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            Current = Current with
            {
                Name = definition.Name,
                NormalizedName = MarketplaceSourceDefinition.NormalizeName(definition.Name),
                Repository = definition.Repository,
                RequestedRef = definition.RequestedRef,
                Subpath = definition.Subpath,
                Revision = expectedRevision + 1,
                UpdatedByActorId = actorId,
                UpdatedAt = updatedAt,
            };
            return Task.FromResult(Current);
        }

        public Task<ProjectMarketplaceSourceRecord> RemoveAsync(
            string projectId,
            Guid sourceId,
            long expectedRevision,
            string actorId,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            Current = Current with
            {
                State = ProjectMarketplaceSourceState.Removed,
                Revision = expectedRevision + 1,
                UpdatedByActorId = actorId,
                UpdatedAt = updatedAt,
            };
            return Task.FromResult(Current);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
