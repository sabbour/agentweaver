using System.Text.Json;
using System.Threading;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Builds MAF <see cref="FileSystemJsonCheckpointStore"/> instances defensively so the API ALWAYS
/// boots. Two distinct startup hazards are handled:
/// <list type="number">
///   <item>
///     <b>Corrupt index.</b> MAF parses the store's <c>index.jsonl</c> one JSON object per line at
///     construction time, so a single blank or partially-written line (e.g. from an interrupted
///     append) throws and would otherwise brick the API ("Could not load store ... Index corrupted").
///     This factory first sanitizes the index (dropping blank/unparseable lines, after backing it up);
///     if construction still fails it quarantines the index so the API starts with a fresh one.
///   </item>
///   <item>
///     <b>Multi-writer lock contention.</b> <see cref="FileSystemJsonCheckpointStore"/> takes an
///     EXCLUSIVE process lock on the checkpoint directory. When the API runs <c>replicas:2</c> with a
///     SHARED RWX volume (HOME on Azure Files), only one pod can hold the lock — the other pod's
///     constructor throws "already in use by another process". This is NOT corruption, so we must not
///     quarantine the index. Instead we retry briefly (to ride out transient mid-write locks during a
///     rolling update), then fall back to a per-pod checkpoint sub-directory under the same volume so
///     the replica gets its own writable store and boots. Cross-replica checkpoint resume was never
///     actually available under the single-writer file lock; the long-term fix is a DB-backed store.
///   </item>
/// </list>
/// <see cref="Create"/> is guaranteed never to throw: the absolute last resort is a unique per-pod
/// temp directory.
/// </summary>
public static class ResilientCheckpointStore
{
    /// <summary>Number of times to try opening the SHARED store before falling back to a per-pod dir.</summary>
    private const int SharedOpenAttempts = 3;

    /// <summary>Backoff between shared-store open attempts (rides out transient mid-write locks).</summary>
    private static readonly TimeSpan SharedOpenBackoff = TimeSpan.FromMilliseconds(250);

    /// <summary>Default store opener. Overridable in tests to simulate ctor failures.</summary>
    private static FileSystemJsonCheckpointStore DefaultOpener(DirectoryInfo dir) => new(dir);

    public static FileSystemJsonCheckpointStore Create(string checkpointDir, ILogger logger)
        => Create(checkpointDir, logger, DefaultOpener);

