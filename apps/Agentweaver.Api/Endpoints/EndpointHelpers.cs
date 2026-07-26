using System.Text.Encodings.Web;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;

namespace Agentweaver.Api.Endpoints;

internal static class EndpointHelpers
{
internal static bool IsOwner(HttpContext context, Run run) =>
    ApiKeyAuthMiddleware.GetCaller(context).Owns(run.SubmittingUser);

/// <summary>
/// Resolves the run that actually OWNS a tool-approval <paramref name="requestId"/>. The approval
/// context is registered (via <c>WaitForApprovalAsync</c>) on the run that RAISED the tool call —
/// for a Coordinator orchestration that is a CHILD subtask run, not the coordinator itself. Yet
/// operators grant/deny from the coordinator view and the request may be POSTed to the parent
/// coordinator run id. When the posted run does not own the request and it is a coordinator/parent
/// run, this searches its child subtask runs and returns the child that owns the request. Returns
/// the posted run id when it already owns the request, or <see langword="null"/> when no parent or
/// child run knows the request_id. This is the server-side, definitive owning-run resolution that
/// makes tool approval robust regardless of which run id the client targets (recurrence of #196).
/// </summary>
/// <summary>
/// Synthetic run-id suffixes used to key approval-gate context for coordinator-phase LLM turns
/// (drafting, decomposing, orchestrating) that run under the coordinator's OWN run id rather than
/// a persisted child subtask run — see <c>CopilotCoordinatorSpecDrafter</c> (SetupAsync runId:
/// <c>input.RunId + "-coordinator-draft"</c>) and <c>IRunAgentHostContextResolver</c>'s matching
/// list. These ids never exist as RunStore rows, so the child-subtask fan-out below can never find
/// them; they must be checked directly against the posted (parent) run id first.
/// </summary>
private static readonly string[] CoordinatorPhaseSuffixes =
[
    "-coordinator-draft",
    "-coordinator-decompose",
    "-coordinator-orchestrate",
];

/// <summary>
/// True when <paramref name="candidateRunId"/> is a synthetic coordinator-phase approval-gate key
/// (<paramref name="postedRunId"/> + one of <see cref="CoordinatorPhaseSuffixes"/>) rather than a
/// real, independently-persisted run id. <see cref="ResolveApprovalOwningRunIdAsync"/> can return
/// such a synthetic id to key the approval-gate lookup, but it is NOT a row in the run store — it
/// must never be passed to <c>RunId.Parse</c>. Callers should treat it as referring to the SAME
/// underlying run as <paramref name="postedRunId"/> for ownership/status/RunId-parsing purposes,
/// while still using the synthetic id (unchanged) as the approval-gate lookup key.
/// </summary>
internal static bool IsCoordinatorPhaseSuffixedId(string candidateRunId, string postedRunId)
{
    foreach (var suffix in CoordinatorPhaseSuffixes)
    {
        if (candidateRunId.Length == postedRunId.Length + suffix.Length
            && candidateRunId.StartsWith(postedRunId, StringComparison.Ordinal)
            && candidateRunId.EndsWith(suffix, StringComparison.Ordinal))
            return true;
    }
    return false;
}

internal static async Task<string?> ResolveApprovalOwningRunIdAsync(
    string postedRunId,
    Run postedRun,
    string requestId,
    IToolApprovalGate gate,
    IRunStore runStore,
    CancellationToken ct,
    IServiceScopeFactory? scopeFactory = null)
{
    if (gate.GetRequestState(postedRunId, requestId) != ToolApprovalRequestState.Unknown)
        return postedRunId;

    foreach (var suffix in CoordinatorPhaseSuffixes)
    {
        var suffixedRunId = postedRunId + suffix;
        if (gate.GetRequestState(suffixedRunId, requestId) != ToolApprovalRequestState.Unknown)
            return suffixedRunId;
    }

    // Only a coordinator/parent run (ParentRunId == null && AgentName == "Coordinator") fans out to
    // child subtask runs; a plain run or a child never owns another run's approval requests.
    var isCoordinator = postedRun.ParentRunId is null
        && string.Equals(postedRun.AgentName, "Coordinator", StringComparison.Ordinal);
    if (!isCoordinator)
        return null;

    var children = await runStore.GetRunsByParentAsync(postedRunId, ct).ConfigureAwait(false);
    foreach (var child in children)
    {
        var childId = child.Id.ToString();
        if (gate.GetRequestState(childId, requestId) != ToolApprovalRequestState.Unknown)
            return childId;
    }

    if (scopeFactory is not null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var payloads = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == postedRunId
                && e.EventType == EventTypes.CoordinatorChildApprovalRequired)
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.PayloadJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var payloadJson in payloads)
        {
            try
            {
                using var payload = JsonDocument.Parse(payloadJson);
                var root = payload.RootElement;
                if (root.TryGetProperty("requestId", out var persistedRequestId)
                    && string.Equals(persistedRequestId.GetString(), requestId, StringComparison.Ordinal)
                    && root.TryGetProperty("childRunId", out var childRunId)
                    && !string.IsNullOrWhiteSpace(childRunId.GetString()))
                {
                    return childRunId.GetString();
                }
            }
            catch (JsonException)
            {
                // Ignore a corrupt unrelated event and continue searching older matching records.
            }
        }
    }

    return null;
}

