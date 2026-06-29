using System.Text.Json.Serialization;
using Agentweaver.Api.Backlog;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// A node in the project workspace file tree returned by
/// <c>GET /api/projects/{id}/workspace/files</c>. Directories carry a <see cref="Children"/> list;
/// files set <see cref="Children"/> to null.
/// </summary>
public sealed record WorkspaceFileNode(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("is_directory")] bool IsDirectory,
    [property: JsonPropertyName("children")] IReadOnlyList<WorkspaceFileNode>? Children);

/// <summary>Request body for <c>POST /api/projects/{id}/backlog/decompose</c>.</summary>
public sealed record DecomposeRequest(
    /// <summary>
    /// Workspace-relative path of the markdown file to decompose. When <c>null</c>, the endpoint
    /// uses the confirmed outcome spec for the run identified by <see cref="RunId"/>.
    /// </summary>
    [property: JsonPropertyName("file_path")] string? FilePath,
    /// <summary>
    /// Coordinator run id whose confirmed outcome spec should be decomposed. Required when
    /// <see cref="FilePath"/> is <c>null</c>; ignored when <see cref="FilePath"/> is provided.
    /// </summary>
    [property: JsonPropertyName("run_id")] string? RunId,
    /// <summary>
    /// When <c>true</c>, creates Backlog tasks for all non-duplicate proposed items.
    /// When <c>false</c>, returns a preview without persisting anything.
    /// </summary>
    [property: JsonPropertyName("confirm")] bool Confirm);

/// <summary>A proposed backlog item in the decomposition preview or confirm response.</summary>
public sealed record ProposedBacklogItem(
    /// <summary>Extracted task title (imperative verb phrase, max 80 chars).</summary>
    [property: JsonPropertyName("title")] string Title,
    /// <summary>Optional brief description extracted from the document.</summary>
    [property: JsonPropertyName("description")] string? Description,
    /// <summary>
    /// True when a Backlog task with the same title already exists for the same project and
    /// source file. Duplicate items are skipped on confirm.
    /// </summary>
    [property: JsonPropertyName("already_exists")] bool AlreadyExists);

/// <summary>Response body for <c>POST /api/projects/{id}/backlog/decompose</c>.</summary>
public sealed record DecomposeResponse(
    /// <summary>Proposed items (at most 50). Each carries an <c>already_exists</c> flag.</summary>
    [property: JsonPropertyName("proposed_items")] IReadOnlyList<ProposedBacklogItem> ProposedItems,
    /// <summary>True when the agent extracted more than 50 items and the list was truncated.</summary>
    [property: JsonPropertyName("was_capped")] bool WasCapped,
    /// <summary>Total items extracted before applying the 50-item cap.</summary>
    [property: JsonPropertyName("total_found")] int TotalFound);

/// <summary>
/// Spec-to-backlog decomposition endpoints (Feature 014).
/// <list type="bullet">
///   <item><c>GET /api/projects/{id}/workspace/files</c> — sandbox-safe workspace file tree.</item>
///   <item>
///     <c>POST /api/projects/{id}/backlog/decompose</c> — agent decomposition, 50-item hard cap,
///     idempotency by (project_id, source_file_path, title).
///   </item>
/// </list>
/// </summary>
public static class BacklogDecomposeEndpoints
{
    /// <summary>Registers both Feature 014 endpoints on the application.</summary>
    public static void MapBacklogDecomposeEndpoints(this WebApplication app)
    {
        // GET /api/projects/{id}/workspace/files — workspace file tree (sandbox-scoped)
        app.MapGet("/api/projects/{id}/workspace/files", async (
            string id,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct);
            if (project is null) return Results.NotFound();

            var workspaceRoot = project.WorkingDirectory;
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return Results.Ok(Array.Empty<WorkspaceFileNode>());

            var tree = BuildTree(workspaceRoot, workspaceRoot);
            return Results.Ok(tree);
        }).WithTags("Backlog");

