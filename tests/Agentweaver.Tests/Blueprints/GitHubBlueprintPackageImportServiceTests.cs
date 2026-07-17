using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain.BlueprintPackages;
using Agentweaver.Squad.BlueprintPackages;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Blueprints;

public sealed class GitHubBlueprintPackageImportServiceTests
{
    [Fact]
    public async Task Import_PinsEveryObjectReadToResolvedCommit_AndPersistsDescriptiveProvenance()
    {
        await using var database = await TestSqliteDb.CreateAsync();
        var github = ImportTestSupport.ValidClient();
        var library = new SqliteOwnerBlueprintPackageLibrary(database.Db, new Owner("authenticated-owner"));
        var service = new GitHubBlueprintPackageImportService(github, library);
        var locator = new GitHubBlueprintPackageLocator("octo", "private-blueprints", "package", "release/v1");

        var result = await service.ImportAsync(locator);
        var stored = await library.GetVersionAsync("engineering", "1.0.0");

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        github.TreeReads.Should().OnlyContain(read => read.CommitSha == ImportTestSupport.CommitSha);
        github.TreeReads.Should().Equal(
            new ImportTestSupport.TreeRead(ImportTestSupport.CommitSha, ImportTestSupport.TreeSha, false),
            new ImportTestSupport.TreeRead(ImportTestSupport.CommitSha, ImportTestSupport.PackageTreeSha, true));
        github.BlobCommitReads.Should().OnlyContain(sha => sha == ImportTestSupport.CommitSha);
        stored!.Acquisitions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new BlueprintPackageAcquisition(
                "github",
                Repository: "https://github.com/octo/private-blueprints",
                Revision: ImportTestSupport.CommitSha,
                RequestedRef: "release/v1"),
            options => options.Excluding(x => x.AcquiredAt));
    }

    [Fact]
    public async Task Import_IsIdempotentForSameImmutableMaterial_AndConflictsForChangedMaterial()
    {
        await using var database = await TestSqliteDb.CreateAsync();
        var library = new SqliteOwnerBlueprintPackageLibrary(database.Db, new Owner("authenticated-owner"));
        var first = new GitHubBlueprintPackageImportService(ImportTestSupport.ValidClient(), library);
        var same = new GitHubBlueprintPackageImportService(ImportTestSupport.ValidClient(), library);
        var changed = new GitHubBlueprintPackageImportService(ImportTestSupport.ValidClient("{\"changed\":true}"), library);
        var locator = new GitHubBlueprintPackageLocator("octo", "public-blueprints", "package");

        (await first.ImportAsync(locator)).Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        (await same.ImportAsync(locator)).Disposition.Should().Be(BlueprintPackagePersistDisposition.Idempotent);
        (await changed.ImportAsync(locator)).Disposition.Should().Be(BlueprintPackagePersistDisposition.ImmutableConflict);
    }

    [Theory]
    [InlineData("package/../escape")]
    [InlineData("/package")]
    [InlineData(@"package\child")]
    public async Task Import_RejectsUnsafePackageRootBeforeGitHubCalls(string root)
    {
        var github = ImportTestSupport.ValidClient();
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "blueprints", root));

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.InvalidLocator);
        github.ResolveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Import_RejectsTraversalCaseCollisionsSymlinksAndLfsPointers()
    {
        var cases = new[]
        {
            ImportTestSupport.ValidClient(extraEntries:
            [
                new("definitions/../escape.json", "blob", "100644", ImportTestSupport.ExtraSha, 1),
            ]),
            ImportTestSupport.ValidClient(extraEntries:
            [
                new("Definitions/blueprints/engineering.json", "blob", "100644", ImportTestSupport.ExtraSha, 1),
            ]),
            ImportTestSupport.ValidClient(extraEntries:
            [
                new("link", "blob", "120000", ImportTestSupport.ExtraSha, 1),
            ]),
            ImportTestSupport.ValidClient(lfsPayload: true),
        };

        foreach (var github in cases)
        {
            var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

            var action = () => service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "blueprints", "package"));

            (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
                .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.MalformedContent);
        }
    }

    [Fact]
    public async Task Import_TraversesLargeRepositoryNonRecursively_ThenLimitsOnlySmallNestedPackage()
    {
        var github = ImportTestSupport.NestedClientWithLargeRoot();
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var result = await service.ImportAsync(
            new GitHubBlueprintPackageLocator("octo", "monorepo", "catalog/small-package"));

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        github.TreeReads.Should().Equal(
            new ImportTestSupport.TreeRead(ImportTestSupport.CommitSha, ImportTestSupport.TreeSha, false),
            new ImportTestSupport.TreeRead(ImportTestSupport.CommitSha, ImportTestSupport.CatalogTreeSha, false),
            new ImportTestSupport.TreeRead(ImportTestSupport.CommitSha, ImportTestSupport.PackageTreeSha, true));
    }

    [Fact]
    public async Task Import_SupportsPackageAtRepositoryRoot()
    {
        var github = ImportTestSupport.ValidRootClient();
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var result = await service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "root-package"));

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        github.TreeReads.Should().Equal(
            new ImportTestSupport.TreeRead(ImportTestSupport.CommitSha, ImportTestSupport.TreeSha, true));
    }

    [Theory]
    [InlineData("Package", "tree", "040000")]
    [InlineData("package", "blob", "120000")]
    [InlineData("package", "commit", "160000")]
    public async Task Import_RejectsAmbiguousOrNonTreePackageRootSegments(string returnedPath, string type, string mode)
    {
        var github = ImportTestSupport.ValidClient();
        github.SetTree(
            ImportTestSupport.TreeSha,
            false,
            [new(returnedPath, type, mode, ImportTestSupport.PackageTreeSha, null)]);
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(
            new GitHubBlueprintPackageLocator("octo", "blueprints", "package"));

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.MalformedContent);
    }

    [Fact]
    public async Task Import_RejectsCaseAmbiguousPackageRootSegment()
    {
        var github = ImportTestSupport.ValidClient();
        github.SetTree(
            ImportTestSupport.TreeSha,
            false,
            [
                new("package", "tree", "040000", ImportTestSupport.PackageTreeSha, null),
                new("Package", "tree", "040000", ImportTestSupport.CatalogTreeSha, null),
            ]);
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(
            new GitHubBlueprintPackageLocator("octo", "blueprints", "package"));

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.MalformedContent);
    }

    [Fact]
    public async Task Import_RejectsSizeBoundsBeforeDownloadingPayloads()
    {
        var github = ImportTestSupport.ValidClient(payloadTreeSize: BlueprintPackageLimits.MaximumPayloadBytes + 1);
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "blueprints", "package"));

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.MalformedContent);
        github.BlobCommitReads.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_PropagatesDistinctGitHubRateLimitWithoutCredentialLeakage()
    {
        const string credential = "sensitive-github-credential";
        var github = ImportTestSupport.ValidClient();
        github.ResolveFailure = new GitHubBlueprintPackageAcquisitionException(
            GitHubBlueprintPackageAcquisitionFailure.RateLimited,
            "GitHub API rate limit was reached.");
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "blueprints"));

        var error = (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>()).Which;
        error.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.RateLimited);
        error.ToString().Should().NotContain(credential);
    }

    [Fact]
    public async Task Import_RejectsBlobObjectChangedAfterImmutableTreeRead()
    {
        var github = ImportTestSupport.ValidClient();
        github.ReturnedBlobShaOverride = ImportTestSupport.ExtraSha;
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "blueprints", "package"));

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.ObjectChanged);
    }

    [Fact]
    public async Task Import_RejectsTreeObjectChangedAtAnyTraversalRead()
    {
        var github = ImportTestSupport.ValidClient();
        github.ReturnedTreeShaOverride = ImportTestSupport.CatalogTreeSha;
        var service = new GitHubBlueprintPackageImportService(github, new ImportTestSupport.RecordingLibrary());

        var action = () => service.ImportAsync(new GitHubBlueprintPackageLocator("octo", "blueprints", "package"));

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.ObjectChanged);
    }

    [Theory]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nsize 42\n")]
    [InlineData("version https://git-lfs.github.com/spec/v1\r\next-0-agentweaver sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\r\noid sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\nsize 42\r\n")]
    public void LfsPointerDetection_AcceptsSpecLineEndingsAndExtensions(string pointer)
    {
        GitHubBlueprintPackageImportService.IsLfsPointer(Encoding.UTF8.GetBytes(pointer)).Should().BeTrue();
    }

    [Theory]
    [InlineData("ordinary text")]
    [InlineData("version https://git-lfs.github.com/spec/v1\nthis is documentation, not a pointer\n")]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:not-a-hash\nsize 42\n")]
    [InlineData("prefix version https://git-lfs.github.com/spec/v1\noid sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nsize 42\n")]
    public void LfsPointerDetection_DoesNotRejectOrdinaryOrMalformedText(string text)
    {
        GitHubBlueprintPackageImportService.IsLfsPointer(Encoding.UTF8.GetBytes(text)).Should().BeFalse();
    }

    private sealed record Owner(string OwnerId) : IAuthenticatedOwnerContext;
}

