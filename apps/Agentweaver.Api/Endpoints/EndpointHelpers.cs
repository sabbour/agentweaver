using System.Text.Encodings.Web;
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

internal static async Task WriteSseEventAsync(HttpResponse response, RunEvent evt, CancellationToken ct)
{
    var json = System.Text.Json.JsonSerializer.Serialize(evt.Payload,
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    await response.WriteAsync($"id: {evt.Sequence}\nevent: {evt.Type}\ndata: {json}\n\n", ct);
    await response.Body.FlushAsync(ct);
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