        // POST /api/projects/{id}/backlog/decompose — spec-to-backlog decomposition
        app.MapPost("/api/projects/{id}/backlog/decompose", async (
            string id,
            DecomposeRequest request,
            IProjectStore projectStore,
            IBacklogTaskStore backlogStore,
            BacklogDecomposeService decomposeService,
            MemoryDbContext db,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct);
            if (project is null) return Results.NotFound();

            string fileContent;
            string normalizedPath;

            if (request.FilePath is null)
            {
                // Outcome-spec mode: decompose the confirmed spec for the supplied coordinator run.
                if (string.IsNullOrWhiteSpace(request.RunId))
                    return Results.BadRequest(new { error = "run_id is required when file_path is not provided." });

                var spec = await db.OutcomeSpecs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CoordinatorRunId == request.RunId, ct);

                if (spec is null)
                    return Results.NotFound(new { error = "Outcome spec not found for the specified run." });

                fileContent = BuildOutcomeSpecMarkdown(spec);
                // Virtual source path used for idempotency — uniquely identifies this spec per run.
                normalizedPath = $"__outcome-spec__/{request.RunId}";
            }
            else
            {
                // File mode: validate and read from the project workspace.
                if (!EndpointHelpers.TryValidateRelativePath(request.FilePath, out normalizedPath))
                    return Results.BadRequest(new { error = "File path must be within the project workspace." });

                var workspaceRoot = project.WorkingDirectory
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, normalizedPath));

                // Containment check: reject any resolved path that escapes the workspace root.
                var rootWithSep = workspaceRoot + Path.DirectorySeparatorChar;
                var cmp = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                if (!fullPath.StartsWith(rootWithSep, cmp) && !fullPath.Equals(workspaceRoot, cmp))
                    return Results.BadRequest(new { error = "File path must be within the project workspace." });

                if (!File.Exists(fullPath))
                    return Results.NotFound(new { error = "File not found in project workspace." });

                try
                {
                    fileContent = await File.ReadAllTextAsync(fullPath, ct);
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        $"Decomposition failed: could not read file — {ex.Message}",
                        statusCode: 500);
                }
            }

            // Run the decomposition agent turn.
            DecomposeAgentResult agentResult;
            try
            {
                agentResult = await decomposeService.DecomposeAsync(project, fileContent, ct);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    $"Decomposition failed: {ex.Message}",
                    statusCode: 500);
            }

            // Idempotency: collect titles that already exist for this (project, source file).
            var existingTitles = await backlogStore.GetExistingTitlesFromSourceAsync(
                projectId, normalizedPath, ct);

            var proposedItems = agentResult.Items
                .Select(item => new ProposedBacklogItem(
                    item.Title,
                    item.Description,
                    existingTitles.Contains(item.Title)))
                .ToList();

            // Confirm = true → create tasks for genuinely new items only.
            if (request.Confirm)
            {
                var now = DateTimeOffset.UtcNow;
                var existing = await backlogStore.ListByProjectAsync(projectId, ct);
                // Build the tail of the existing backlog order_keys so new items append after them.
                var orderKeys = existing
                    .Where(t => t.State == BacklogTaskState.Backlog)
                    .OrderBy(t => t.OrderKey, StringComparer.Ordinal)
                    .Select(t => t.OrderKey)
                    .ToList();

                foreach (var item in proposedItems.Where(p => !p.AlreadyExists))
                {
                    var newKey = OrderKey.Between(orderKeys.Count > 0 ? orderKeys[^1] : null, null);
                    orderKeys.Add(newKey);

                    await backlogStore.InsertAsync(new BacklogTask
                    {
                        Id = BacklogTaskId.New(),
                        ProjectId = projectId,
                        Title = item.Title,
                        Description = item.Description,
                        State = BacklogTaskState.Backlog,
                        OrderKey = newKey,
                        CapturedBy = "decompose",
                        CreatedAt = now,
                        SourceFilePath = normalizedPath,
                    }, ct);
                }
            }

            return Results.Ok(new DecomposeResponse(
                proposedItems,
                agentResult.WasCapped,
                agentResult.TotalFound));
        }).WithTags("Backlog");
    }

    /// <summary>
    /// Formats an <see cref="OutcomeSpec"/> as a Markdown document suitable for the decomposition
    /// agent. Mirrors the display format shown in the web client's OutcomeSpecPanel.
    /// </summary>
    private static string BuildOutcomeSpecMarkdown(OutcomeSpec spec)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Goal");
        sb.AppendLine(spec.Goal);
        sb.AppendLine();
        sb.AppendLine("## Desired Outcome");
        sb.AppendLine(spec.DesiredOutcome);
        sb.AppendLine();
        sb.AppendLine("## Scope");
        sb.AppendLine(spec.Scope);
        sb.AppendLine();
        sb.AppendLine("## Assumptions");
        sb.AppendLine(spec.Assumptions);
        if (!string.IsNullOrWhiteSpace(spec.ClarifyingQuestions))
        {
            sb.AppendLine();
            sb.AppendLine("## Clarifying Questions");
            sb.AppendLine(spec.ClarifyingQuestions);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Recursively builds the workspace file tree rooted at <paramref name="root"/>. Skips
    /// <c>.git</c> directories. Returns directories before files at each level, both sorted
    /// alphabetically.
    /// </summary>
    private static IReadOnlyList<WorkspaceFileNode> BuildTree(string root, string directory)
    {
        var nodes = new List<WorkspaceFileNode>();
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        try
        {
            foreach (var subDir in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(subDir);
                if (name == ".git") continue;

                var rel = subDir.Length > rootWithSep.Length
                    ? subDir[rootWithSep.Length..].Replace('\\', '/')
                    : name;
                nodes.Add(new WorkspaceFileNode(name, rel, true, BuildTree(root, subDir)));
            }

            foreach (var file in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var rel = file.Length > rootWithSep.Length
                    ? file[rootWithSep.Length..].Replace('\\', '/')
                    : name;
                nodes.Add(new WorkspaceFileNode(name, rel, false, null));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories that are not accessible.
        }

        return nodes;
    }
}