internal static class ImportTestSupport
{
    internal const string CommitSha = "1111111111111111111111111111111111111111";
    internal const string TreeSha = "2222222222222222222222222222222222222222";
    internal const string CatalogTreeSha = "3333333333333333333333333333333333333333";
    internal const string PackageTreeSha = "4444444444444444444444444444444444444444";
    internal static readonly string ExtraSha = GitBlobSha([0]);

    internal static FakeGitHubClient ValidClient(
        string payload = "{}",
        IEnumerable<GitHubBlueprintPackageTreeEntry>? extraEntries = null,
        bool lfsPayload = false,
        long? payloadTreeSize = null)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(lfsPayload
            ? "version https://git-lfs.github.com/spec/v1\noid sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nsize 1\n"
            : payload);
        var manifest = Manifest(payloadBytes);
        var manifestSha = GitBlobSha(manifest);
        var payloadSha = GitBlobSha(payloadBytes);
        var entries = new List<GitHubBlueprintPackageTreeEntry>
        {
            new("manifest.json", "blob", "100644", manifestSha, manifest.Length),
            new("definitions/blueprints/engineering.json", "blob", "100644", payloadSha, payloadTreeSize ?? payloadBytes.Length),
        };
        if (extraEntries is not null) entries.AddRange(extraEntries);
        var client = new FakeGitHubClient(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [manifestSha] = manifest,
            [payloadSha] = payloadBytes,
            [ExtraSha] = [0],
        });
        client.SetTree(TreeSha, false, [new("package", "tree", "040000", PackageTreeSha, null)]);
        client.SetTree(PackageTreeSha, true, entries);
        return client;
    }

    internal static FakeGitHubClient ValidRootClient()
    {
        var client = ValidClient();
        client.SetTree(TreeSha, true, client.GetTreeEntries(PackageTreeSha, true));
        return client;
    }

    internal static FakeGitHubClient NestedClientWithLargeRoot()
    {
        var client = ValidClient();
        var largeRoot = Enumerable.Range(0, 600)
            .Select(index => new GitHubBlueprintPackageTreeEntry(
                $"unrelated-{index:D3}", "blob", "100644", ExtraSha, 1))
            .Append(new("catalog", "tree", "040000", CatalogTreeSha, null))
            .ToArray();
        client.SetTree(TreeSha, false, largeRoot);
        client.SetTree(CatalogTreeSha, false, [new("small-package", "tree", "040000", PackageTreeSha, null)]);
        return client;
    }

    private static byte[] Manifest(byte[] payload)
    {
        var document = new
        {
            schema_version = "1",
            package = new { id = "engineering", version = "1.0.0" },
            definitions = new[]
            {
                new
                {
                    kind = "blueprint",
                    id = "engineering",
                    path = "definitions/blueprints/engineering.json",
                    size = payload.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
    }

    internal sealed record TreeRead(string CommitSha, string TreeSha, bool Recursive);

    internal sealed class FakeGitHubClient(
        IReadOnlyDictionary<string, byte[]> blobs) : IGitHubBlueprintPackageClient
    {
        private readonly Dictionary<(string Sha, bool Recursive), IReadOnlyList<GitHubBlueprintPackageTreeEntry>> _trees = [];
        internal int ResolveCalls { get; private set; }
        internal List<TreeRead> TreeReads { get; } = [];
        internal List<string> BlobCommitReads { get; } = [];
        internal Exception? ResolveFailure { get; set; }
        internal string? ReturnedBlobShaOverride { get; set; }
        internal string? ReturnedTreeShaOverride { get; set; }

        internal void SetTree(
            string sha,
            bool recursive,
            IReadOnlyList<GitHubBlueprintPackageTreeEntry> entries) =>
            _trees[(sha, recursive)] = entries;

        internal IReadOnlyList<GitHubBlueprintPackageTreeEntry> GetTreeEntries(string sha, bool recursive) =>
            _trees[(sha, recursive)];

        public Task<GitHubBlueprintPackageCommit> ResolveCommitAsync(GitHubBlueprintPackageLocator locator, CancellationToken ct = default)
        {
            ResolveCalls++;
            if (ResolveFailure is not null) throw ResolveFailure;
            return Task.FromResult(new GitHubBlueprintPackageCommit(CommitSha, TreeSha));
        }

        public Task<GitHubBlueprintPackageTree> ReadTreeAsync(
            GitHubBlueprintPackageLocator locator,
            string commitSha,
            string treeSha,
            bool recursive,
            CancellationToken ct = default)
        {
            TreeReads.Add(new(commitSha, treeSha, recursive));
            return Task.FromResult(new GitHubBlueprintPackageTree(
                ReturnedTreeShaOverride ?? treeSha,
                _trees[(treeSha, recursive)],
                false));
        }

        public Task<GitHubBlueprintPackageBlob> ReadBlobAsync(
            GitHubBlueprintPackageLocator locator, string commitSha, string blobSha, CancellationToken ct = default)
        {
            BlobCommitReads.Add(commitSha);
            return Task.FromResult(new GitHubBlueprintPackageBlob(ReturnedBlobShaOverride ?? blobSha, blobs[blobSha]));
        }
    }

    internal sealed class RecordingLibrary : IOwnerBlueprintPackageLibrary
    {
        public List<BlueprintPackageWrite> Writes { get; } = [];
        public Task<BlueprintPackagePersistResult> PersistAsync(BlueprintPackageWrite package, CancellationToken ct = default)
        {
            Writes.Add(package);
            return Task.FromResult(new BlueprintPackagePersistResult(BlueprintPackagePersistDisposition.Created));
        }
        public Task<OwnerBlueprintPackageVersion?> GetVersionAsync(string packageId, string canonicalVersion, CancellationToken ct = default) =>
            Task.FromResult<OwnerBlueprintPackageVersion?>(null);
        public Task<IReadOnlyList<OwnerBlueprintPackageEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OwnerBlueprintPackageEntry>>([]);
        public Task<bool> DeletePackageAsync(string packageId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
