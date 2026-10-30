using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Agentweaver.Cli;

internal static class OciArchiveMeasurement
{
    private const long MaxRetainedEntryBytes = 2 * 1024 * 1024;
    private const string OciManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string OciConfigMediaType = "application/vnd.oci.image.config.v1+json";
    private const string OciLayerMediaType = "application/vnd.oci.image.layer.v1.tar+gzip";

    public static async Task<JsonObject> MeasureAsync(
        string archivePath,
        string sourceSha,
        string sourceTree,
        CancellationToken ct)
    {
        if (!AcceptanceDeploymentEvidenceProvider.IsSha(sourceSha) || !AcceptanceDeploymentEvidenceProvider.IsSha(sourceTree))
            throw new InvalidDataException("An exact clean Git source SHA and tree are required.");

        JsonObject index = JsonNode.Parse(
            (await ReadEntryAsync(archivePath, "index.json", retain: true, ct).ConfigureAwait(false)).Content!)
            as JsonObject ?? throw new InvalidDataException("The OCI archive index is malformed.");
        if (!ApiAcceptanceSupport.TryLong(index["schemaVersion"], out long indexSchema) || indexSchema != 2
            || index["manifests"] is not JsonArray { Count: 1 } manifests
            || manifests[0] is not JsonObject descriptor)
        {
            throw new InvalidDataException("The OCI archive must contain one exact platform manifest, not a multi-platform tag.");
        }

        string manifestDigest = ApiAcceptanceSupport.RequireString(descriptor, "digest", "OCI manifest digest");
        long manifestSize = RequireSize(descriptor, "size", "OCI manifest descriptor");
        if (!AcceptanceDeploymentEvidenceProvider.IsDigest(manifestDigest))
            throw new InvalidDataException("The OCI manifest descriptor is invalid.");
        MeasuredEntry measuredManifest = await ReadBlobAsync(archivePath, manifestDigest, retain: true, ct)
            .ConfigureAwait(false);
        if (measuredManifest.Size != manifestSize || measuredManifest.Digest != manifestDigest)
            throw new InvalidDataException("The OCI platform manifest bytes do not match their descriptor.");

        JsonObject manifest = JsonNode.Parse(measuredManifest.Content!) as JsonObject
            ?? throw new InvalidDataException("The OCI platform manifest is malformed.");
        if (!ApiAcceptanceSupport.TryLong(manifest["schemaVersion"], out long manifestSchema) || manifestSchema != 2
            || ApiAcceptanceSupport.GetString(manifest, "mediaType") != OciManifestMediaType
            || manifest["layers"] is not JsonArray { Count: > 0 } layers
            || manifest["config"] is not JsonObject configDescriptor
            || ApiAcceptanceSupport.GetString(configDescriptor, "mediaType") != OciConfigMediaType
            || layers.OfType<JsonObject>().Count() != layers.Count
            || layers.OfType<JsonObject>().Any(layer =>
                ApiAcceptanceSupport.GetString(layer, "mediaType") != OciLayerMediaType))
        {
            throw new InvalidDataException("An OCI image with gzip-compressed layers and an explicit config is required.");
        }

        long compressedPullBytes = manifestSize;
        Dictionary<string, long> blobSizes = new(StringComparer.Ordinal);
        MeasuredEntry? measuredConfig = null;
        foreach (JsonObject blob in new[] { configDescriptor }.Concat(layers.OfType<JsonObject>()))
        {
            string digest = ApiAcceptanceSupport.RequireString(blob, "digest", "OCI blob digest");
            long declaredSize = RequireSize(blob, "size", "OCI blob descriptor");
            if (!AcceptanceDeploymentEvidenceProvider.IsDigest(digest))
                throw new InvalidDataException("An OCI blob descriptor is invalid or conflicting.");
            if (blobSizes.TryGetValue(digest, out long existingSize))
            {
                if (existingSize != declaredSize)
                    throw new InvalidDataException("An OCI blob descriptor is invalid or conflicting.");
                continue;
            }
            blobSizes.Add(digest, declaredSize);

            bool isConfig = ReferenceEquals(blob, configDescriptor);
            MeasuredEntry measured = await ReadBlobAsync(archivePath, digest, retain: isConfig, ct)
                .ConfigureAwait(false);
            if (measured.Size != declaredSize || measured.Digest != digest)
                throw new InvalidDataException("An OCI blob does not match its exact compressed size and digest.");
            if (!isConfig && !measured.IsGzip)
                throw new InvalidDataException("The declared compressed OCI layer is not a gzip blob.");
            if (isConfig)
                measuredConfig = measured;
            compressedPullBytes = checked(compressedPullBytes + declaredSize);
            if (compressedPullBytes > 9_007_199_254_740_991L)
                throw new InvalidDataException("The measured OCI pull-byte total exceeds the exact integer range.");
        }

        JsonObject imageConfig = JsonNode.Parse(measuredConfig?.Content!)
            as JsonObject ?? throw new InvalidDataException("The OCI image config is malformed.");
        JsonObject config = imageConfig["config"] as JsonObject ?? [];
        JsonObject labels = config["Labels"] as JsonObject ?? [];
        if (ApiAcceptanceSupport.GetString(imageConfig, "os") != "linux"
            || ApiAcceptanceSupport.GetString(imageConfig, "architecture") != "amd64"
            || ApiAcceptanceSupport.GetString(config, "User") != "1654:1654"
            || ApiAcceptanceSupport.GetString(labels, "org.opencontainers.image.revision") != sourceSha
            || ApiAcceptanceSupport.GetString(labels, "io.agentweaver.source-tree") != sourceTree
            || imageConfig["config"]?["Entrypoint"] is not JsonArray entrypoint
            || entrypoint.Count != 2
            || entrypoint[0]?.GetValue<string>() != "dotnet"
            || entrypoint[1]?.GetValue<string>() != "Agentweaver.AgentHost.dll")
        {
            throw new InvalidDataException("The image platform, non-root owner, executable, or clean-source labels do not match.");
        }

        JsonObject image = new()
        {
            ["digest"] = manifestDigest,
            ["configDigest"] = ApiAcceptanceSupport.GetString(configDescriptor, "digest"),
            ["platform"] = "linux/amd64",
            ["compressedPullBytes"] = compressedPullBytes,
        };
        if (ApiAcceptanceSupport.GetString(labels, "org.opencontainers.image.version") is string version)
            image["version"] = version;
        return new JsonObject
        {
            ["kind"] = "agenthost-image",
            ["schemaVersion"] = 1L,
            ["sourceSha"] = sourceSha,
            ["sourceTree"] = sourceTree,
            ["image"] = image,
            ["measurement"] = "platform-manifest plus unique config and gzip-layer blob bytes",
        };
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
    }

