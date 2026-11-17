using System.Security.Cryptography;
using System.Text.Json;

namespace Agentweaver.Cli;

internal sealed record PolicyApplyJournalEntry(
    string Path,
    string IdempotencyKey,
    string CorrelationId);

internal sealed class PolicyApplyJournalException(string message) : IOException(message);

internal static class PolicyApplyJournal
{
    private const int MaximumRecordBytes = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<PolicyApplyJournalEntry> PrepareAsync(
        CliIdentity identity,
        long? expectedRevision,
        string artifactHash,
        string reason,
        string? directoryOverride,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string directory;
        try
        {
            directory = Path.GetFullPath(directoryOverride ?? DefaultDirectory());
        }
        catch (PolicyApplyJournalException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new PolicyApplyJournalException(
                "The per-user CLI state directory is unavailable; no policy request was sent.");
        }
        var targetHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            gateway = identity.Target.Gateway.AbsoluteUri,
            issuer = identity.Target.Issuer.AbsoluteUri,
            identity.Target.ClientId,
            identity.Target.Audience,
            identity.Target.TenantId,
        }, JsonOptions));
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            expectedRevision,
            artifactHash,
            reason,
        }, JsonOptions));
        var path = Path.Combine(directory, $"{targetHash}-{fingerprint}.json");

        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            if (File.Exists(path))
                return await ReadAsync(path, fingerprint, ct).ConfigureAwait(false);

            var record = new JournalRecord(
                Version: 1,
                Fingerprint: fingerprint,
                IdempotencyKey: Guid.NewGuid().ToString("N"),
                CorrelationId: Guid.NewGuid().ToString("N"));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            FileStream stream;
            try
            {
                stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(path))
            {
                return await ReadAsync(path, fingerprint, ct).ConfigureAwait(false);
            }

            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    TryDelete(path);
                    throw;
                }
            }

            return new PolicyApplyJournalEntry(path, record.IdempotencyKey, record.CorrelationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PolicyApplyJournalException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or JsonException)
        {
            throw new PolicyApplyJournalException(
                "The local policy apply recovery record is unavailable; no policy request was sent.");
        }
    }

    public static void Complete(string path) => TryDelete(path);

    private static async Task<PolicyApplyJournalEntry> ReadAsync(
        string path,
        string expectedFingerprint,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumRecordBytes)
            throw InvalidRecord();
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
            !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) ||
            versionNumber != 1 ||
            !root.TryGetProperty("fingerprint", out var fingerprint) ||
            fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString() != expectedFingerprint ||
            !root.TryGetProperty("idempotencyKey", out var idempotencyKey) ||
            idempotencyKey.ValueKind != JsonValueKind.String || !ValidId(idempotencyKey.GetString()) ||
            !root.TryGetProperty("correlationId", out var correlationId) ||
            correlationId.ValueKind != JsonValueKind.String || !ValidId(correlationId.GetString()))
            throw InvalidRecord();

        return new PolicyApplyJournalEntry(path, idempotencyKey.GetString()!, correlationId.GetString()!);
    }

    private static bool ValidId(string? value) =>
        value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string DefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
                throw new PolicyApplyJournalException("The per-user CLI state directory is unavailable.");
            return Path.Combine(local, "Agentweaver", "cli-policy-apply");
        }
        if (OperatingSystem.IsMacOS())
            return Path.Combine(UserProfileDirectory(),
                "Library", "Application Support", "Agentweaver", "cli-policy-apply");

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var state = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(UserProfileDirectory(), ".local", "state");
        return Path.Combine(state, "agentweaver", "cli-policy-apply");
    }

    private static string UserProfileDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new PolicyApplyJournalException("The per-user CLI state directory is unavailable.");
        return path;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PolicyApplyJournalException InvalidRecord() =>
        new("The local policy apply recovery record is invalid; refusing to send the request.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record JournalRecord(int Version, string Fingerprint, string IdempotencyKey, string CorrelationId);
}