    internal static FileSystemJsonCheckpointStore Create(
        string checkpointDir,
        ILogger logger,
        Func<DirectoryInfo, FileSystemJsonCheckpointStore> openStore)
    {
        Directory.CreateDirectory(checkpointDir);
        var indexPath = Path.Combine(checkpointDir, "index.jsonl");
        SanitizeIndex(indexPath, logger);

        for (var attempt = 1; attempt <= SharedOpenAttempts; attempt++)
        {
            try
            {
                return openStore(new DirectoryInfo(checkpointDir));
            }
            catch (Exception ex) when (IsLockContention(ex))
            {
                // Another replica/process owns the shared store. NOT corruption — never quarantine here.
                if (attempt < SharedOpenAttempts)
                {
                    logger.LogDebug(
                        "Checkpoint store {Path} is locked by another process (attempt {Attempt}/{Max}); retrying after {Backoff}ms.",
                        checkpointDir, attempt, SharedOpenAttempts, SharedOpenBackoff.TotalMilliseconds);
                    Thread.Sleep(SharedOpenBackoff);
                    continue;
                }

                logger.LogDebug(ex, "Checkpoint store {Path} still locked after {Max} attempts.", checkpointDir, SharedOpenAttempts);
                return CreatePerPodStore(checkpointDir, logger, "lock contention", openStore);
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                // Shared volume is not readable/writable by this pod (e.g. RWX Azure Files perms). This is
                // EXPECTED in some deployments and is NOT corruption — do not quarantine, do not log a
                // stacktrace at fail. Fall back quietly to a per-pod directory; CreatePerPodStore emits a
                // single concise warn describing where checkpoints actually landed.
                logger.LogDebug(ex, "Checkpoint store {Path} is not accessible (permission denied).", checkpointDir);
                return CreatePerPodStore(checkpointDir, logger, "permission denied", openStore);
            }
            catch (Exception ex)
            {
                // Genuine index corruption: quarantine the index and retry once on the SHARED dir.
                logger.LogError(ex,
                    "Checkpoint index at {Path} is unrecoverable; quarantining it so the API can start.",
                    indexPath);
                QuarantineIndex(indexPath, logger);
                try
                {
                    return openStore(new DirectoryInfo(checkpointDir));
                }
                catch (Exception retryEx) when (IsLockContention(retryEx))
                {
                    logger.LogDebug(retryEx,
                        "Checkpoint store {Path} is locked after index quarantine; falling back to a per-pod directory.",
                        checkpointDir);
                    return CreatePerPodStore(checkpointDir, logger, "lock contention", openStore);
                }
                catch (Exception retryEx) when (IsAccessDenied(retryEx))
                {
                    logger.LogDebug(retryEx,
                        "Checkpoint store {Path} is not accessible after index quarantine; falling back to a per-pod directory.",
                        checkpointDir);
                    return CreatePerPodStore(checkpointDir, logger, "permission denied", openStore);
                }
                catch (Exception retryEx)
                {
                    logger.LogError(retryEx,
                        "Checkpoint store {Path} still failed after index quarantine; falling back to a per-pod directory.",
                        checkpointDir);
                    return CreatePerPodStore(checkpointDir, logger, "index still unreadable", openStore);
                }
            }
        }

        // Unreachable in practice (the loop always returns), but keeps the API booting no matter what.
        return CreatePerPodStore(checkpointDir, logger, "shared store unavailable", openStore);
    }

