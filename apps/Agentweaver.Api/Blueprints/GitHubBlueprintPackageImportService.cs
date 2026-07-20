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

        var tree = await ResolvePackageTreeAsync(locator, commit, ct).ConfigureAwait(false);
        if (tree.IsTruncated || tree.Entries.Count > MaximumTreeEntries)
            throw Malformed("GitHub tree is truncated or exceeds the package object limit.");

        var files = SelectPackageFiles(tree);
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

    private async Task<GitHubBlueprintPackageTree> ResolvePackageTreeAsync(
        GitHubBlueprintPackageLocator locator,
        GitHubBlueprintPackageCommit commit,
        CancellationToken ct)
    {
        var treeSha = commit.TreeSha;
        if (!string.IsNullOrEmpty(locator.PackageRootPath))
        {
            foreach (var segment in locator.PackageRootPath.Split('/'))
            {
                var parent = await ReadVerifiedTreeAsync(
                    locator, commit.CommitSha, treeSha, recursive: false, ct).ConfigureAwait(false);
                if (parent.IsTruncated)
                    throw Malformed("GitHub truncated a package-root traversal tree.");

                var matches = parent.Entries
                    .Where(entry => string.Equals(entry.Path, segment, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1 || !string.Equals(matches[0].Path, segment, StringComparison.Ordinal))
                    throw Malformed("Package root is missing or has ambiguous casing.");

                var selected = matches[0];
                if (selected.Type != "tree" || selected.Mode != "040000"
                    || !GitHubBlueprintPackagePath.IsFullSha(selected.Sha))
                    throw Malformed("Package root contains a symlink, submodule, or non-tree path segment.");
                treeSha = selected.Sha;
            }
        }

        return await ReadVerifiedTreeAsync(
            locator, commit.CommitSha, treeSha, recursive: true, ct).ConfigureAwait(false);
    }

    private async Task<GitHubBlueprintPackageTree> ReadVerifiedTreeAsync(
        GitHubBlueprintPackageLocator locator,
        string commitSha,
        string treeSha,
        bool recursive,
        CancellationToken ct)
    {
        var tree = await _github.ReadTreeAsync(locator, commitSha, treeSha, recursive, ct).ConfigureAwait(false);
        if (!string.Equals(tree.Sha, treeSha, StringComparison.Ordinal))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.ObjectChanged,
                "GitHub tree changed after the immutable commit was resolved.");
        return tree;
    }

    private static Dictionary<string, GitHubBlueprintPackageTreeEntry> SelectPackageFiles(
        GitHubBlueprintPackageTree tree)
    {
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, GitHubBlueprintPackageTreeEntry>(StringComparer.Ordinal);

        foreach (var entry in tree.Entries)
        {
            var relative = entry.Path;
            if (!GitHubBlueprintPackagePath.IsCanonicalPosixPath(relative)
                || !sourcePaths.Add(relative)
                || !caseInsensitivePaths.Add(relative))
                throw Malformed("GitHub tree contains a non-canonical or case-colliding package path.");
            if (entry.Type == "tree")
            {
                if (entry.Mode != "040000" || !GitHubBlueprintPackagePath.IsFullSha(entry.Sha))
                    throw Malformed("GitHub tree contains an unsupported directory object.");
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
        if (IsLfsPointer(bytes))
            throw Malformed("Git LFS pointers are not supported in Blueprint packages.");
    }

    internal static bool IsLfsPointer(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'))
                return false;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(normalized) > 1_024)
            return false;
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 3
            || lines[0] != "version https://git-lfs.github.com/spec/v1"
            || lines.Any(line => line.Length == 0))
            return false;

        var indexOfLine = 1;
        string? previousExtensionKey = null;
        while (indexOfLine < lines.Count
            && IsLfsExtension(lines[indexOfLine], out var extensionKey))
        {
            if (previousExtensionKey is not null
                && string.CompareOrdinal(previousExtensionKey, extensionKey) >= 0)
                return false;
            previousExtensionKey = extensionKey;
            indexOfLine++;
        }
        if (indexOfLine + 2 != lines.Count)
            return false;

        const string oidPrefix = "oid sha256:";
        var oid = lines[indexOfLine++];
        if (!oid.StartsWith(oidPrefix, StringComparison.Ordinal)
            || oid.Length != oidPrefix.Length + 64
            || !oid[oidPrefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;

        const string sizePrefix = "size ";
        var size = lines[indexOfLine];
        if (!size.StartsWith(sizePrefix, StringComparison.Ordinal))
            return false;
        var digits = size[sizePrefix.Length..];
        return digits.Length is > 0 and <= 20
            && digits.All(char.IsAsciiDigit)
            && long.TryParse(digits, out _);
    }

    private static bool IsLfsExtension(string line, out string key)
    {
        key = string.Empty;
        if (!line.StartsWith("ext-", StringComparison.Ordinal))
            return false;
        var priorityEnd = line.IndexOf('-', 4);
        var nameEnd = line.IndexOf(' ', priorityEnd + 1);
        if (priorityEnd <= 4 || nameEnd <= priorityEnd + 1 || nameEnd == line.Length - 1)
            return false;
        key = line[..nameEnd];
        var value = line[(nameEnd + 1)..];
        return line[4..priorityEnd].All(char.IsAsciiDigit)
            && line[(priorityEnd + 1)..nameEnd]
                .All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value.Length == "sha256:".Length + 64
            && value["sha256:".Length..].All(
                character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
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
