using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Domain.BlueprintPackages;
using Agentweaver.Squad.BlueprintPackages;

namespace Agentweaver.Api.Blueprints;

/// <summary>Acquires one immutable, definitions-only package from an authenticated github.com repository.</summary>
public sealed class GitHubBlueprintPackageImportService
{
    private const int MaximumTreeEntries = 512;
    private readonly IGitHubBlueprintPackageClient _github;
    private readonly IOwnerBlueprintPackageLibrary _library;

    public GitHubBlueprintPackageImportService(
        IGitHubBlueprintPackageClient github,
        IOwnerBlueprintPackageLibrary library)
    {
        _github = github;
        _library = library;
    }

    public async Task<GitHubBlueprintPackageImportResult> ImportAsync(
        GitHubBlueprintPackageLocator locator,
        CancellationToken ct = default)
    {
        locator.Validate();
        var commit = await _github.ResolveCommitAsync(locator, ct).ConfigureAwait(false);
        if (!GitHubBlueprintPackagePath.IsFullSha(commit.CommitSha) || !GitHubBlueprintPackagePath.IsFullSha(commit.TreeSha))
            throw Malformed("GitHub returned invalid immutable object identifiers.");

        var tree = await _github.ReadTreeAsync(locator, commit.CommitSha, commit.TreeSha, ct).ConfigureAwait(false);
        if (tree.IsTruncated || tree.Entries.Count > MaximumTreeEntries)
            throw Malformed("GitHub tree is truncated or exceeds the package object limit.");

        var files = SelectPackageFiles(locator, tree);
        PreflightTree(files);
        if (!files.TryGetValue("manifest.json", out var manifestEntry))
            throw Malformed("Package manifest.json is missing.");

        var rawManifest = await ReadVerifiedBlobAsync(locator, commit.CommitSha, manifestEntry, ct).ConfigureAwait(false);
        RejectLfsPointer(rawManifest);
        var inventory = ReadDeclaredInventory(rawManifest);
        ValidateInventoryAgainstTree(files, inventory);

        var payloads = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var definition in inventory.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var entry = files[definition.Path];
            if (entry.Size != definition.Size)
                throw Malformed("Manifest inventory size does not match the immutable tree.");
            var bytes = await ReadVerifiedBlobAsync(locator, commit.CommitSha, entry, ct).ConfigureAwait(false);
            RejectLfsPointer(bytes);
            payloads.Add(definition.Path, ImmutableArray.CreateRange(bytes));
        }

        var validation = BlueprintPackageValidator.Validate(
            new BlueprintPackageSource(rawManifest, payloads.Select(x => new KeyValuePair<string, byte[]>(x.Key, x.Value.ToArray()))));
        if (!validation.IsValid)
            throw Malformed("Package content does not meet the Blueprint package v1 contract.");

