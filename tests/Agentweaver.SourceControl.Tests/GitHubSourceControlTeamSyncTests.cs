using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed partial class GitHubSourceControlAdapterTests
{
    [Fact]
    public async Task TeamSyncReadReturnsOnlyVerifiedSquadBlobsAtTheRequestedRef()
    {
        // The adapter must ignore repository files outside .squad and verify returned blob bytes.
        var content = Encoding.UTF8.GetBytes("team");
        var blobSha = GitBlobSha(content);
        var calls = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            if (path == "/repos/octo/widget/commits/main")
                return Task.FromResult(CommitJson(HeadSha, BaseSha));
            if (path == $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1")
                return Task.FromResult(TreeJson(
                    new { path = "README.md", mode = "100644", type = "blob", sha = BaseSha, size = 1 },
                    new { path = ".squad/team.md", mode = "100644", type = "blob", sha = blobSha, size = 4 }));
            if (path == $"/repos/octo/widget/git/blobs/{blobSha}")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(
                    new { encoding = "base64", content = Convert.ToBase64String(content) })));
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        var credential = NewCredential();
        var adapter = CreateAdapter(handler);

        var snapshot = await adapter.ReadTeamSyncTreeAsync(
            new SourceControlRepositoryIdentity("octo", "widget"),
            "main",
            credential,
            CancellationToken.None);

        var file = Assert.Single(snapshot.Files);
        Assert.Equal("octo/widget", snapshot.Repository);
        Assert.Equal("main", snapshot.Reference);
        Assert.Equal(HeadSha, snapshot.CommitSha);
        Assert.Equal(".squad/team.md", file.Path);
        Assert.Equal(blobSha, file.BlobSha);
        Assert.Equal(content, file.Content);
        Assert.Equal(
            ["/repos/octo/widget/commits/main",
             $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1",
             $"/repos/octo/widget/git/blobs/{blobSha}"],
            calls);
        Assert.DoesNotContain("transient-token", credential.ToString());
    }

    [Fact]
    public async Task TeamSyncCommitWritesOnlySquadPathAndUpdatesBranchWithoutForce()
    {
        // A successful commit creates immutable Git objects, then performs one non-forced ref update.
        var original = Encoding.UTF8.GetBytes("old");
        var changed = Encoding.UTF8.GetBytes("new");
        var originalSha = GitBlobSha(original);
        var changedSha = GitBlobSha(changed);
        const string newTreeSha = "cccccccccccccccccccccccccccccccccccccccc";
        const string newCommitSha = "dddddddddddddddddddddddddddddddddddddddd";
        var calls = new List<string>();
        var handler = new StubHandler(async (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add($"{request.Method.Method} {path}");
            if (path == "/repos/octo/widget/commits/main")
                return CommitJson(HeadSha, BaseSha);
            if (path == $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1")
                return TreeJson(
                    new { path = ".squad/team.md", mode = "100644", type = "blob", sha = originalSha, size = 3 });
            if (path == "/repos/octo/widget/git/blobs" && request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(Convert.ToBase64String(changed), body.RootElement.GetProperty("content").GetString());
                Assert.Equal("base64", body.RootElement.GetProperty("encoding").GetString());
                return ShaJson(changedSha);
            }
            if (path == "/repos/octo/widget/git/trees" && request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(BaseSha, body.RootElement.GetProperty("base_tree").GetString());
                var entry = Assert.Single(body.RootElement.GetProperty("tree").EnumerateArray());
                Assert.Equal(".squad/team.md", entry.GetProperty("path").GetString());
                Assert.Equal(changedSha, entry.GetProperty("sha").GetString());
                return ShaJson(newTreeSha);
            }
            if (path == "/repos/octo/widget/git/commits" && request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("Sync team files", body.RootElement.GetProperty("message").GetString());
                Assert.Equal(newTreeSha, body.RootElement.GetProperty("tree").GetString());
                Assert.Equal([HeadSha], body.RootElement.GetProperty("parents").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray());
                return ShaJson(newCommitSha);
            }
            if (path == "/repos/octo/widget/git/refs/heads/main" && request.Method == HttpMethod.Patch)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(newCommitSha, body.RootElement.GetProperty("sha").GetString());
                Assert.False(body.RootElement.GetProperty("force").GetBoolean());
                return JsonResponse($"{{\"object\":{{\"sha\":\"{newCommitSha}\"}}}}");
            }
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        var adapter = CreateAdapter(handler);

        var commit = await adapter.CommitTeamSyncChangesAsync(
            new SourceControlRepositoryIdentity("octo", "widget"),
            NewCredential(),
            new SourceControlTeamSyncCommitRequest(
                "main",
                HeadSha,
                "Sync team files",
                [new SourceControlTeamSyncChange(".squad/team.md", originalSha, changed)]),
            CancellationToken.None);

        Assert.Equal(newCommitSha, commit.CommitSha);
        Assert.Equal("main", commit.Branch);
        Assert.Equal(new Uri($"https://github.com/octo/widget/commit/{newCommitSha}"), commit.HtmlUrl);
        Assert.Equal(6, calls.Count);
        Assert.Equal("POST /repos/octo/widget/git/blobs", calls[2]);
        Assert.Equal("PATCH /repos/octo/widget/git/refs/heads/main", calls[^1]);
    }

    [Fact]
    public async Task TeamSyncRejectsStaleBaseBeforeAnyGitHubWrite()
    {
        // A changed branch head must stop before any POST or PATCH request.
        var writes = 0;
        var handler = new StubHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Get)
                writes++;
            return Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/main" => CommitJson(HeadSha, BaseSha),
                $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1" =>
                    JsonResponse("""{"truncated":false,"tree":[]}"""),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });

        var error = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            CreateAdapter(handler).CommitTeamSyncChangesAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                NewCredential(),
                new SourceControlTeamSyncCommitRequest(
                    "main",
                    BaseSha,
                    "Sync team files",
                    [new SourceControlTeamSyncChange(".squad/team.md", null, Encoding.UTF8.GetBytes("team"))]),
                CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.Conflict, error.Code);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task TeamSyncRejectsStaleBlobBeforeAnyGitHubWrite()
    {
        // A changed file blob must be rejected even when the branch head still matches.
        var currentSha = GitBlobSha(Encoding.UTF8.GetBytes("current"));
        var writes = 0;
        var handler = new StubHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Get)
                writes++;
            return Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/main" => CommitJson(HeadSha, BaseSha),
                $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1" =>
                    TreeJson(new { path = ".squad/team.md", mode = "100644", type = "blob", sha = currentSha, size = 7 }),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });

        var error = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            CreateAdapter(handler).CommitTeamSyncChangesAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                NewCredential(),
                new SourceControlTeamSyncCommitRequest(
                    "main",
                    HeadSha,
                    "Sync team files",
                    [new SourceControlTeamSyncChange(
                        ".squad/team.md",
                        GitBlobSha(Encoding.UTF8.GetBytes("preview")),
                        Encoding.UTF8.GetBytes("updated"))]),
                CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.Conflict, error.Code);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task TeamSyncRejectsTraversalAndSymlinksBeforeWriting()
    {
        // Invalid paths and remote symlinks must never reach a GitHub write endpoint.
        var calls = 0;
        var adapter = CreateAdapter(new StubHandler((request, _) =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/main" => CommitJson(HeadSha, BaseSha),
                $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1" =>
                    TreeJson(new { path = ".squad/link", mode = "120000", type = "blob", sha = HeadSha, size = 4 }),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        }));

        var traversal = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            adapter.CommitTeamSyncChangesAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                NewCredential(),
                new SourceControlTeamSyncCommitRequest(
                    "main",
                    HeadSha,
                    "Sync team files",
                    [new SourceControlTeamSyncChange("../outside", null, Encoding.UTF8.GetBytes("x"))]),
                CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.InvalidRequest, traversal.Code);
        Assert.Equal(0, calls);

        var symlink = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            adapter.ReadTeamSyncTreeAsync(
                new SourceControlRepositoryIdentity("octo", "widget"), "main", NewCredential(), default));
        Assert.Equal(SourceControlFailureCode.InvalidBinding, symlink.Code);
        Assert.Equal(2, calls);

        var rootSymlinkAdapter = CreateAdapter(new StubHandler((request, _) =>
            Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/main" => CommitJson(HeadSha, BaseSha),
                $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1" =>
                    TreeJson(new { path = ".squad", mode = "120000", type = "blob", sha = HeadSha, size = 4 }),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            })));
        var rootSymlink = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            rootSymlinkAdapter.ReadTeamSyncTreeAsync(
                new SourceControlRepositoryIdentity("octo", "widget"), "main", NewCredential(), default));
        Assert.Equal(SourceControlFailureCode.InvalidBinding, rootSymlink.Code);
    }

    [Fact]
    public async Task TeamSyncRejectsTraversalInDirectoryEntries()
    {
        // Validate directory paths too; malformed trees must not hide traversal in skipped entries.
        var adapter = CreateAdapter(new StubHandler((request, _) =>
            Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/main" => CommitJson(HeadSha, BaseSha),
                $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1" =>
                    TreeJson(new { path = ".squad/../outside", mode = "040000", type = "tree", sha = BaseSha, size = 0 }),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            })));

        var error = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            adapter.ReadTeamSyncTreeAsync(
                new SourceControlRepositoryIdentity("octo", "widget"), "main", NewCredential(), default));

        Assert.Equal(SourceControlFailureCode.InvalidRequest, error.Code);
    }

    [Fact]
    public async Task TeamSyncRejectsFileDirectoryConflictsBeforeCreatingBlobs()
    {
        // Conflicts against unchanged files and within one request must be rejected before POSTs.
        var existingSha = GitBlobSha(Encoding.UTF8.GetBytes("child"));
        var writesAgainstCurrentTree = 0;
        var currentTreeAdapter = CreateAdapter(new StubHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Get)
                writesAgainstCurrentTree++;
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/repos/octo/widget/commits/main")
                return Task.FromResult(CommitJson(HeadSha, BaseSha));
            if (path == $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1")
                return Task.FromResult(TreeJson(
                    new { path = ".squad/a/b", mode = "100644", type = "blob", sha = existingSha, size = 5 }));
            throw new InvalidOperationException("Unexpected GitHub request.");
        }));

        var existingConflict = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            currentTreeAdapter.CommitTeamSyncChangesAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                NewCredential(),
                new SourceControlTeamSyncCommitRequest(
                    "main",
                    HeadSha,
                    "Sync team files",
                    [new SourceControlTeamSyncChange(".squad/a", null, Encoding.UTF8.GetBytes("parent"))]),
                CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.InvalidRequest, existingConflict.Code);
        Assert.Equal(0, writesAgainstCurrentTree);

        var writesWithinRequest = 0;
        var emptyTreeAdapter = CreateAdapter(new StubHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Get)
                writesWithinRequest++;
            return Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/main" => CommitJson(HeadSha, BaseSha),
                $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1" =>
                    TreeJson(),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        }));

        var requestConflict = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            emptyTreeAdapter.CommitTeamSyncChangesAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                NewCredential(),
                new SourceControlTeamSyncCommitRequest(
                    "main",
                    HeadSha,
                    "Sync team files",
                    [
                        new SourceControlTeamSyncChange(".squad/a", null, Encoding.UTF8.GetBytes("parent")),
                        new SourceControlTeamSyncChange(".squad/a/b", null, Encoding.UTF8.GetBytes("child"))
                    ]),
                CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.InvalidRequest, requestConflict.Code);
        Assert.Equal(0, writesWithinRequest);
    }

    private static string GitBlobSha(byte[] content)
    {
        // Git blob IDs hash the object header and content rather than the raw file bytes alone.
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var blob = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, blob, 0, header.Length);
        Buffer.BlockCopy(content, 0, blob, header.Length, content.Length);
        return Convert.ToHexStringLower(SHA1.HashData(blob));
    }

    private static HttpResponseMessage CommitJson(string commitSha, string treeSha) =>
        JsonResponse(JsonSerializer.Serialize(new { sha = commitSha, commit = new { tree = new { sha = treeSha } } }));

    private static HttpResponseMessage TreeJson(params object[] entries) =>
        JsonResponse(JsonSerializer.Serialize(new { truncated = false, tree = entries }));

    private static HttpResponseMessage ShaJson(string sha) =>
        JsonResponse(JsonSerializer.Serialize(new { sha }));
}
