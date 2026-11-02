using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed partial class GitHubSourceControlAdapterTests
{
    [Fact]
    public async Task WorkspaceRefsReturnBoundedPagesAndPinTheDefaultBranch()
    {
        var calls = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            return Task.FromResult(path switch
            {
                "/repos/octo/widget/branches/main" =>
                    JsonResponse(JsonSerializer.Serialize(new { name = "main", commit = new { sha = HeadSha } })),
                "/repos/octo/widget/branches?per_page=49&page=1" => WithNextLink(JsonResponse(
                    JsonSerializer.Serialize(new[]
                    {
                        new { name = "main", commit = new { sha = HeadSha } },
                        new { name = "release", commit = new { sha = BaseSha } }
                    }))),
                "/repos/octo/widget/tags?per_page=50&page=1" => JsonResponse(
                    JsonSerializer.Serialize(new[] { new { name = "v1", commit = new { sha = BaseSha } } })),
                "/repos/octo/widget/git/ref/tags/v1" => JsonResponse(
                    JsonSerializer.Serialize(new
                    {
                        @ref = "refs/tags/v1",
                        @object = new { type = "commit", sha = BaseSha }
                    })),
                "/repos/octo/widget/branches?per_page=49&page=2" => JsonResponse("[]"),
                "/repos/octo/widget/tags?per_page=50&page=2" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });
        var adapter = CreateAdapter(handler);
        var repository = new SourceControlRepositoryIdentity("octo", "widget");

        var first = await adapter.ReadWorkspaceRefsAsync(
            repository, "main", null, NewCredential(), CancellationToken.None);
        var second = await adapter.ReadWorkspaceRefsAsync(
            repository, "main", first.NextCursor, NewCredential(), CancellationToken.None);

        Assert.Equal("octo/widget", first.Repository);
        Assert.Equal("main", first.DefaultBranch);
        Assert.Equal(3, first.Items.Length);
        Assert.Contains(first.Items, item => item.Name == "main" && item.IsDefault && item.CommitSha == HeadSha);
        Assert.Contains(first.Items, item => item.Name == "release" && !item.IsDefault);
        Assert.Contains(first.Items, item => item.Name == "v1" && item.Kind == SourceControlWorkspaceRefKind.Tag);
        Assert.Equal("2", first.NextCursor);
        Assert.Empty(second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(6, calls.Count);
    }

    [Fact]
    public async Task WorkspaceRefsDereferenceAnnotatedTagsToCommitShas()
    {
        const string tagSha = "cccccccccccccccccccccccccccccccccccccccc";
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(path switch
            {
                "/repos/octo/widget/branches/main" =>
                    JsonResponse(JsonSerializer.Serialize(new { name = "main", commit = new { sha = HeadSha } })),
                "/repos/octo/widget/branches?per_page=49&page=1" =>
                    JsonResponse("[]"),
                "/repos/octo/widget/tags?per_page=50&page=1" =>
                    JsonResponse(JsonSerializer.Serialize(new[] { new { name = "v2", commit = new { sha = tagSha } } })),
                "/repos/octo/widget/git/ref/tags/v2" =>
                    JsonResponse(JsonSerializer.Serialize(new
                    {
                        @ref = "refs/tags/v2",
                        @object = new { type = "tag", sha = tagSha }
                    })),
                "/repos/octo/widget/git/tags/" + tagSha =>
                    JsonResponse(JsonSerializer.Serialize(new
                    {
                        sha = tagSha,
                        @object = new { type = "commit", sha = BaseSha }
                    })),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });
        var adapter = CreateAdapter(handler);

        var page = await adapter.ReadWorkspaceRefsAsync(
            new SourceControlRepositoryIdentity("octo", "widget"),
            "main",
            null,
            NewCredential(),
            CancellationToken.None);

        Assert.Contains(page.Items, item =>
            item.Name == "v2" && item.Kind == SourceControlWorkspaceRefKind.Tag && item.CommitSha == BaseSha);
    }

    [Fact]
    public async Task WorkspaceRefsAcceptUnicodeBranchesAndEscapeBuildMetadataTags()
    {
        const string branch = "feature/über";
        const string tag = "v1.0.0+build.1";
        var calls = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            return Task.FromResult(path switch
            {
                "/repos/octo/widget/branches/main" =>
                    JsonResponse(JsonSerializer.Serialize(new { name = "main", commit = new { sha = HeadSha } })),
                "/repos/octo/widget/branches?per_page=49&page=1" => JsonResponse(JsonSerializer.Serialize(new[]
                {
                    new { name = "main", commit = new { sha = HeadSha } },
                    new { name = branch, commit = new { sha = BaseSha } }
                })),
                "/repos/octo/widget/tags?per_page=50&page=1" => JsonResponse(JsonSerializer.Serialize(new[]
                {
                    new { name = tag, commit = new { sha = BaseSha } }
                })),
                "/repos/octo/widget/git/ref/tags/v1.0.0%2Bbuild.1" => JsonResponse(JsonSerializer.Serialize(new
                {
                    @ref = "refs/tags/" + tag,
                    @object = new { type = "commit", sha = BaseSha }
                })),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });
        var adapter = CreateAdapter(handler);

        var page = await adapter.ReadWorkspaceRefsAsync(
            new SourceControlRepositoryIdentity("octo", "widget"),
            "main",
            null,
            NewCredential(),
            CancellationToken.None);

        Assert.Contains(page.Items, item =>
            item.Name == branch && item.Kind == SourceControlWorkspaceRefKind.Branch);
        Assert.Contains(page.Items, item =>
            item.Name == tag && item.Kind == SourceControlWorkspaceRefKind.Tag && item.CommitSha == BaseSha);
        Assert.Contains("/repos/octo/widget/git/ref/tags/v1.0.0%2Bbuild.1", calls);
    }

    [Fact]
    public async Task WorkspaceRefsAcceptUppercaseLockSuffixAndRejectLowercaseLockSuffix()
    {
        async Task<SourceControlWorkspaceRefsPage> ReadRefs(string tagName)
        {
            var handler = new StubHandler((request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                return Task.FromResult(path switch
                {
                    "/repos/octo/widget/branches/main" =>
                        JsonResponse(JsonSerializer.Serialize(new { name = "main", commit = new { sha = HeadSha } })),
                    "/repos/octo/widget/branches?per_page=49&page=1" => JsonResponse("[]"),
                    "/repos/octo/widget/tags?per_page=50&page=1" => JsonResponse(JsonSerializer.Serialize(new[]
                    {
                        new { name = tagName, commit = new { sha = BaseSha } }
                    })),
                    var value when value == "/repos/octo/widget/git/ref/tags/" + tagName =>
                        JsonResponse(JsonSerializer.Serialize(new
                        {
                            @ref = "refs/tags/" + tagName,
                            @object = new { type = "commit", sha = BaseSha }
                        })),
                    _ => throw new InvalidOperationException("Unexpected GitHub request.")
                });
            });
            return await CreateAdapter(handler).ReadWorkspaceRefsAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                "main",
                null,
                NewCredential(),
                CancellationToken.None);
        }

        var accepted = await ReadRefs("release.LOCK");
        Assert.Contains(accepted.Items, item => item.Name == "release.LOCK");

        var rejected = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => ReadRefs("release.lock"));
        Assert.Equal(SourceControlFailureCode.InvalidResponse, rejected.Code);
    }

    [Fact]
    public async Task WorkspaceRefsRejectMalformedGitNames()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(path switch
            {
                "/repos/octo/widget/branches/main" =>
                    JsonResponse(JsonSerializer.Serialize(new { name = "main", commit = new { sha = HeadSha } })),
                "/repos/octo/widget/branches?per_page=49&page=1" => JsonResponse(JsonSerializer.Serialize(new[]
                {
                    new { name = "release..candidate", commit = new { sha = BaseSha } }
                })),
                _ => throw new InvalidOperationException("Malformed branch names must stop refs parsing.")
            });
        });
        var adapter = CreateAdapter(handler);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            adapter.ReadWorkspaceRefsAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                "main",
                null,
                NewCredential(),
                CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task WorkspaceTreeAndFileStayPinnedToTheSelectedCommit()
    {
        var content = Encoding.UTF8.GetBytes("hello\n");
        var blobSha = GitBlobSha(content);
        var calls = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            if (path == "/repos/octo/widget/commits/" + HeadSha)
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = HeadSha,
                    commit = new { tree = new { sha = BaseSha } }
                })));
            if (path == "/repos/octo/widget/git/trees/" + BaseSha + "?recursive=1")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = BaseSha,
                    truncated = false,
                    tree = new object[]
                    {
                        new { path = "src", mode = "040000", type = "tree", sha = BaseSha },
                        new { path = "src/nested", mode = "040000", type = "tree", sha = BaseSha },
                        new { path = "src/nested/a file.cs", mode = "100644", type = "blob", sha = blobSha, size = content.Length },
                    }
                })));
            if (path == "/repos/octo/widget/git/blobs/" + blobSha)
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    encoding = "base64",
                    content = Convert.ToBase64String(content)
                })));
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        var adapter = CreateAdapter(handler);
        var repository = new SourceControlRepositoryIdentity("octo", "widget");

        var tree = await adapter.ReadWorkspaceTreeAsync(repository, HeadSha, NewCredential(), CancellationToken.None);
        var file = await adapter.ReadWorkspaceFileAsync(
            repository, HeadSha, "src/nested/a file.cs", NewCredential(), CancellationToken.None);

        Assert.Equal(HeadSha, tree.CommitSha);
        Assert.Contains(tree.Items, item => item.Path == "src/nested/a file.cs" &&
            item.Kind == SourceControlWorkspaceEntryKind.File && item.BlobSha == blobSha);
        Assert.Equal("hello\n", file.Content);
        Assert.Equal("csharp", file.Language);
        Assert.Equal(HeadSha, file.CommitSha);
        Assert.Equal(5, calls.Count);
        Assert.Equal(HeadSha, calls[0].Split('/').Last());
        Assert.Equal(HeadSha, calls[2].Split('/').Last());
    }

    [Fact]
    public async Task WorkspaceRejectsTruncatedTreesAndUnsafeOrOversizedFiles()
    {
        var calls = 0;
        var truncatedAdapter = CreateAdapter(new StubHandler((request, _) =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.PathAndQuery switch
            {
                "/repos/octo/widget/commits/" + HeadSha => JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = HeadSha,
                    commit = new { tree = new { sha = BaseSha } }
                })),
                "/repos/octo/widget/git/trees/" + BaseSha + "?recursive=1" => JsonResponse(
                    JsonSerializer.Serialize(new { sha = BaseSha, truncated = true, tree = Array.Empty<object>() })),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        }));
        var treeError = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            truncatedAdapter.ReadWorkspaceTreeAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                HeadSha,
                NewCredential(),
                CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.ResponseTooLarge, treeError.Code);

        var noCalls = 0;
        var guarded = CreateAdapter(new StubHandler((_, _) =>
        {
            noCalls++;
            throw new InvalidOperationException("Unsafe requests must be rejected before GitHub.");
        }));
        var invalidPath = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            guarded.ReadWorkspaceFileAsync(
                new SourceControlRepositoryIdentity("octo", "widget"),
                HeadSha,
                "../private.txt",
                NewCredential(),
                CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.InvalidRequest, invalidPath.Code);
        Assert.Equal(0, noCalls);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WorkspaceMarksBinaryFilesAndRejectsOversizedFilesBeforeBlobRead()
    {
        var binary = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF };
        var binarySha = GitBlobSha(binary);
        var blobReads = 0;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == $"/repos/octo/widget/commits/{HeadSha}")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = HeadSha,
                    commit = new { tree = new { sha = BaseSha } }
                })));
            if (path == $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = BaseSha,
                    truncated = false,
                    tree = new[]
                    {
                        new { path = "file.bin", mode = "100644", type = "blob", sha = binarySha, size = binary.Length },
                        new { path = "large.bin", mode = "100644", type = "blob", sha = BaseSha, size = 1024 * 1024 + 1 },
                    }
                })));
            if (path == $"/repos/octo/widget/git/blobs/{binarySha}")
            {
                blobReads++;
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    encoding = "base64",
                    content = Convert.ToBase64String(binary)
                })));
            }
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        var adapter = CreateAdapter(handler);
        var repository = new SourceControlRepositoryIdentity("octo", "widget");

        var file = await adapter.ReadWorkspaceFileAsync(repository, HeadSha, "file.bin", NewCredential(), CancellationToken.None);
        var large = await Assert.ThrowsAsync<SourceControlOperationException>(() =>
            adapter.ReadWorkspaceFileAsync(repository, HeadSha, "large.bin", NewCredential(), CancellationToken.None));

        Assert.True(file.IsBinary);
        Assert.Null(file.Content);
        Assert.Equal(SourceControlFailureCode.ResponseTooLarge, large.Code);
        Assert.Equal(1, blobReads);
    }

    [Fact]
    public async Task WorkspaceDoesNotMarkUtf8TextBinaryWhenProbeEndsMidCodePoint()
    {
        var bytes = Enumerable.Repeat((byte)'a', 8193).ToArray();
        bytes[8191] = 0xc2;
        bytes[8192] = 0xa2;
        var blobSha = GitBlobSha(bytes);
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == $"/repos/octo/widget/commits/{HeadSha}")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = HeadSha,
                    commit = new { tree = new { sha = BaseSha } }
                })));
            if (path == $"/repos/octo/widget/git/trees/{BaseSha}?recursive=1")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    sha = BaseSha,
                    truncated = false,
                    tree = new[]
                    {
                        new { path = "text.txt", mode = "100644", type = "blob", sha = blobSha, size = bytes.Length }
                    }
                })));
            if (path == $"/repos/octo/widget/git/blobs/{blobSha}")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    encoding = "base64",
                    content = Convert.ToBase64String(bytes)
                })));
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        var adapter = CreateAdapter(handler);

        var file = await adapter.ReadWorkspaceFileAsync(
            new SourceControlRepositoryIdentity("octo", "widget"),
            HeadSha,
            "text.txt",
            NewCredential(),
            CancellationToken.None);

        Assert.False(file.IsBinary);
        Assert.Equal(Encoding.UTF8.GetString(bytes), file.Content);
    }

    private static HttpResponseMessage WithNextLink(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation(
            "Link",
            "<https://api.github.com/repos/octo/widget/branches?per_page=49&page=2>; rel=\"next\"");
        return response;
    }
}
