using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Scaffolder.AgentRuntime;
using Scaffolder.Api.Memory;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Auth;
using Scaffolder.Api.Casting;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Coordinator;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Projects;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Security;
using Scaffolder.Domain;
using Scaffolder.Squad.Catalog;
using Scaffolder.Squad.Model;
using Scaffolder.Squad.Squad;
using Scaffolder.Squad.Analysis;
using Scaffolder.Squad.Sync;

namespace Scaffolder.Api.Endpoints;

internal static class EndpointHelpers
{
internal static bool IsOwner(HttpContext context, Run run) =>
    string.Equals(ApiKeyAuthMiddleware.GetCaller(context).User, run.SubmittingUser, StringComparison.Ordinal);

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

internal static SandboxPolicy ToSandboxPolicyDomain(SandboxPolicyDto dto) => new()
{
    RepositoryPath             = dto.RepositoryPath,
    ShellEnabled               = dto.ShellEnabled,
    Direct                     = dto.Direct,
    NetworkEnabled             = dto.NetworkEnabled,
    AllowedRepositoryRoots     = dto.AllowedRepositoryRoots,
    DestructiveCommandPatterns = dto.DestructiveCommandPatterns,
    RequireApprovalForAllShell = dto.RequireApprovalForAllShell,
    RedactPii                  = dto.RedactPii,
    MaxOutputBytes             = dto.MaxOutputBytes,
};
}