        var package = validation.Package!;
        var material = new BlueprintPackageWrite(
            package.Manifest.PackageId,
            package.Manifest.Version.ToString(),
            rawManifest,
            payloads.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new BlueprintPackagePayload(x.Key, x.Value.ToArray())).ToArray(),
            package.Digests.SemanticSha256,
            package.Digests.PayloadSetSha256,
            package.Digests.RawManifestSha256,
            package.Digests.ContainerSha256,
            [new BlueprintPackageAcquisition(
                "github",
                Repository: $"https://github.com/{locator.Owner}/{locator.Repository}",
                Revision: commit.CommitSha,
                AcquiredAt: DateTimeOffset.UtcNow,
                RequestedRef: locator.Ref)]);
        var persisted = await _library.PersistAsync(material, ct).ConfigureAwait(false);
        return new(
            persisted.Disposition,
            package.Manifest.PackageId,
            package.Manifest.Version.ToString(),
            $"https://github.com/{locator.Owner}/{locator.Repository}",
            commit.CommitSha,
            locator.Ref);
    }

    private static Dictionary<string, GitHubBlueprintPackageTreeEntry> SelectPackageFiles(
        GitHubBlueprintPackageLocator locator,
        GitHubBlueprintPackageTree tree)
    {
        var root = locator.PackageRootPath ?? string.Empty;
        var prefix = root.Length == 0 ? string.Empty : $"{root}/";
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, GitHubBlueprintPackageTreeEntry>(StringComparer.Ordinal);

        foreach (var entry in tree.Entries)
        {
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = entry.Path[prefix.Length..];
            if (!GitHubBlueprintPackagePath.IsCanonicalPosixPath(relative)
                || !sourcePaths.Add(relative)
                || !caseInsensitivePaths.Add(relative))
                throw Malformed("GitHub tree contains a non-canonical or case-colliding package path.");
            if (entry.Type == "tree")
            {
                if (entry.Mode != "040000") throw Malformed("GitHub tree contains an unsupported directory object.");
                continue;
            }
            if (entry.Type != "blob" || entry.Mode is not ("100644" or "100755")
                || !GitHubBlueprintPackagePath.IsFullSha(entry.Sha))
                throw Malformed("GitHub tree contains a symlink, submodule, or unsupported object.");
            if (!files.TryAdd(relative, entry))
                throw Malformed("GitHub tree contains duplicate package files.");
        }
        return files;
    }

    private static void PreflightTree(IReadOnlyDictionary<string, GitHubBlueprintPackageTreeEntry> files)
    {
        if (files.Count is 0 or > BlueprintPackageLimits.MaximumDefinitions + 1)
            throw Malformed("Package file count exceeds the v1 package limit.");

        long payloadBytes = 0;
        foreach (var (path, entry) in files)
        {
            if (entry.Size is null or < 0)
                throw Malformed("GitHub tree omitted a blob size.");
            if (path == "manifest.json")
            {
                if (entry.Size > BlueprintPackageLimits.MaximumManifestBytes)
                    throw Malformed("Manifest exceeds the package byte limit.");
                continue;
            }
            if (entry.Size > BlueprintPackageLimits.MaximumPayloadBytes
                || payloadBytes > BlueprintPackageLimits.MaximumTotalPayloadBytes - entry.Size.Value)
                throw Malformed("Package payloads exceed the package byte limit.");
            payloadBytes += entry.Size.Value;
        }
    }

    private async Task<byte[]> ReadVerifiedBlobAsync(
        GitHubBlueprintPackageLocator locator,
        string commitSha,
        GitHubBlueprintPackageTreeEntry entry,
        CancellationToken ct)
    {
        var blob = await _github.ReadBlobAsync(locator, commitSha, entry.Sha, ct).ConfigureAwait(false);
        if (!string.Equals(blob.Sha, entry.Sha, StringComparison.Ordinal)
            || blob.Bytes is null
            || blob.Bytes.LongLength != entry.Size
            || !string.Equals(GitBlobSha(blob.Bytes), entry.Sha, StringComparison.Ordinal))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.ObjectChanged,
                "GitHub blob changed after the immutable tree was read.");
        return blob.Bytes;
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<ManifestDefinition> ReadDeclaredInventory(byte[] rawManifest)
    {
        try
        {
            using var document = JsonDocument.Parse(rawManifest, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("definitions", out var definitions)
                || definitions.ValueKind != JsonValueKind.Array
                || definitions.GetArrayLength() is < 1 or > BlueprintPackageLimits.MaximumDefinitions)
                throw Malformed("Manifest inventory is invalid.");

            var paths = new HashSet<string>(StringComparer.Ordinal);
            var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ManifestDefinition>();
            foreach (var definition in definitions.EnumerateArray())
            {
                if (definition.ValueKind != JsonValueKind.Object
                    || !definition.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind != JsonValueKind.String
                    || !definition.TryGetProperty("size", out var sizeElement)
                    || !sizeElement.TryGetInt64(out var size))
                    throw Malformed("Manifest inventory entry is invalid.");
                var path = pathElement.GetString();
                if (!GitHubBlueprintPackagePath.IsCanonicalPosixPath(path)
                    || !paths.Add(path!)
                    || !caseInsensitivePaths.Add(path!))
                    throw Malformed("Manifest inventory path is non-canonical or duplicates another path.");
                if (size is < 0 or > BlueprintPackageLimits.MaximumPayloadBytes)
                    throw Malformed("Manifest inventory size exceeds the package byte limit.");
                result.Add(new(path!, size));
            }
            return result;
        }
        catch (JsonException)
        {
            throw Malformed("Manifest is not valid JSON.");
        }
    }

    private static void ValidateInventoryAgainstTree(
        IReadOnlyDictionary<string, GitHubBlueprintPackageTreeEntry> files,
        IReadOnlyList<ManifestDefinition> inventory)
    {
        var expected = new HashSet<string>(inventory.Select(item => item.Path), StringComparer.Ordinal)
        {
            "manifest.json",
        };
        if (!expected.SetEquals(files.Keys))
            throw Malformed("Package has missing manifest entries or unlisted files.");
        long total = 0;
        foreach (var entry in inventory)
        {
            if (total > BlueprintPackageLimits.MaximumTotalPayloadBytes - entry.Size)
                throw Malformed("Manifest payloads exceed the package byte limit.");
            total += entry.Size;
        }
    }

    private static void RejectLfsPointer(byte[] bytes)
    {
        var prefix = Encoding.ASCII.GetBytes("version https://git-lfs.github.com/spec/v1\n");
        if (bytes.AsSpan().StartsWith(prefix))
            throw Malformed("Git LFS pointers are not supported in Blueprint packages.");
    }

    private static GitHubBlueprintPackageAcquisitionException Malformed(string message) =>
        new(GitHubBlueprintPackageAcquisitionFailure.MalformedContent, message);

    private sealed record ManifestDefinition(string Path, long Size);
}

/// <summary>Credential-free result of a GitHub package import.</summary>
public sealed record GitHubBlueprintPackageImportResult(
    BlueprintPackagePersistDisposition Disposition,
    string PackageId,
    string CanonicalVersion,
    string Repository,
    string CommitSha,
    string? RequestedRef);
