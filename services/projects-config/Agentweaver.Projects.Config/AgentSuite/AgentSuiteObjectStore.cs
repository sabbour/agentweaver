using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Retains original AgentSuite blobs by native digest and one immutable import record per project and
/// source identity. Writes are create-only; an existing object is verified, never overwritten.
/// </summary>
internal sealed class AgentSuiteObjectStore(IObjectStore objectStore)
{
    private const int MaximumRecordBytes = 4 * 1024 * 1024;
    private const int MaximumBlobBytes = AgentSuiteValidator.MaxArchiveBytes;

    // Blobs are shared across projects: the same digest always means the same bytes, so one copy is enough.
    public static ObjectKey BlobKey(string digest) => new($"agentsuite/blobs/sha256/{Hex(digest)}");

    // One record per project and source identity. Hex() also rejects malformed digests before they reach a key.
    public static ObjectKey RecordKey(string projectId, string sourceIdentity) =>
        new($"agentsuite/projects/{projectId}/imports/{Hex(sourceIdentity)}.json");

    /// <summary>Effective IDs contain ':' and suite names, so the index key is their SHA-256.</summary>
    public static ObjectKey AgentIndexKey(string projectId, string effectiveAgentId) =>
        new($"agentsuite/projects/{projectId}/agents/{Hex(Digest(Encoding.UTF8.GetBytes(effectiveAgentId)))}.json");

