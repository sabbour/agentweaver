using System.Net;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed record MafSourceControlOwnerOptions(Uri OwnerAddress);

/// <summary>Sends authenticated, bounded requests to the Orchestrator's Source Control owner routes.</summary>
internal sealed class MafSourceControlOwnerClient(
    HttpClient client,
    MafSourceControlOwnerOptions options)
{
    private const int MaximumPullRequestResponseBytes = 128 * 1024;
    private const int MaximumWorkspaceRevisionResponseBytes = 32 * 1024;
    private const int MaximumMergeIntentResponseBytes = 16 * 1024;

    private sealed record PullRequestResponse(
        string? Error,
        long? Number,
        Uri? HtmlUrl,
        string? State,
        string? HeadBranch,
        string? HeadSha,
        string? BaseBranch,
        string? BaseSha,
        bool? Merged,
        SourceControlPullRequestDisposition? Disposition,
        string? Title);

    private sealed record WorkspaceDiffResponse(
        string? Error,
        string? WorkspaceId,
        string? BaseSha,
        string? HeadSha,
        string? Status,
        string? Patch,
        Guid? WorkspaceIncarnationId);

    private sealed record MergeIntentResponse(
        string? Error,
        string? IntentId,
        string? ApprovalRequestId,
        string? State,
        string? Repository,
        long? PullRequestNumber,
        string? HeadSha,
        SourceControlMergeMethod? Method,
        Guid? ApprovalDecisionId,
        long? ApprovalStateVersion,
        JsonElement? GrantReference,
        string? MergeSha,
        string? FailureCode,
        string? LastFailureCode);

    internal async Task<SourceControlWorkspaceDiffView> ReadWorkspaceDiffAsync(
        RuntimeActorAuthorization actor,
        SessionIdentity identity,
        SourceControlPullRequestWorkspaceLocator workspace,
        CancellationToken cancellationToken)
    {
        var path = SourceControlPath(identity) +
            $"/workspaces/{Uri.EscapeDataString(workspace.WorkspaceId)}/diff";
        var (status, response) = await SendAsync<WorkspaceDiffResponse>(
            path,
            actor,
            new PrepareSourceControlWorkspaceRevisionRequest(
                workspace.BaseSha,
                workspace.BranchName)
            {
                WorkspaceIncarnationId = workspace.WorkspaceIncarnationId,
                IncludePatch = false
            },
            MaximumWorkspaceRevisionResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(status, response?.Error);
        if (response?.WorkspaceId is null || response.BaseSha is null ||
            response.HeadSha is null || response.Status is null || response.Patch is null ||
            response.WorkspaceIncarnationId is null)
            throw InvalidOwnerResponse();
        return new SourceControlWorkspaceDiffView(
            response.WorkspaceId,
            response.BaseSha,
            response.HeadSha,
            response.Status,
            response.Patch)
        {
            WorkspaceIncarnationId = response.WorkspaceIncarnationId.Value
        };
    }

    internal async Task<SourceControlPullRequest> CreateOrReusePullRequestAsync(
        RuntimeActorAuthorization actor,
        SessionIdentity identity,
        SourceControlPullRequestRequest request,
        CancellationToken cancellationToken)
    {
        var (status, response) = await SendAsync<PullRequestResponse>(
            SourceControlPath(identity) + "/pull-requests",
            actor,
            request,
            MaximumPullRequestResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(status, response?.Error);
        if (response?.Number is null || response.HtmlUrl is null ||
            response.State is null || response.HeadBranch is null || response.HeadSha is null ||
            response.BaseBranch is null || response.BaseSha is null ||
            response.Merged is null || response.Disposition is null)
            throw InvalidOwnerResponse();
        return new SourceControlPullRequest(
            response.Number.Value,
            response.HtmlUrl,
            response.State,
            response.HeadBranch,
            response.HeadSha,
            response.BaseBranch,
            response.BaseSha,
            response.Merged.Value,
            response.Disposition.Value,
            response.Title);
    }

    internal async Task<MafMergeOwnerIntent> PrepareMergeIntentAsync(
        RuntimeActorAuthorization actor,
        SessionIdentity identity,
        PrepareSourceControlMergeIntentRequest request,
        CancellationToken cancellationToken)
    {
        // Use the same durable owner route as interactive merge approval and preserve its exact response state.
        var (status, response) = await SendAsync<MergeIntentResponse>(
            SourceControlPath(identity) + "/merge-intents",
            actor,
            request,
            MaximumMergeIntentResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(status, response?.Error);
        if (response?.IntentId is null || response.State is null)
            throw InvalidOwnerResponse();
        return ToMergeOwnerIntent(response);
    }

    internal async Task<MafMergeOwnerIntent> ExecuteMergeIntentAsync(
        RuntimeActorAuthorization actor,
        SessionIdentity identity,
        string intentId,
        CancellationToken cancellationToken)
    {
        // The owner serializes execution and resolves merge_started without repeating a possibly applied effect.
        var (status, response) = await SendAsync<MergeIntentResponse>(
            SourceControlPath(identity) + "/merge-intents/" + Uri.EscapeDataString(intentId) + "/execute",
            actor,
            new { },
            MaximumMergeIntentResponseBytes,
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(status, response?.Error);
        if (response?.IntentId is null || response.State is null)
            throw InvalidOwnerResponse();
        return ToMergeOwnerIntent(response);
    }

    private async Task<(HttpStatusCode StatusCode, TResponse? Response)> SendAsync<TResponse>(
        string path,
        RuntimeActorAuthorization actor,
        object body,
        int maximumBytes,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            return await RuntimeOwnerHttpTransport.SendBoundedOutcomeAsync<TResponse>(
                client, options.OwnerAddress, path, actor, body, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw exception.Code switch
            {
                "runtime_owner_denied" => new CoordinationException(
                    "source_control_run_denied", StatusCodes.Status403Forbidden),
                "runtime_owner_binding_changed" => new CoordinationException(
                    "source_control_run_binding_changed", StatusCodes.Status409Conflict),
                _ => new CoordinationException(
                    "source_control_owner_unavailable", StatusCodes.Status502BadGateway)
            };
        }
        catch (HttpRequestException)
        {
            throw new CoordinationException(
                "source_control_owner_unavailable", StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoordinationException(
                "source_control_owner_unavailable", StatusCodes.Status502BadGateway);
        }
    }

    private static void RequireSuccess(HttpStatusCode statusCode, string? error)
    {
        if ((int)statusCode is >= 200 and < 300)
            return;
        if (!string.IsNullOrWhiteSpace(error) &&
            error.Length <= 128 &&
            error.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            throw new CoordinationException(error, (int)statusCode);
        throw new CoordinationException(
            "source_control_owner_unavailable", statusCode == HttpStatusCode.Unauthorized ||
            statusCode == HttpStatusCode.Forbidden
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status502BadGateway);
    }

    private static string SourceControlPath(SessionIdentity identity) =>
        $"/api/projects/{Uri.EscapeDataString(identity.ProjectId)}/runs/{Uri.EscapeDataString(identity.RunId)}" +
        $"/source-control/sessions/{Uri.EscapeDataString(identity.SessionId)}";

    private static CoordinationException InvalidOwnerResponse() =>
        new("source_control_owner_response_invalid", StatusCodes.Status502BadGateway);

    private static MafMergeOwnerIntent ToMergeOwnerIntent(MergeIntentResponse response)
    {
        // Retain only fields needed to prove the approval, exact PR binding, and terminal merge result.
        return new(
            response.IntentId!,
            response.State!,
            response.ApprovalRequestId,
            response.Repository,
            response.PullRequestNumber,
            response.HeadSha,
            response.Method,
            response.ApprovalDecisionId,
            response.ApprovalStateVersion,
            response.GrantReference is { ValueKind: JsonValueKind.Object },
            response.MergeSha,
            response.FailureCode ?? response.LastFailureCode);
    }
}