    private static long RequireSize(JsonObject descriptor, string property, string description)
    {
        if (!ApiAcceptanceSupport.TryLong(descriptor[property], out long size)
            || size is < 1 or > 9_007_199_254_740_991L)
            throw new InvalidDataException($"The {description} is invalid.");
        return size;
    }

    private static async Task<MeasuredEntry> ReadBlobAsync(
        string archivePath,
        string digest,
        bool retain,
        CancellationToken ct) =>
        await ReadEntryAsync(archivePath, $"blobs/sha256/{digest[7..]}", retain, ct).ConfigureAwait(false);

    private static async Task<MeasuredEntry> ReadEntryAsync(
        string archivePath,
        string path,
        bool retain,
        CancellationToken ct)
    {
        await using FileStream archive = new(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        using TarReader reader = new(archive, leaveOpen: true);
        MeasuredEntry? found = null;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (!string.Equals(entry.Name, path, StringComparison.Ordinal))
                continue;
            if (found is not null)
                throw new InvalidDataException($"The OCI archive contains duplicate entries for {path}.");
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is null)
                throw new InvalidDataException($"The OCI archive entry {path} is not a regular file.");

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using MemoryStream? content = retain ? new MemoryStream() : null;
            byte[] buffer = new byte[81920];
            byte[] prefix = new byte[3];
            int prefixLength = 0;
            long size = 0;
            while (true)
            {
                int read = await entry.DataStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                size = checked(size + read);
                if (retain && size > MaxRetainedEntryBytes)
                    throw new InvalidDataException($"The bounded OCI archive entry {path} exceeds 2 MiB.");
                hash.AppendData(buffer, 0, read);
                if (prefixLength < prefix.Length)
                {
                    int copy = Math.Min(prefix.Length - prefixLength, read);
                    buffer.AsSpan(0, copy).CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += copy;
                }
                content?.Write(buffer, 0, read);
            }
            found = new MeasuredEntry(
                size,
                $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}",
                prefixLength == 3 && prefix[0] == 0x1f && prefix[1] == 0x8b && prefix[2] == 8,
                content?.ToArray());
        }
        if (found is not null)
            return found;
        throw new InvalidDataException($"Cannot read the bounded OCI archive entry {path}.");
    }

    private sealed record MeasuredEntry(long Size, string Digest, bool IsGzip, byte[]? Content);
}
