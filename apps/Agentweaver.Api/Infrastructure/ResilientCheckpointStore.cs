using System.Text.Json;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Builds MAF <see cref="FileSystemJsonCheckpointStore"/> instances defensively. MAF parses the
/// store's <c>index.jsonl</c> one JSON object per line at construction time, so a single blank or
/// partially-written line (e.g. from an interrupted append) throws and would otherwise brick the
/// entire API at startup ("Could not load store ... Index corrupted"). This factory first sanitizes
/// the index (dropping blank/unparseable lines, after backing it up); if construction still fails it
/// quarantines the index so the API can start with a fresh one rather than crash-loop. Durability of
/// NEW runs is preserved; only unreadable historical entries are set aside.
/// </summary>
public static class ResilientCheckpointStore
{
    public static FileSystemJsonCheckpointStore Create(string checkpointDir, ILogger logger)
    {
        Directory.CreateDirectory(checkpointDir);
        var indexPath = Path.Combine(checkpointDir, "index.jsonl");
        SanitizeIndex(indexPath, logger);
        try
        {
            return new FileSystemJsonCheckpointStore(new DirectoryInfo(checkpointDir));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Checkpoint index at {Path} is unrecoverable; quarantining it so the API can start.",
                indexPath);
            if (File.Exists(indexPath))
            {
                var quarantine = $"{indexPath}.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                try { File.Move(indexPath, quarantine); }
                catch (Exception moveEx)
                {
                    logger.LogError(moveEx, "Failed to quarantine corrupt checkpoint index {Path}.", indexPath);
                }
            }
            return new FileSystemJsonCheckpointStore(new DirectoryInfo(checkpointDir));
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