    /// <summary>
    /// Opens a checkpoint store in a per-pod sub-directory under the same volume, keyed by a unique
    /// per-pod id (<c>POD_NAME</c>/<c>HOSTNAME</c>, else a GUID), so a replica that lost the shared
    /// lock — or cannot access the shared volume — still gets its own writable file store. Emits exactly
    /// ONE warn line describing the final location and is guaranteed not to throw: the last resort is a
    /// unique temp directory, which cannot be locked or permission-blocked by another process.
    /// </summary>
    private static FileSystemJsonCheckpointStore CreatePerPodStore(
        string checkpointDir,
        ILogger logger,
        string reason,
        Func<DirectoryInfo, FileSystemJsonCheckpointStore> openStore)
    {
        var podId = ResolvePodId();
        var perPodDir = Path.Combine(checkpointDir, "replicas", podId);
        try
        {
            Directory.CreateDirectory(perPodDir);
            SanitizeIndex(Path.Combine(perPodDir, "index.jsonl"), logger);
            var store = openStore(new DirectoryInfo(perPodDir));
            logger.LogWarning(
                "Shared checkpoint store {Shared} not usable ({Reason}); using per-pod directory {Dir}. "
                + "Checkpoints are durable for this pod but not shared across replicas (cross-replica resume needs a DB-backed store).",
                checkpointDir, reason, perPodDir);
            return store;
        }
        catch (Exception ex)
        {
            // Absolute last resort: a fresh unique temp directory. The API MUST start. This stays a
            // single warn (not fail) — the per-pod warn above was never emitted because we threw first.
            var tempDir = Path.Combine(Path.GetTempPath(), $"agentweaver-checkpoints-{podId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var store = openStore(new DirectoryInfo(tempDir));
            logger.LogWarning(
                "Shared checkpoint store {Shared} not usable ({Reason}) and per-pod directory was not writable; "
                + "using temporary directory {Temp}. Checkpoints will NOT persist across pod restarts.",
                checkpointDir, reason, tempDir);
            logger.LogDebug(ex, "Per-pod checkpoint directory {Dir} could not be opened.", perPodDir);
            return store;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the multi-writer lock-contention failure (the
    /// <see cref="FileSystemJsonCheckpointStore"/> ctor reports "already in use by another process"),
    /// as opposed to an index-corruption parse failure.
    /// </summary>
    private static bool IsLockContention(Exception ex)
        => ex.Message.Contains("already in use by another process", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner exception) is a filesystem permission failure —
    /// an <see cref="UnauthorizedAccessException"/> or an <see cref="IOException"/> reporting permission
    /// denied. The MAF ctor wraps these as <c>InvalidOperationException("...Index corrupted") ---&gt;
    /// UnauthorizedAccessException</c>, so we must walk the inner-exception chain. This is an EXPECTED
    /// fallback condition (shared-volume perms), NOT genuine index corruption.
    /// </summary>
    private static bool IsAccessDenied(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException) return true;
            if (e is IOException io && io.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Resolves a filesystem-safe, per-pod id from the pod's identity env vars, or a GUID.</summary>
    private static string ResolvePodId()
    {
        var id = Environment.GetEnvironmentVariable("POD_NAME")
            ?? Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(id))
        {
            return $"pod-{Guid.NewGuid():N}";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? $"pod-{Guid.NewGuid():N}" : safe;
    }

    /// <summary>
    /// Moves a (genuinely corrupt) index aside so the store can start fresh. The destination name is
    /// made unique per pod and per call (pod id + timestamp + GUID) and uses overwrite semantics, so it
    /// can NEVER collide with a prior quarantine (the old fixed <c>.corrupt.&lt;unixSeconds&gt;</c> name threw
    /// <c>IOException: already exists</c> on rapid restarts within the same second). Never throws.
    /// </summary>
    internal static void QuarantineIndex(string indexPath, ILogger logger)
    {
        if (!File.Exists(indexPath)) return;
        var quarantine = $"{indexPath}.corrupt.{ResolvePodId()}.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{Guid.NewGuid():N}";
        try { File.Move(indexPath, quarantine, overwrite: true); }
        catch (Exception moveEx)
        {
            // A quarantine failure must NOT cascade into another fail log — the caller already falls
            // back to a per-pod/temp store regardless. Record at warn without a fail-level stacktrace.
            logger.LogWarning("Could not quarantine corrupt checkpoint index {Path}: {Reason}", indexPath, moveEx.Message);
        }
    }

    /// <summary>
    /// Removes blank/whitespace-only and unparseable lines from the checkpoint <c>index.jsonl</c>,
    /// backing up the original first. A no-op when the index is absent or already clean.
    /// </summary>
    private static void SanitizeIndex(string indexPath, ILogger logger)
    {
        if (!File.Exists(indexPath)) return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(indexPath);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            // Expected when the shared volume is not readable by this pod — the caller will fall back to
            // a per-pod store and emit a single warn there. Keep this quiet (Debug, no stacktrace at warn).
            logger.LogDebug(ex, "Checkpoint index {Path} not accessible for sanitization (permission denied).", indexPath);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read checkpoint index {Path} for sanitization.", indexPath);
            return;
        }

        var kept = new List<string>(lines.Length);
        var dropped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) { dropped++; continue; }
            try
            {
                using var _ = JsonDocument.Parse(line);
                kept.Add(line);
            }
            catch (JsonException)
            {
                dropped++;
                var snippet = line.Length > 80 ? line[..80] : line;
                logger.LogWarning("Dropping corrupt checkpoint index line: {Snippet}", snippet);
            }
        }

        if (dropped == 0) return;

        try
        {
            File.Copy(indexPath, $"{indexPath}.bak.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", overwrite: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not back up checkpoint index {Path} before repair.", indexPath);
        }

        try
        {
            File.WriteAllLines(indexPath, kept);
            logger.LogWarning(
                "Repaired checkpoint index {Path}: dropped {Dropped} blank/corrupt line(s), kept {Kept}.",
                indexPath, dropped, kept.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rewrite repaired checkpoint index {Path}.", indexPath);
        }
    }
}