    /// <summary>
    /// Points each effective agent ID at its source identity, create-only. An effective ID that already points at
    /// another source (a 12-hex prefix collision) is a conflict; the existing mapping is never replaced.
    /// </summary>
    public async Task WriteAgentIndexAsync(ProjectAgentSuiteImport record, CancellationToken cancellationToken)
    {
        foreach (var agent in record.Agents)
        {
            // Read first: the object store has no compare-and-set, so check for an existing mapping ourselves.
            var existing = await ReadAgentIndexAsync(record.ProjectId, agent.EffectiveAgentId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                // Same source: an earlier attempt already wrote this entry, so skip it (idempotent retry).
                if (!string.Equals(existing, record.SourceIdentity, StringComparison.Ordinal))
                    throw ProjectConfigException.Conflict(
                        "agentsuite_identity_conflict",
                        $"Agent '{agent.EffectiveAgentId}' already belongs to another import. It was not changed.");
                continue;
            }
            // Canonical JSON makes the entry bytes identical for identical input.
            var bytes = CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(
                new AgentIndexEntry(agent.EffectiveAgentId, record.SourceIdentity)));
            using var stream = new MemoryStream(bytes, writable: false);
            await objectStore.WriteAsync(AgentIndexKey(record.ProjectId, agent.EffectiveAgentId), stream, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Returns the source identity that owns an effective agent ID, or null when none does.</summary>
    public async Task<string?> ReadAgentIndexAsync(
        string projectId,
        string effectiveAgentId,
        CancellationToken cancellationToken)
    {
        // An index entry is tiny; 4 KiB is a generous cap that stops a damaged object from being read in full.
        var bytes = await ReadAsync(AgentIndexKey(projectId, effectiveAgentId), 4096, cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
            return null;
        AgentIndexEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<AgentIndexEntry>(bytes);
        }
        catch (JsonException)
        {
            // Stored data we cannot parse is a server-side integrity problem (500), not a caller error.
            throw IntegrityError("A stored AgentSuite agent index entry is invalid.");
        }
        // The key is a hash, so confirm the entry really belongs to the requested effective ID.
        if (entry is null || !string.Equals(entry.EffectiveAgentId, effectiveAgentId, StringComparison.Ordinal))
            throw IntegrityError("A stored AgentSuite agent index entry does not match its key.");
        // Validate the stored source identity; Hex() throws if it is not a well-formed sha256 digest.
        _ = Hex(entry.SourceIdentity);
        return entry.SourceIdentity;
    }

    private sealed record AgentIndexEntry(string EffectiveAgentId, string SourceIdentity);

    /// <summary>Stores each blob once. An existing blob must hash to its key.</summary>
    public async Task WriteBlobsAsync(IEnumerable<AgentSuiteBlob> blobs, CancellationToken cancellationToken)
    {
        foreach (var blob in blobs)
        {
            // Never store bytes under a digest they do not match; that would poison the shared blob key.
            if (!string.Equals(Digest(blob.Data.Span), blob.Digest, StringComparison.Ordinal))
                throw new InvalidOperationException("A retained AgentSuite blob does not match its digest.");
            var key = BlobKey(blob.Digest);
            var existing = await ReadAsync(key, MaximumBlobBytes, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                // Already stored (by this or another import). Verify the stored bytes, then skip the write.
                if (!string.Equals(Digest(existing), blob.Digest, StringComparison.Ordinal))
                    throw IntegrityError("A stored AgentSuite blob does not match its digest.");
                continue;
            }
            // Store the original bytes unchanged.
            using var stream = new MemoryStream(blob.Data.ToArray(), writable: false);
            await objectStore.WriteAsync(key, stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one retained blob and verifies that its bytes still match the native digest.</summary>
    public async Task<byte[]> ReadBlobAsync(string digest, CancellationToken cancellationToken)
    {
        // Read through the same bounded path used for records so a damaged object cannot be returned unchecked.
        var bytes = await ReadAsync(BlobKey(digest), MaximumBlobBytes, cancellationToken).ConfigureAwait(false)
            ?? throw IntegrityError("A retained AgentSuite blob is missing.");
        if (!string.Equals(Digest(bytes), digest, StringComparison.Ordinal))
            throw IntegrityError("A retained AgentSuite blob does not match its digest.");
        return bytes;
    }

    /// <summary>
    /// Writes the record if absent. An existing record is returned only when it is byte-identical; the same
    /// source with different bindings or blueprint data is a conflict and the stored record is kept.
    /// </summary>
    public async Task<ProjectAgentSuiteImport> WriteRecordAsync(
        ProjectAgentSuiteImport record,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var key = RecordKey(record.ProjectId, record.SourceIdentity);
        // Canonical JSON gives one exact byte form, so "same record" can be checked with a byte comparison.
        var bytes = CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(record, options));
        var existing = await ReadAsync(key, MaximumRecordBytes, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Read through the checked path so a damaged stored record fails as an integrity error.
            var stored = await ReadRecordAsync(record.ProjectId, record.SourceIdentity, options, cancellationToken)
                .ConfigureAwait(false);
            // Any byte difference means different bindings or blueprint data for the same source: refuse (409).
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw ProjectConfigException.Conflict(
                    "agentsuite_import_conflict",
                    "This source was already imported with different tool connections or blueprint data.");
            // Identical record: return the stored copy (idempotent repeat).
            return stored!;
        }

        // No record yet: create it.
        using var stream = new MemoryStream(bytes, writable: false);
        await objectStore.WriteAsync(key, stream, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>Reads and checks a stored record. Returns null when it does not exist.</summary>
    public async Task<ProjectAgentSuiteImport?> ReadRecordAsync(
        string projectId,
        string sourceIdentity,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(RecordKey(projectId, sourceIdentity), MaximumRecordBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
            return null;
        ProjectAgentSuiteImport? record;
        try
        {
            // Records are always written as canonical JSON, so any other form means the object was altered.
            if (!CanonicalJson.IsCanonical(bytes))
                throw IntegrityError("The stored AgentSuite import record is not canonical JSON.");
            record = JsonSerializer.Deserialize<ProjectAgentSuiteImport>(bytes, options);
        }
        catch (JsonException)
        {
            throw IntegrityError("The stored AgentSuite import record is invalid.");
        }
        // The record must match the version and the key it was read from, so one project cannot see another's.
        if (record is null ||
            record.RecordVersion != AgentSuiteImportPlanner.RecordVersion ||
            !string.Equals(record.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(record.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
            throw IntegrityError("The stored AgentSuite import record does not match its key.");
        return record;
    }

    // Reads a whole object into memory with a size cap. Returns null when the object does not exist.
    private async Task<byte[]?> ReadAsync(ObjectKey key, int limit, CancellationToken cancellationToken)
    {
        var read = await objectStore.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (read is null)
            return null;
        using (read)
        {
            // Check the declared length first, then count the actual bytes too, in case the length is wrong.
            if (read.Length > limit)
                throw IntegrityError("A stored AgentSuite object exceeds its size limit.");
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit)
                    throw IntegrityError("A stored AgentSuite object exceeds its size limit.");
                output.Write(buffer, 0, count);
            }
            // A short read means the object was truncated or changed while reading.
            if (output.Length != read.Length)
                throw IntegrityError("A stored AgentSuite object has an inconsistent length.");
            return output.ToArray();
        }
    }

    // Returns the digest in the same "sha256:<lowercase hex>" form the suite uses.
    public static string Digest(ReadOnlySpan<byte> data) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));

    // Returns the 64 hex characters after "sha256:". Anything else throws, so this key part cannot hold '/' or '..'.
    private static string Hex(string digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0
            ? digest[7..]
            : throw new ArgumentException("The digest must be sha256 with 64 lowercase hex characters.", nameof(digest));

    // Stored data that fails a check is a server-side problem, so it maps to HTTP 500.
    private static ProjectConfigException IntegrityError(string message) =>
        new("agentsuite_integrity_error", message, StatusCodes.Status500InternalServerError);
}