/// <summary>
/// The set of run statuses that represent a completed lifecycle. A run in any of these states
/// has no live workflow to cancel and owns no worktree that needs tearing down.
/// </summary>
internal static readonly IReadOnlySet<RunStatus> TerminalRunStatuses = new HashSet<RunStatus>
{
    RunStatus.Merged, RunStatus.Declined, RunStatus.MergeFailed, RunStatus.Failed, RunStatus.Completed,
};

internal static bool IsTerminal(RunStatus status) => TerminalRunStatuses.Contains(status);

/// <summary>
/// Cancels a non-terminal run's live work: signals the MAF workflow to abandon (which also stops any
/// child subtask runs the coordinator is driving through the same workflow), best-effort removes the
/// worktree, forces the run to a terminal <see cref="RunStatus.Failed"/> state, and completes the
/// live event stream. This is the SHARED cancellation path used by both the DELETE /api/runs/{id}
/// endpoint (which then deletes the run row) and the POST /api/runs/{id}/cancel endpoint (which
/// leaves the row so the user can still inspect it). Callers MUST verify the run is non-terminal
/// before invoking (see <see cref="IsTerminal"/>).
/// </summary>
/// <param name="podLifecycle">
/// #350 — cancelling <paramref name="registry"/>'s local <see cref="System.Threading.CancellationTokenSource"/>
/// has NO effect on a remote AgentHost/sandbox pod (pod-per-run mode): the underlying process can
/// keep executing tool calls and emitting new tool.approval_required events against a run the system
/// already considers dead. Null when not running pod-per-run / not in Kubernetes — release is then a
/// no-op. Optional so existing callers/tests that only exercise in-api mode are unaffected.
/// </param>
/// <param name="sandboxRuntime">Resolves whether the deployment is pod-per-run; defaults to in-api (no-op) when omitted.</param>
internal static async Task CancelRunWorkAsync(
    Run run,
    IRunStore runStore,
    RunStreamStore streamStore,
    RunWorkflowRegistry registry,
    IWorktreeOperations worktreeOps,
    ILogger logger,
    CancellationToken ct,
    IAgentHostPodLifecycle? podLifecycle = null,
    SandboxRuntimeOptions? sandboxRuntime = null)
{
    var id = run.Id.ToString();

    registry.Abandon(id);
    // Give the running agent a brief window to observe the cancellation signal before the worktree
    // is torn down. Without this, a tool call in-flight may try to write to a path already deleted.
    await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);

    if (run.WorktreePath is not null && worktreeOps.WorktreeExists(run.WorktreePath))
    {
        try { worktreeOps.RemoveWorktree(run.RepositoryPath, run.WorktreePath, run.WorktreeBranch ?? string.Empty); }
        catch (Exception ex) { logger.LogWarning(ex, "Best-effort worktree cleanup failed for cancelled run {RunId}", id); }
    }

    await runStore.TrySetTerminalStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, "abandoned", ct);
    streamStore.Complete(id);

    // #350: reliably tear down the remote AgentHost pod itself, not just the local token above.
    await ReleaseAgentHostPodSafeAsync(id, podLifecycle, sandboxRuntime, logger).ConfigureAwait(false);
}

/// <summary>
/// Releases the AgentHost pod for <paramref name="runId"/> when running pod-per-run (#350). Shared
/// best-effort helper: logs and swallows exceptions so a release failure never blocks the calling
/// run/cancel/fail transition. Mirrors the identically-named helpers in CoordinatorRunService /
/// CoordinatorDispatchService / CoordinatorAssemblyService / RunWatchLoopService, adapted to the
/// static, non-DI-field context of this helper class.
/// </summary>
internal static async Task ReleaseAgentHostPodSafeAsync(
    string runId,
    IAgentHostPodLifecycle? podLifecycle,
    SandboxRuntimeOptions? sandboxRuntime,
    ILogger logger)
{
    if (podLifecycle is null || sandboxRuntime is not { IsPodPerRun: true })
        return;

    try
    {
        await podLifecycle.ReleaseAgentHostPodAsync(runId, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("CancelRunWorkAsync: AgentHost pod released for cancelled run {RunId}", runId);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex,
            "CancelRunWorkAsync: failed to release AgentHost pod for run {RunId} (best-effort)", runId);
    }
}

