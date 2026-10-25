using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>Builds and validates the durable request and terminal proof for an OpenPullRequest gate.</summary>
internal static class MafOpenPullRequestContract
{
    internal const string DefaultTitleSource = "Agentweaver: {outcome_summary}";
    internal const string DefaultBodySource =
        "Automated changes produced by Agentweaver run `{run_id}` on branch `{worktree_branch}`.\n\n{outcome_summary}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string CreateOutcomeSummary(int completedWorkItems, string branchName) =>
        completedWorkItems switch
        {
            <= 0 => "No changes were recorded for this run.",
            1 => $"1 step produced changes on `{branchName}`.",
            _ => $"{completedWorkItems} steps produced changes on `{branchName}`."
        };

    internal static MafExecutionOpenPullRequestIntent CreateIntent(
        Guid operationId,
        SessionIdentity identity,
        string checkpointId,
        long checkpointRevision,
        string workPlanId,
        string stepId,
        long decisionStateVersion,
        long executionFence,
        string acceptedSelectionHash,
        SourceControlRepositoryPin pin,
        SourceControlPullRequestWorkspaceLocator workspace,
        int completedWorkItems)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(workspace);
        var outcomeSummary = CreateOutcomeSummary(completedWorkItems, workspace.BranchName);
        var request = new SourceControlPullRequestRequest(
            DefaultTitleSource.Replace("{outcome_summary}", outcomeSummary, StringComparison.Ordinal),
            DefaultBodySource
                .Replace("{run_id}", identity.RunId, StringComparison.Ordinal)
                .Replace("{worktree_branch}", workspace.BranchName, StringComparison.Ordinal)
                .Replace("{outcome_summary}", outcomeSummary, StringComparison.Ordinal),
            workspace.BranchName,
            workspace.HeadSha,
            workspace.BaseBranch,
            workspace.BaseSha,
            Draft: false)
        {
            ExecutionBinding = new SourceControlPullRequestExecutionBinding(
                operationId,
                decisionStateVersion,
                executionFence,
                acceptedSelectionHash,
                workPlanId,
                stepId,
                pin.PinId,
                workspace.WorkspaceId,
                workspace.WorkspaceIncarnationId,
                workspace.BranchName,
                workspace.BaseBranch,
                workspace.BaseSha,
                workspace.HeadSha)
        };
        var intent = new MafExecutionOpenPullRequestIntent(
            operationId,
            identity,
            checkpointId,
            checkpointRevision,
            workPlanId,
            stepId,
            decisionStateVersion,
            executionFence,
            acceptedSelectionHash,
            pin.PinId,
            pin.Repository,
            workspace,
            DefaultTitleSource,
            DefaultBodySource,
            outcomeSummary,
            request);
        if (!IsValidIntent(intent))
            throw new CoordinationException(
                "source_control_pull_request_request_invalid", StatusCodes.Status400BadRequest);
        return intent;
    }

    internal static bool IsValidWorkspace(SourceControlPullRequestWorkspaceLocator? workspace) =>
        workspace is not null &&
        IsWorkspaceId(workspace.WorkspaceId) &&
        workspace.WorkspaceIncarnationId != Guid.Empty &&
        IsGitSha(workspace.BaseSha) &&
        IsGitBranch(workspace.BranchName) &&
        IsGitSha(workspace.HeadSha) &&
        IsGitBranch(workspace.BaseBranch);

    internal static bool IsValidIntent(MafExecutionOpenPullRequestIntent intent)
    {
        if (intent is null || intent.Request is null ||
            !IsValidWorkspace(intent.Workspace) ||
            !IsRepository(intent.Repository) ||
            !IsMetadataValue(intent.SourceControlPinId, 128) ||
            intent.OperationId == Guid.Empty ||
            intent.Identity == default ||
            !IsMetadataValue(intent.Identity.ProjectId, 256) ||
            !IsMetadataValue(intent.Identity.RunId, 256) ||
            !IsMetadataValue(intent.Identity.SessionId, 256) ||
            !IsMetadataValue(intent.CheckpointId, 128) ||
            !IsMetadataValue(intent.WorkPlanId, 128) ||
            !IsMetadataValue(intent.StepId, 128) ||
            intent.CheckpointRevision < 1 ||
            intent.DecisionStateVersion < 1 ||
            intent.ExecutionFence < 1 ||
            !IsSha256(intent.AcceptedSelectionHash) ||
            intent.TitleSource != DefaultTitleSource ||
            intent.BodySource != DefaultBodySource ||
            string.IsNullOrWhiteSpace(intent.OutcomeSummary) ||
            intent.OutcomeSummary.Length > 1024 ||
            intent.OutcomeSummary.Any(character => char.IsControl(character) &&
                character is not ('\r' or '\n' or '\t')))
            return false;

        var workspace = intent.Workspace;
        var request = intent.Request;
        return request.Title == DefaultTitleSource.Replace(
                "{outcome_summary}", intent.OutcomeSummary, StringComparison.Ordinal) &&
            request.Body == DefaultBodySource
                .Replace("{run_id}", intent.Identity.RunId, StringComparison.Ordinal)
                .Replace("{worktree_branch}", workspace.BranchName, StringComparison.Ordinal)
                .Replace("{outcome_summary}", intent.OutcomeSummary, StringComparison.Ordinal) &&
            request.Title.Length is > 0 and <= 256 &&
            request.Body is { Length: <= 60_000 } &&
            request.HeadBranch == workspace.BranchName &&
            request.ExpectedHeadSha == workspace.HeadSha &&
            request.BaseBranch == workspace.BaseBranch &&
            request.ExpectedBaseSha == workspace.BaseSha &&
            !request.Draft &&
            IsMatchingExecutionBinding(intent, request.ExecutionBinding);
    }

    internal static void RequireCurrentSource(
        MafExecutionOpenPullRequestIntent intent,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        long decisionStateVersion,
        long executionFence,
        string acceptedSelectionHash,
        SourceControlRepositoryPin pin)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(pin);
        var accepted = pin.AcceptedRun;
        if (intent.Identity != identity ||
            intent.DecisionStateVersion != decisionStateVersion ||
            intent.ExecutionFence != executionFence ||
            !string.Equals(intent.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(intent.SourceControlPinId, pin.PinId, StringComparison.Ordinal) ||
            intent.Repository != pin.Repository ||
            !string.Equals(intent.Request.BaseBranch, pin.DefaultBranch, StringComparison.Ordinal) ||
            accepted.ProjectId != identity.ProjectId ||
            accepted.RunId != identity.RunId ||
            accepted.RootSessionId != identity.SessionId ||
            accepted.Issuer != actor.Issuer ||
            accepted.Subject != actor.Subject ||
            accepted.TenantId != tenantId ||
            accepted.Fence != executionFence ||
            !string.Equals(accepted.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.OrdinalIgnoreCase))
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static void RequireWorkspaceRevision(
        MafExecutionOpenPullRequestIntent intent,
        SourceControlWorkspaceDiffView workspace)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!string.Equals(workspace.WorkspaceId, intent.Workspace.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(workspace.BaseSha, intent.Workspace.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workspace.HeadSha, intent.Workspace.HeadSha, StringComparison.OrdinalIgnoreCase) ||
            workspace.WorkspaceIncarnationId != intent.Workspace.WorkspaceIncarnationId)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static void RequireOwnerBinding(
        SourceControlRunContext run,
        SourceControlPullRequestRequest request)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);
        var binding = request.ExecutionBinding;
        var selectionHash = CoordinationOwnerStore.HashSelection(run.Selection.Selection);
        if (binding is null ||
            binding.OperationId == Guid.Empty ||
            binding.DecisionStateVersion < 1 ||
            binding.ExecutionFence != run.Pin.AcceptedRun.Fence ||
            binding.DecisionStateVersion != run.DecisionStateVersion ||
            !IsSha256(binding.AcceptedSelectionHash) ||
            !string.Equals(binding.AcceptedSelectionHash, selectionHash, StringComparison.Ordinal) ||
            !IsMetadataValue(binding.WorkPlanId, 128) ||
            !IsMetadataValue(binding.WorkflowStepId, 128) ||
            !string.Equals(binding.SourceControlPinId, run.Pin.PinId, StringComparison.Ordinal) ||
            binding.WorkspaceIncarnationId == Guid.Empty ||
            !string.Equals(binding.WorkspaceBranchName, request.HeadBranch, StringComparison.Ordinal) ||
            !string.Equals(binding.WorkspaceBaseBranch, request.BaseBranch, StringComparison.Ordinal) ||
            !string.Equals(binding.WorkspaceBaseSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(binding.WorkspaceHeadSha, request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.BaseBranch, run.Pin.DefaultBranch, StringComparison.Ordinal) ||
            request.HeadBranch.Length > 255 ||
            !string.Equals(binding.AcceptedSelectionHash, run.Pin.AcceptedRun.AcceptedSelectionHash,
                StringComparison.Ordinal))
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static void RequireOwnerWorkspaceRevision(
        SourceControlPullRequestExecutionBinding binding,
        SourceControlWorkspaceDiffView workspace)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!string.Equals(workspace.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal) ||
            workspace.WorkspaceIncarnationId != binding.WorkspaceIncarnationId ||
            !string.Equals(workspace.BaseSha, binding.WorkspaceBaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workspace.HeadSha, binding.WorkspaceHeadSha, StringComparison.OrdinalIgnoreCase))
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static void RequireOwnerPullRequestTarget(
        SourceControlPullRequestExecutionBinding binding,
        SourceControlRepositoryIdentity repository,
        SourceControlPullRequest pullRequest)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (pullRequest.Number < 1 ||
            pullRequest.State is not ("open" or "closed") ||
            pullRequest.State == "open" && pullRequest.Merged ||
            pullRequest.State == "closed" && pullRequest.Merged &&
                pullRequest.Disposition == SourceControlPullRequestDisposition.Created ||
            pullRequest.Disposition is not (SourceControlPullRequestDisposition.Created or
                SourceControlPullRequestDisposition.Reused) ||
            !string.Equals(pullRequest.HeadBranch, binding.WorkspaceBranchName, StringComparison.Ordinal) ||
            !string.Equals(pullRequest.BaseBranch, binding.WorkspaceBaseBranch, StringComparison.Ordinal) ||
            !string.Equals(pullRequest.HeadSha, binding.WorkspaceHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pullRequest.BaseSha, binding.WorkspaceBaseSha, StringComparison.OrdinalIgnoreCase) ||
            pullRequest.HtmlUrl is null ||
            !IsPullRequestUrl(pullRequest.HtmlUrl, repository, pullRequest.Number))
            throw new CoordinationException(
                "source_control_pull_request_response_invalid", StatusCodes.Status502BadGateway);
    }

    internal static string OwnerRequestFingerprint(
        SessionIdentity identity,
        SourceControlPullRequestRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            identity,
            request
        }, JsonOptions)));

    internal static MafOpenPullRequestTerminalReceipt CreateReceipt(
        MafExecutionOpenPullRequestIntent intent,
        SourceControlPullRequest pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (!IsValidPullRequestResult(intent, pullRequest))
            throw new CoordinationException(
                "source_control_pull_request_response_invalid", StatusCodes.Status502BadGateway);
        return new MafOpenPullRequestTerminalReceipt(
            intent.OperationId,
            pullRequest.Number,
            pullRequest.HtmlUrl.AbsoluteUri,
            pullRequest.HeadSha,
            Fingerprint(intent));
    }

    internal static MafExecutionTaskStatus ValidateReceipt(
        MafExecutionOpenPullRequestIntent intent,
        MafOpenPullRequestTerminalReceipt receipt)
    {
        if (receipt.OperationId != intent.OperationId ||
            receipt.PullRequestNumber < 1 ||
            !string.Equals(receipt.HeadSha, intent.Request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.RequestFingerprint, Fingerprint(intent), StringComparison.Ordinal) ||
            !Uri.TryCreate(receipt.HtmlUrl, UriKind.Absolute, out var url) ||
            !IsPullRequestUrl(url, intent.Repository, receipt.PullRequestNumber))
            throw new ArgumentException("The OpenPullRequest receipt is invalid.", nameof(receipt));
        return MafExecutionTaskStatus.Succeeded;
    }

    internal static bool IsValidPullRequestResult(
        MafExecutionOpenPullRequestIntent intent,
        SourceControlPullRequest pullRequest) =>
        pullRequest.Number > 0 &&
        string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase) &&
        !pullRequest.Merged &&
        pullRequest.Disposition is SourceControlPullRequestDisposition.Created or SourceControlPullRequestDisposition.Reused &&
        string.Equals(pullRequest.HeadBranch, intent.Request.HeadBranch, StringComparison.Ordinal) &&
        string.Equals(pullRequest.HeadSha, intent.Request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(pullRequest.BaseBranch, intent.Request.BaseBranch, StringComparison.Ordinal) &&
        string.Equals(pullRequest.BaseSha, intent.Request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase) &&
        pullRequest.HtmlUrl is not null &&
        IsPullRequestUrl(pullRequest.HtmlUrl, intent.Repository, pullRequest.Number);

    internal static string ResultSummary(MafOpenPullRequestTerminalReceipt receipt) =>
        JsonSerializer.Serialize(new
        {
            kind = "open-pull-request",
            pullRequestNumber = receipt.PullRequestNumber,
            htmlUrl = receipt.HtmlUrl,
            headSha = receipt.HeadSha
        });

    internal static string Fingerprint(MafExecutionOpenPullRequestIntent intent) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            intent.OperationId,
            intent.Identity,
            intent.WorkPlanId,
            intent.StepId,
            intent.DecisionStateVersion,
            intent.ExecutionFence,
            intent.AcceptedSelectionHash,
            intent.SourceControlPinId,
            intent.Repository,
            intent.Workspace,
            intent.TitleSource,
            intent.BodySource,
            intent.OutcomeSummary,
            intent.Request
        }, JsonOptions)));

    private static bool IsMatchingExecutionBinding(
        MafExecutionOpenPullRequestIntent intent,
        SourceControlPullRequestExecutionBinding? binding) =>
        binding is not null &&
        binding.OperationId == intent.OperationId &&
        binding.DecisionStateVersion == intent.DecisionStateVersion &&
        binding.ExecutionFence == intent.ExecutionFence &&
        string.Equals(binding.AcceptedSelectionHash, intent.AcceptedSelectionHash, StringComparison.Ordinal) &&
        binding.WorkPlanId == intent.WorkPlanId &&
        binding.WorkflowStepId == intent.StepId &&
        binding.SourceControlPinId == intent.SourceControlPinId &&
        binding.WorkspaceId == intent.Workspace.WorkspaceId &&
        binding.WorkspaceIncarnationId == intent.Workspace.WorkspaceIncarnationId &&
        binding.WorkspaceBranchName == intent.Workspace.BranchName &&
        binding.WorkspaceBaseBranch == intent.Workspace.BaseBranch &&
        string.Equals(binding.WorkspaceBaseSha, intent.Workspace.BaseSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(binding.WorkspaceHeadSha, intent.Workspace.HeadSha, StringComparison.OrdinalIgnoreCase) &&
        !binding.OwnerMayCreate;

    private static bool IsPullRequestUrl(
        Uri uri,
        SourceControlRepositoryIdentity repository,
        long number)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.AbsoluteUri.Length > 2048)
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 &&
            string.Equals(segments[^4], repository.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[^3], repository.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[^2], "pull", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[^1], number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static bool IsRepository(SourceControlRepositoryIdentity? repository) =>
        repository is not null &&
        IsMetadataValue(repository.Owner, 100) &&
        IsMetadataValue(repository.Name, 100);

    private static bool IsWorkspaceId(string? value) =>
        IsMetadataValue(value, 128) &&
        value!.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsGitBranch(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 255 &&
        value[0] != '-' &&
        !value.Contains("..", StringComparison.Ordinal) &&
        value.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not ("." or "..") &&
            !segment.StartsWith(".", StringComparison.Ordinal) &&
            !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsMetadataValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);
}
