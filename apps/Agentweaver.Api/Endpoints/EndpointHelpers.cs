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
}