/// <summary>
/// Authorizes either a human OWNER of the run (<see cref="IsOwner"/>) OR the run's own agent
/// callback channel, which authenticates with the shared service API key and therefore resolves
/// to the configured <c>Auth:User</c> identity (not the human owner). The agent-callback write
/// endpoints (memory/decision/backlog) rely on the global auth middleware only; this helper lets
/// the run's own agent reach a run-scoped action (e.g. <c>start_preview</c>) without weakening
/// security: the runId is server-bound in the tool closure, so a service-identity caller can only
/// act on the run the agent is actually executing, never another user's run via a different runId.
/// </summary>
internal static bool IsOwnerOrServiceCaller(HttpContext context, Run run, IConfiguration configuration)
{
    if (IsOwner(context, run)) return true;

    var serviceUser = configuration["Auth:User"];
    if (string.IsNullOrEmpty(serviceUser)) return false;

    var caller = ApiKeyAuthMiddleware.GetCaller(context);
    return string.Equals(caller.User, serviceUser, StringComparison.Ordinal);
}

internal static async Task WriteSseEventAsync(HttpResponse response, RunEvent evt, CancellationToken ct)
{
    var json = System.Text.Json.JsonSerializer.Serialize(StampTimestamp(evt),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    await response.WriteAsync($"id: {evt.Sequence}\nevent: {evt.Type}\ndata: {json}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

/// <summary>
/// Ensures the wire payload carries a `timestamp_utc` key sourced from <see cref="RunEvent.TimestampUtc"/>
/// (stamped centrally by RunStreamStore.RecordNext/Record — see RunEvent.cs) so the frontend's
/// `readTimestamp()` (apps/web/src/components/AgentSessionPanel.tsx) never has to fall back to
/// `Date.now()` at render time. Individual emitters that already embed their own `timestamp_utc`/
/// `timestampUtc`/`timestamp` field in the payload are left untouched — this only fills the gap.
/// </summary>
internal static System.Text.Json.Nodes.JsonObject StampTimestamp(RunEvent evt)
{
    var node = System.Text.Json.JsonSerializer.SerializeToNode(evt.Payload) as System.Text.Json.Nodes.JsonObject
        ?? new System.Text.Json.Nodes.JsonObject();
    if (!node.ContainsKey("timestamp_utc") && !node.ContainsKey("timestampUtc") && !node.ContainsKey("timestamp"))
        node["timestamp_utc"] = evt.TimestampUtc == default
            ? DateTimeOffset.UtcNow.ToString("O")
            : evt.TimestampUtc.ToString("O");
    return node;
}

internal static async Task WriteSseDoneAsync(HttpResponse response, CancellationToken ct)
{
    await response.WriteAsync("event: done\ndata: {}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

internal static SandboxPolicyDto ToSandboxPolicyDto(SandboxPolicy policy) => new()
{
    RepositoryPath             = policy.RepositoryPath,
    ShellEnabled               = policy.ShellEnabled,
    Direct                     = policy.Direct,
    NetworkEnabled             = policy.NetworkEnabled,
    AllowedRepositoryRoots     = policy.AllowedRepositoryRoots,
    DestructiveCommandPatterns = policy.DestructiveCommandPatterns,
    RequireApprovalForAllShell = policy.RequireApprovalForAllShell,
    RedactPii                  = policy.RedactPii,
    MaxOutputBytes             = policy.MaxOutputBytes,
};

/// <summary>
/// Applies a partial <see cref="SandboxPolicyUpdateRequest"/> onto the EXISTING stored policy
/// (PATCH/preserve semantics). For each field: a provided (non-null) value is applied; an omitted
/// (null) field preserves the existing value. An explicitly provided empty array clears that list —
/// only a missing array preserves it. This is what makes a minimal partial PUT (e.g. only
/// shell_enabled) flip that field and leave repo roots / blocked patterns / the other flags intact.
/// </summary>
internal static SandboxPolicy MergeSandboxPolicy(SandboxPolicy existing, SandboxPolicyUpdateRequest request) => existing with
{
    RepositoryPath             = request.RepositoryPath,
    ShellEnabled               = request.ShellEnabled ?? existing.ShellEnabled,
    Direct                     = request.Direct ?? existing.Direct,
    NetworkEnabled             = request.NetworkEnabled ?? existing.NetworkEnabled,
    AllowedRepositoryRoots     = request.AllowedRepositoryRoots ?? existing.AllowedRepositoryRoots,
    DestructiveCommandPatterns = request.DestructiveCommandPatterns ?? existing.DestructiveCommandPatterns,
    RequireApprovalForAllShell = request.RequireApprovalForAllShell ?? existing.RequireApprovalForAllShell,
    RedactPii                  = request.RedactPii ?? existing.RedactPii,
    MaxOutputBytes             = request.MaxOutputBytes ?? existing.MaxOutputBytes,
};

/// <summary>
/// Builds the <see cref="WorkspaceFileContent"/> the Preview/source tab consumes from a git
/// <see cref="Blob"/>. Shared by the merged-run content endpoint (RunEndpoints) and the coordinator
/// assembly content endpoint (CoordinatorEndpoints) so the binary / too-large / text handling has a
/// single implementation. The 1&#160;MB cap mirrors the worktree-backed content path.
/// </summary>
internal static WorkspaceFileContent BuildBlobContent(Blob blob, string normalizedPath)
{
    const int maxGitContentBytes = 1 * 1024 * 1024;

    if (blob.IsBinary)
        return new WorkspaceFileContent { Path = normalizedPath, Content = null, IsBinary = true, Language = DetectLanguage(normalizedPath) };

    if (blob.Size > maxGitContentBytes)
        return new WorkspaceFileContent { Path = normalizedPath, Content = null, IsBinary = false, Language = "too_large" };

    return new WorkspaceFileContent
    {
        Path     = normalizedPath,
        Content  = blob.GetContentText(),
        IsBinary = false,
        Language = DetectLanguage(normalizedPath),
    };
}

/// <summary>
/// Maps a file extension to a language identifier accepted by react-syntax-highlighter.
/// Returns null for unknown extensions. Shared across the run and coordinator content endpoints.
/// </summary>
internal static string? DetectLanguage(string path)
{
    var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    return ext switch
    {
        "cs"                                    => "csharp",
        "ts" or "tsx"                           => "typescript",
        "js" or "jsx"                           => "javascript",
        "json"                                  => "json",
        "md"                                    => "markdown",
        "css"                                   => "css",
        "html"                                  => "html",
        "xml" or "csproj" or "props" or "targets" => "xml",
        "yaml" or "yml"                         => "yaml",
        "sh" or "bash"                          => "bash",
        "ps1"                                   => "powershell",
        "py"                                    => "python",
        "go"                                    => "go",
        "rs"                                    => "rust",
        "java"                                  => "java",
        "cpp" or "cc" or "cxx" or "c" or "h" or "hpp" => "cpp",
        "sql"                                   => "sql",
        "txt"                                   => "plaintext",
        _                                       => null
    };
}

/// <summary>
/// Recursively enumerates a git <see cref="Tree"/> into a flat list of <see cref="WorkspaceNode"/>
/// (folders and blobs, forward-slash relative paths, no diff status). Shared by the run workspace
/// endpoint and the project workspace service so commit-tree listing has a single implementation.
/// </summary>
internal static void EnumerateGitTree(Tree tree, string prefix, List<WorkspaceNode> nodes)
{
    foreach (var entry in tree)
    {
        var entryPath = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
        if (entry.TargetType == TreeEntryTargetType.Tree)
        {
            nodes.Add(new WorkspaceNode { Path = entryPath, IsFolder = true, Status = null });
            EnumerateGitTree((Tree)entry.Target, entryPath, nodes);
        }
        else if (entry.TargetType == TreeEntryTargetType.Blob)
        {
            nodes.Add(new WorkspaceNode { Path = entryPath, IsFolder = false, Status = null });
        }
    }
}

/// <summary>
/// Validates a relative file path from a route parameter. Normalizes percent-encoded
/// separators (%2F, %5C) that ASP.NET Core does not decode in catch-all route params,
/// then rejects null bytes, control characters (including DEL and C1), rooted paths,
/// UNC paths, device paths, drive-relative paths, parent-traversal segments, and on
/// Windows, Alternate Data Stream specifiers. Returns false on any violation; sets
/// normalizedPath to the canonical relative form on success. Shared by the run file
/// endpoint and the project workspace service.
/// </summary>
internal static bool TryValidateRelativePath(string? rawPath, out string normalizedPath)
{
    normalizedPath = string.Empty;
    if (string.IsNullOrEmpty(rawPath)) return false;

    rawPath = rawPath.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
                     .Replace("%5C", "/", StringComparison.OrdinalIgnoreCase);

    foreach (var c in rawPath)
    {
        if (c == '\0' || c < ' ') return false;
        if (c == '\u007F' || (c >= '\u0080' && c <= '\u009F')) return false;
    }

    if (rawPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;

    var normalized = rawPath.Replace('\\', '/');

    if (Path.IsPathRooted(normalized)) return false;

    if (normalized.StartsWith("//", StringComparison.Ordinal)) return false;

    if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        return false;

    foreach (var segment in normalized.Split('/'))
    {
        if (segment == "..") return false;
    }

    if (OperatingSystem.IsWindows() && normalized.Contains(':', StringComparison.Ordinal))
        return false;

    normalizedPath = normalized;
    return true;
}
}
