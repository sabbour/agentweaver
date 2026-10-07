using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public enum SourceControlFailureCode
{
    InvalidBinding,
    CapabilityUnavailable,
    InvalidRequest,
    NotFound,
    PermissionDenied,
    Conflict,
    StaleRevision,
    PullRequestMismatch,
    RemoteOutcomeUncertain,
    RateLimited,
    RemoteUnavailable,
    InvalidResponse
}

public sealed class SourceControlOperationException(
    SourceControlFailureCode code,
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public SourceControlFailureCode Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class GitHubSourceControlAdapter : ISourceControlAdapter
{
    public const int CurrentOptionsSchemaVersion = 1;
    private const string GitHubApiVersion = "2022-11-28";
    private readonly HttpClient _httpClient;
    public GitHubSourceControlAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is not { IsAbsoluteUri: true } address ||
            address.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(address.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            address.AbsolutePath != "/")
            throw new ArgumentException(
                "The GitHub API client must use the HTTPS api.github.com origin.", nameof(httpClient));
    }

    public string ProviderId => SourceControlProviderIds.GitHub;

    public ImmutableHashSet<string> AdvertisedCapabilities { get; } =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.RepositoryCheckout,
            SourceControlCapabilities.IssueWrite,
            SourceControlCapabilities.PullRequestRead,
            SourceControlCapabilities.PullRequestWrite,
            SourceControlCapabilities.ReviewRead,
            SourceControlCapabilities.Merge);

    public static ProviderDescriptor CreateDescriptor() =>
        new(
            ProviderSeam.SourceControl,
            SourceControlProviderIds.GitHub,
            new Version(1, 0, 0),
            CurrentOptionsSchemaVersion,
            ProviderHostingPattern.InProcess,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                SourceControlCapabilities.RepositoryRead,
                SourceControlCapabilities.RepositoryCheckout,
                SourceControlCapabilities.IssueWrite,
                SourceControlCapabilities.PullRequestRead,
                SourceControlCapabilities.PullRequestWrite,
                SourceControlCapabilities.ReviewRead,
                SourceControlCapabilities.Merge));

    public async Task<SourceControlRepositoryNegotiation> NegotiateRepositoryAsync(
        ProviderCandidate candidate,
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        if (candidate.Seam != ProviderSeam.SourceControl ||
            !string.Equals(candidate.ProviderId, ProviderId, StringComparison.Ordinal) ||
            candidate.OptionsSchemaVersion != CurrentOptionsSchemaVersion ||
            !candidate.AdvertisedCapabilities.IsSupersetOf(AdvertisedCapabilities))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidBinding,
                "The selected provider candidate does not match this GitHub adapter and options schema.");

        var info = await ReadRepositoryAsync(repository, credential, cancellationToken).ConfigureAwait(false);
        var capabilities = GetNegotiatedCapabilities(info);
        if (!candidate.AdvertisedCapabilities.IsSupersetOf(capabilities))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidBinding,
                "The GitHub repository negotiated a capability not advertised by the selected provider.");

        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl,
            ProviderId,
            info.Id.ToString(CultureInfo.InvariantCulture),
            info.CreatedAt.UtcDateTime.Ticks);
        return new SourceControlRepositoryNegotiation(
            repository,
            new ResourceNegotiation(resource, capabilities),
            info.Id,
            info.DefaultBranch,
            info.IsPrivate);
    }

    public async Task VerifyCurrentBindingAsync(
        SourceControlOperationContext context,
        string requiredCapability,
        CancellationToken cancellationToken)
    {
        if (!AdvertisedCapabilities.Contains(requiredCapability))
            throw new SourceControlOperationException(
                SourceControlFailureCode.CapabilityUnavailable,
                "The GitHub adapter does not advertise the requested capability.");
        ValidateContext(context, requiredCapability);
        var info = await ReadRepositoryAsync(
            context.Repository, context.Credential, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                context.Binding.Resource.ResourceId,
                info.Id.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            context.Binding.Resource.Generation != info.CreatedAt.UtcDateTime.Ticks)
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidBinding,
                "The GitHub repository no longer matches the pinned resource identity and generation.");
        if (!GetNegotiatedCapabilities(info).Contains(requiredCapability))
            throw new SourceControlOperationException(
                SourceControlFailureCode.CapabilityUnavailable,
                "The current GitHub credential does not have the required repository capability.");
    }

    public async Task<SourceControlIssue> CreateIssueAsync(
        SourceControlOperationContext context,
        SourceControlIssueRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIssueRequest(request);
        ValidateContext(context, SourceControlCapabilities.IssueWrite);
        using var response = await SendAsync(
            HttpMethod.Post,
            RepositoryUri(context.Repository, "issues"),
            context.Credential,
            JsonContent.Create(new { title = request.Title, body = request.Body }),
            cancellationToken,
            operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub issue creation", writeMayHaveChangedRemoteState: true);
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return new SourceControlIssue(
                ReadPositiveInt64(root, "number"),
                ReadRequiredString(root, "title"),
                ReadGitHubUrl(root, "html_url"),
                ReadRequiredString(root, "state"));
        }
        catch (SourceControlOperationException exception) when (
            exception.Code == SourceControlFailureCode.InvalidResponse)
        {
            throw UncertainWriteResponse(exception);
        }
    }

    public async Task<SourceControlPullRequest> CreateOrReusePullRequestAsync(
        SourceControlOperationContext context,
        SourceControlPullRequestRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePullRequestRequest(request);
        ValidateContext(context, SourceControlCapabilities.PullRequestWrite);
        var uri = RepositoryUri(context.Repository, "pulls");
        using var response = await SendAsync(
            HttpMethod.Post,
            uri,
            context.Credential,
            JsonContent.Create(new
            {
                title = request.Title,
                body = request.Body,
                head = context.Repository.Owner + ":" + request.HeadBranch,
                @base = request.BaseBranch,
                draft = request.Draft
            }),
            cancellationToken,
            operationMayHaveChangedRemoteState: true).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var matching = await FindOpenPullRequestAsync(
                context, request, cancellationToken).ConfigureAwait(false);
            if (matching is not null)
                return matching with { Disposition = SourceControlPullRequestDisposition.Reused };
            throw new SourceControlOperationException(
                SourceControlFailureCode.Conflict,
                "GitHub rejected pull request creation and no exact matching open pull request exists.",
                response.StatusCode);
        }

        EnsureSuccess(response, "GitHub pull request creation", writeMayHaveChangedRemoteState: true);
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var created = ParsePullRequest(
                document.RootElement,
                context.Repository,
                SourceControlPullRequestDisposition.Created);
            EnsureExactPullRequest(created, request);
            return created;
        }
        catch (SourceControlOperationException exception) when (
            exception.Code == SourceControlFailureCode.InvalidResponse)
        {
            throw UncertainWriteResponse(exception);
        }
    }

    public async Task<SourceControlPullRequest> ReadPullRequestAsync(
        SourceControlOperationContext context,
        long pullRequestNumber,
        CancellationToken cancellationToken)
    {
        ValidatePullRequestNumber(pullRequestNumber);
        ValidateContext(context, SourceControlCapabilities.PullRequestRead);
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(context.Repository, $"pulls/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}"),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub pull request read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParsePullRequest(
            document.RootElement,
            context.Repository,
            SourceControlPullRequestDisposition.Observed);
    }

    public async Task<ImmutableArray<SourceControlReview>> ReadReviewsAsync(
        SourceControlOperationContext context,
        long pullRequestNumber,
        CancellationToken cancellationToken)
    {
        ValidatePullRequestNumber(pullRequestNumber);
        ValidateContext(context, SourceControlCapabilities.ReviewRead);
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(
                context.Repository,
                $"pulls/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}/reviews?per_page=100"),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub pull request review read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw InvalidResponse("GitHub returned an invalid pull request review response.");

        var reviews = ImmutableArray.CreateBuilder<SourceControlReview>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var stateText = ReadRequiredString(item, "state");
            var state = stateText.ToUpperInvariant() switch
            {
                "APPROVED" => SourceControlReviewState.Approved,
                "CHANGES_REQUESTED" => SourceControlReviewState.ChangesRequested,
                "COMMENTED" => SourceControlReviewState.Commented,
                "PENDING" => SourceControlReviewState.Pending,
                "DISMISSED" => SourceControlReviewState.Dismissed,
                _ => SourceControlReviewState.Unknown
            };
            DateTimeOffset? submittedAt = item.TryGetProperty("submitted_at", out var submitted) &&
                                          submitted.ValueKind == JsonValueKind.String &&
                                          DateTimeOffset.TryParse(
                                              submitted.GetString(),
                                              CultureInfo.InvariantCulture,
                                              DateTimeStyles.AssumeUniversal,
                                              out var parsedSubmitted)
                ? parsedSubmitted
                : null;
            reviews.Add(new SourceControlReview(
                ReadPositiveInt64(item, "id"),
                ReadNestedRequiredString(item, "user", "login"),
                state,
                submittedAt));
        }

        return reviews.ToImmutable();
    }

    public async Task<SourceControlMergeReadiness> ReadMergeReadinessAsync(
        SourceControlOperationContext context,
        SourceControlMergeReadinessRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMergeReadinessRequest(request);
        ValidateContext(context, SourceControlCapabilities.Merge);

        using var pullRequestResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(
                context.Repository,
                $"pulls/{request.PullRequestNumber.ToString(CultureInfo.InvariantCulture)}"),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(pullRequestResponse, "GitHub pull request read for merge readiness");
        using var pullRequestDocument = await ReadJsonAsync(
            pullRequestResponse, cancellationToken).ConfigureAwait(false);
        var pullRequest = ParsePullRequest(
            pullRequestDocument.RootElement,
            context.Repository,
            SourceControlPullRequestDisposition.Observed);

        var requiredChecks = await ReadRequiredChecksAsync(
            context,
            request.ExpectedBaseBranch,
            cancellationToken).ConfigureAwait(false);
        var evidence = requiredChecks.IsEmpty
            ? ImmutableArray<SourceControlRequiredCheckEvidence>.Empty
            : await ReadRequiredCheckEvidenceAsync(
                context,
                request.ExpectedHeadSha,
                requiredChecks,
                cancellationToken).ConfigureAwait(false);

        return new SourceControlMergeReadiness(
            request,
            pullRequest,
            RequirementsKnown: true,
            evidence);
    }

    public async Task<SourceControlMergeOutcome> MergePullRequestAsync(
        SourceControlOperationContext context,
        SourceControlMergeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMergeRequest(request);
        ValidateContext(context, SourceControlCapabilities.Merge);

        using var response = await SendAsync(
            HttpMethod.Put,
            RepositoryUri(
                context.Repository,
                $"pulls/{request.PullRequestNumber.ToString(CultureInfo.InvariantCulture)}/merge"),
            context.Credential,
            JsonContent.Create(new
            {
                sha = request.ExpectedHeadSha,
                merge_method = request.Method switch
                {
                    SourceControlMergeMethod.Merge => "merge",
                    SourceControlMergeMethod.Squash => "squash",
                    SourceControlMergeMethod.Rebase => "rebase",
                    _ => throw new SourceControlOperationException(
                        SourceControlFailureCode.InvalidRequest,
                        "The requested GitHub merge method is invalid.")
                }
            }),
            cancellationToken,
            operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub pull request merge", writeMayHaveChangedRemoteState: true);
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("merged", out var merged) ||
                merged.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new SourceControlOperationException(
                    SourceControlFailureCode.RemoteOutcomeUncertain,
                    "GitHub returned an incomplete merge result.");
            if (merged.ValueKind == JsonValueKind.False)
                throw new SourceControlOperationException(
                    SourceControlFailureCode.Conflict,
                    "GitHub did not merge the requested pull request.");
            if (!root.TryGetProperty("sha", out var sha) ||
                sha.ValueKind != JsonValueKind.String ||
                !IsGitSha(sha.GetString()))
                throw new SourceControlOperationException(
                    SourceControlFailureCode.RemoteOutcomeUncertain,
                    "GitHub confirmed the merge but returned no usable merge commit identifier.");

            return new SourceControlMergeOutcome(sha.GetString()!, request.Method);
        }
        catch (SourceControlOperationException exception) when (
            exception.Code == SourceControlFailureCode.InvalidResponse)
        {
            throw UncertainWriteResponse(exception);
        }
    }

    private async Task<ImmutableArray<RequiredCheck>> ReadRequiredChecksAsync(
        SourceControlOperationContext context,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(
                context.Repository,
                "rules/branches/" + Uri.EscapeDataString(baseBranch)),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureMergeEvidenceSuccess(response, "GitHub effective branch rules read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw UnavailableMergeEvidence("GitHub returned unknown effective branch rules.");

        var requirements = ImmutableArray.CreateBuilder<RequiredCheck>();
        foreach (var rule in document.RootElement.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object ||
                !rule.TryGetProperty("type", out var typeValue) ||
                typeValue.ValueKind != JsonValueKind.String)
                throw UnavailableMergeEvidence("GitHub returned an unrecognized branch rule.");

            var type = typeValue.GetString();
            switch (type)
            {
                case "required_status_checks":
                    ParseRequiredStatusChecks(rule, requirements);
                    break;
                case "merge_queue":
                case "required_workflows":
                case "required_deployments":
                case "code_scanning":
                case "copilot_code_review":
                    throw UnavailableMergeEvidence(
                        "The GitHub branch rules require a merge capability this provider cannot verify.");
                case "creation":
                case "update":
                case "deletion":
                case "required_linear_history":
                case "required_signatures":
                case "non_fast_forward":
                case "pull_request":
                case "required_conversation_resolution":
                case "commit_message_pattern":
                case "commit_message_subject_pattern":
                case "branch_name_pattern":
                case "committer_email_pattern":
                case "file_path_restriction":
                case "max_file_path_length":
                case "max_file_size":
                    break;
                default:
                    throw UnavailableMergeEvidence(
                        "GitHub returned an unknown branch rule; required merge evidence cannot be established.");
            }
        }

        return requirements
            .Distinct()
            .ToImmutableArray();
    }

    private static void ParseRequiredStatusChecks(
        JsonElement rule,
        ImmutableArray<RequiredCheck>.Builder requirements)
    {
        if (!rule.TryGetProperty("parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("required_status_checks", out var checks) ||
            checks.ValueKind != JsonValueKind.Array)
            throw UnavailableMergeEvidence(
                "GitHub did not return the required status-check rule parameters.");

        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object ||
                !check.TryGetProperty("context", out var contextValue) ||
                contextValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(contextValue.GetString()) ||
                !check.TryGetProperty("integration_id", out var appValue))
                throw UnavailableMergeEvidence(
                    "GitHub returned an incomplete required status-check identity.");

            long? appId;
            if (appValue.ValueKind == JsonValueKind.Null)
                appId = null;
            else if (appValue.TryGetInt64(out var parsedAppId) && parsedAppId > 0)
                appId = parsedAppId;
            else
                throw UnavailableMergeEvidence(
                    "GitHub returned an invalid required status-check app identity.");

            requirements.Add(new RequiredCheck(contextValue.GetString()!, appId));
        }
    }

    private async Task<ImmutableArray<SourceControlRequiredCheckEvidence>> ReadRequiredCheckEvidenceAsync(
        SourceControlOperationContext context,
        string expectedHeadSha,
        ImmutableArray<RequiredCheck> requirements,
        CancellationToken cancellationToken)
    {
        var checkRunsTask = ReadCheckRunsAsync(context, expectedHeadSha, cancellationToken);
        var statusesTask = ReadCommitStatusesAsync(context, expectedHeadSha, cancellationToken);
        await Task.WhenAll(checkRunsTask, statusesTask).ConfigureAwait(false);
        var checkRuns = await checkRunsTask.ConfigureAwait(false);
        var statuses = await statusesTask.ConfigureAwait(false);

        return requirements.Select(requirement =>
        {
            var candidates = checkRuns
                .Where(check =>
                    string.Equals(check.Context, requirement.Context, StringComparison.Ordinal) &&
                    string.Equals(check.CommitSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase) &&
                    (requirement.AppId is null || check.AppId == requirement.AppId))
                .Select(check => new SourceControlRequiredCheckEvidence(
                    requirement.Context,
                    check.AppId,
                    check.CommitSha,
                    check.State,
                    SourceControlCheckEvidenceSource.CheckRun))
                .Concat(requirement.AppId is null
                    ? statuses
                        .Where(status =>
                            string.Equals(status.Context, requirement.Context, StringComparison.Ordinal) &&
                            string.Equals(status.CommitSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
                        .Select(status => new SourceControlRequiredCheckEvidence(
                            requirement.Context,
                            AppId: null,
                            status.CommitSha,
                            status.State,
                            SourceControlCheckEvidenceSource.CommitStatus))
                    : [])
                .ToArray();

            return candidates.Length == 1
                ? candidates[0]
                : new SourceControlRequiredCheckEvidence(
                    requirement.Context,
                    requirement.AppId,
                    CommitSha: null,
                    SourceControlCheckState.Unknown,
                    SourceControlCheckEvidenceSource.Unknown);
        }).ToImmutableArray();
    }

    private async Task<ImmutableArray<ObservedCheck>> ReadCheckRunsAsync(
        SourceControlOperationContext context,
        string expectedHeadSha,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(
                context.Repository,
                $"commits/{expectedHeadSha}/check-runs?filter=latest&per_page=100"),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureMergeEvidenceSuccess(response, "GitHub check-run read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sha", out var shaValue) ||
            shaValue.ValueKind != JsonValueKind.String ||
            !string.Equals(shaValue.GetString(), expectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("total_count", out var totalValue) ||
            !totalValue.TryGetInt32(out var totalCount) ||
            totalCount < 0 ||
            !root.TryGetProperty("check_runs", out var checkRunsValue) ||
            checkRunsValue.ValueKind != JsonValueKind.Array)
            throw UnavailableMergeEvidence(
                "GitHub returned incomplete check-run evidence for the exact head revision.");

        var results = ImmutableArray.CreateBuilder<ObservedCheck>();
        foreach (var checkRun in checkRunsValue.EnumerateArray())
        {
            if (checkRun.ValueKind != JsonValueKind.Object ||
                !checkRun.TryGetProperty("name", out var nameValue) ||
                nameValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameValue.GetString()) ||
                !checkRun.TryGetProperty("head_sha", out var headValue) ||
                headValue.ValueKind != JsonValueKind.String ||
                !IsGitSha(headValue.GetString()) ||
                !checkRun.TryGetProperty("status", out var statusValue) ||
                statusValue.ValueKind != JsonValueKind.String)
                throw UnavailableMergeEvidence(
                    "GitHub returned an incomplete check-run record.");

            var appId = ReadOptionalCheckRunAppId(checkRun);
            var state = ParseCheckRunState(
                statusValue.GetString()!,
                checkRun.TryGetProperty("conclusion", out var conclusionValue) &&
                conclusionValue.ValueKind == JsonValueKind.String
                    ? conclusionValue.GetString()
                    : null);
            results.Add(new ObservedCheck(
                nameValue.GetString()!,
                appId,
                headValue.GetString()!,
                state));
        }

        if (totalCount != results.Count)
            throw UnavailableMergeEvidence(
                "GitHub returned a truncated check-run list; exact required checks cannot be established.");
        return results.ToImmutable();
    }

    private async Task<ImmutableArray<ObservedCheck>> ReadCommitStatusesAsync(
        SourceControlOperationContext context,
        string expectedHeadSha,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(
                context.Repository,
                $"commits/{expectedHeadSha}/status?per_page=100"),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureMergeEvidenceSuccess(response, "GitHub commit-status read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sha", out var shaValue) ||
            shaValue.ValueKind != JsonValueKind.String ||
            !string.Equals(shaValue.GetString(), expectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("total_count", out var totalValue) ||
            !totalValue.TryGetInt32(out var totalCount) ||
            totalCount < 0 ||
            !root.TryGetProperty("statuses", out var statusesValue) ||
            statusesValue.ValueKind != JsonValueKind.Array)
            throw UnavailableMergeEvidence(
                "GitHub returned incomplete commit-status evidence for the exact head revision.");

        var results = ImmutableArray.CreateBuilder<ObservedCheck>();
        foreach (var status in statusesValue.EnumerateArray())
        {
            if (status.ValueKind != JsonValueKind.Object ||
                !status.TryGetProperty("context", out var contextValue) ||
                contextValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(contextValue.GetString()) ||
                !status.TryGetProperty("state", out var stateValue) ||
                stateValue.ValueKind != JsonValueKind.String)
                throw UnavailableMergeEvidence(
                    "GitHub returned an incomplete commit-status record.");

            results.Add(new ObservedCheck(
                contextValue.GetString()!,
                AppId: null,
                expectedHeadSha,
                ParseCommitStatusState(stateValue.GetString()!)));
        }

        if (totalCount != results.Count)
            throw UnavailableMergeEvidence(
                "GitHub returned a truncated commit-status list; exact required checks cannot be established.");
        return results.ToImmutable();
    }

    private static long? ReadOptionalCheckRunAppId(JsonElement checkRun)
    {
        if (!checkRun.TryGetProperty("app", out var appValue) ||
            appValue.ValueKind == JsonValueKind.Null)
            return null;
        if (appValue.ValueKind != JsonValueKind.Object ||
            !appValue.TryGetProperty("id", out var idValue) ||
            !idValue.TryGetInt64(out var appId) ||
            appId <= 0)
            throw UnavailableMergeEvidence(
                "GitHub returned an incomplete check-run app identity.");
        return appId;
    }

    private static SourceControlCheckState ParseCheckRunState(string status, string? conclusion)
    {
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return status.ToLowerInvariant() switch
            {
                "queued" or "in_progress" or "requested" or "waiting" or "pending" =>
                    SourceControlCheckState.Pending,
                _ => SourceControlCheckState.Unknown
            };

        return conclusion?.ToLowerInvariant() switch
        {
            "success" => SourceControlCheckState.Success,
            "failure" or "startup_failure" => SourceControlCheckState.Failure,
            "action_required" => SourceControlCheckState.ActionRequired,
            "cancelled" => SourceControlCheckState.Cancelled,
            "timed_out" => SourceControlCheckState.TimedOut,
            "skipped" => SourceControlCheckState.Skipped,
            "neutral" => SourceControlCheckState.Neutral,
            "stale" => SourceControlCheckState.Unknown,
            "error" => SourceControlCheckState.Error,
            _ => SourceControlCheckState.Unknown
        };
    }

    private static SourceControlCheckState ParseCommitStatusState(string state) =>
        state.ToLowerInvariant() switch
        {
            "success" => SourceControlCheckState.Success,
            "pending" => SourceControlCheckState.Pending,
            "failure" => SourceControlCheckState.Failure,
            "error" => SourceControlCheckState.Error,
            _ => SourceControlCheckState.Unknown
        };

    private static void EnsureMergeEvidenceSuccess(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw UnavailableMergeEvidence(
                operation + " is unavailable; the effective merge requirements could not be verified.");
        EnsureSuccess(response, operation);
    }

    private static SourceControlOperationException UnavailableMergeEvidence(string message) =>
        new(SourceControlFailureCode.CapabilityUnavailable, message);

    private async Task<SourceControlPullRequest?> FindOpenPullRequestAsync(
        SourceControlOperationContext context,
        SourceControlPullRequestRequest request,
        CancellationToken cancellationToken)
    {
        var query = "?state=open&per_page=100&head=" +
                    Uri.EscapeDataString(context.Repository.Owner + ":" + request.HeadBranch) +
                    "&base=" + Uri.EscapeDataString(request.BaseBranch);
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(context.Repository, "pulls" + query),
            context.Credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub pull request lookup");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw InvalidResponse("GitHub returned an invalid pull request lookup response.");

        var candidates = document.RootElement.EnumerateArray()
            .Select(element => ParsePullRequest(
                element, context.Repository, SourceControlPullRequestDisposition.Observed))
            .ToArray();
        var exact = candidates.Where(candidate =>
                string.Equals(
                    candidate.HeadSha, request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.BaseSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.HeadBranch, request.HeadBranch, StringComparison.Ordinal) &&
                string.Equals(candidate.BaseBranch, request.BaseBranch, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length > 1)
            throw new SourceControlOperationException(
                SourceControlFailureCode.Conflict,
                "GitHub returned multiple open pull requests for the exact requested revision.");
        if (exact.Length == 1)
        {
            EnsureExactPullRequest(exact[0], request);
            return exact[0];
        }
        if (candidates.Length > 0)
            throw new SourceControlOperationException(
                SourceControlFailureCode.PullRequestMismatch,
                "An open pull request exists for the requested branches but its head or base revision differs.");
        return null;
    }

    private async Task<RepositoryInfo> ReadRepositoryAsync(
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, string.Empty),
            credential,
            content: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub repository read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var fullName = ReadRequiredString(root, "full_name");
        var actual = ParseRepositoryIdentity(fullName);
        EnsureRepositoryMatches(repository, actual);
        if (!root.TryGetProperty("permissions", out var permissions) ||
            permissions.ValueKind != JsonValueKind.Object)
            permissions = default;

        var canPull = ReadOptionalBoolean(permissions, "pull");
        var canPush = ReadOptionalBoolean(permissions, "push");
        var createdAt = ReadDateTimeOffset(root, "created_at");
        if (createdAt.UtcDateTime.Ticks <= 0)
            throw InvalidResponse("GitHub returned an invalid repository creation timestamp.");
        return new RepositoryInfo(
            ReadPositiveInt64(root, "id"),
            actual,
            ReadRequiredString(root, "default_branch"),
            ReadRequiredBoolean(root, "private"),
            createdAt,
            canPull,
            canPush);
    }

    private static ImmutableHashSet<string> GetNegotiatedCapabilities(RepositoryInfo info)
    {
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        capabilities.Add(SourceControlCapabilities.RepositoryRead);
        if (info.CanPull)
        {
            capabilities.Add(SourceControlCapabilities.RepositoryCheckout);
            capabilities.Add(SourceControlCapabilities.PullRequestRead);
            capabilities.Add(SourceControlCapabilities.ReviewRead);
        }
        if (info.CanPush)
        {
            capabilities.Add(SourceControlCapabilities.IssueWrite);
            capabilities.Add(SourceControlCapabilities.PullRequestWrite);
            capabilities.Add(SourceControlCapabilities.Merge);
        }
        return capabilities.ToImmutable();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        SecretCredential credential,
        HttpContent? content,
        CancellationToken cancellationToken,
        bool operationMayHaveChangedRemoteState = false)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Agentweaver-SourceControl/1.0");
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential.GetValue());
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new SourceControlOperationException(
                operationMayHaveChangedRemoteState
                    ? SourceControlFailureCode.RemoteOutcomeUncertain
                    : SourceControlFailureCode.RemoteUnavailable,
                operationMayHaveChangedRemoteState
                    ? "GitHub did not return a definitive result for the write request."
                    : "The GitHub request could not be completed.",
                innerException: exception);
        }
        catch (OperationCanceledException exception) when (
            operationMayHaveChangedRemoteState || !cancellationToken.IsCancellationRequested)
        {
            throw new SourceControlOperationException(
                operationMayHaveChangedRemoteState
                    ? SourceControlFailureCode.RemoteOutcomeUncertain
                    : SourceControlFailureCode.RemoteUnavailable,
                operationMayHaveChangedRemoteState
                    ? "GitHub did not return a definitive result for the write request."
                    : "The GitHub request timed out.",
                innerException: exception);
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new SourceControlOperationException(
                SourceControlFailureCode.RemoteUnavailable,
                "GitHub redirected an authenticated request; redirects are not followed.",
                status);
        }
        return response;
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string operation,
        bool writeMayHaveChangedRemoteState = false)
    {
        if (response.IsSuccessStatusCode)
            return;

        var statusCode = response.StatusCode;
        var uncertainWrite = writeMayHaveChangedRemoteState &&
                             ((int)statusCode >= 500 || statusCode == HttpStatusCode.RequestTimeout);
        var code = response.StatusCode switch
        {
            HttpStatusCode.NotFound => SourceControlFailureCode.NotFound,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                SourceControlFailureCode.PermissionDenied,
            HttpStatusCode.Conflict => SourceControlFailureCode.Conflict,
            HttpStatusCode.TooManyRequests => SourceControlFailureCode.RateLimited,
            HttpStatusCode.BadRequest => SourceControlFailureCode.InvalidRequest,
            HttpStatusCode.UnprocessableEntity => SourceControlFailureCode.Conflict,
            HttpStatusCode.MethodNotAllowed => SourceControlFailureCode.CapabilityUnavailable,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                SourceControlFailureCode.RemoteUnavailable,
            _ => SourceControlFailureCode.RemoteUnavailable
        };
        var finalCode = uncertainWrite ? SourceControlFailureCode.RemoteOutcomeUncertain : code;
        throw new SourceControlOperationException(
            finalCode,
            operation + " failed with HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".",
            response.StatusCode);
    }

    private static SourceControlOperationException UncertainWriteResponse(Exception exception) =>
        new(
            SourceControlFailureCode.RemoteOutcomeUncertain,
            "GitHub accepted a write request but returned an unusable response.",
            innerException: exception);

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidResponse,
                "GitHub returned malformed JSON.",
                response.StatusCode,
                exception);
        }
    }

    private static SourceControlPullRequest ParsePullRequest(
        JsonElement root,
        SourceControlRepositoryIdentity expectedRepository,
        SourceControlPullRequestDisposition disposition)
    {
        var head = ReadObject(root, "head");
        var @base = ReadObject(root, "base");
        EnsureRepositoryMatches(expectedRepository, ReadRepositoryName(head));
        EnsureRepositoryMatches(expectedRepository, ReadRepositoryName(@base));
        if (!root.TryGetProperty("merged", out var merged) ||
            merged.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidResponse("GitHub returned a pull request without a merge state.");

        return new SourceControlPullRequest(
            ReadPositiveInt64(root, "number"),
            ReadGitHubUrl(root, "html_url"),
            ReadRequiredString(root, "state"),
            ReadRequiredString(head, "ref"),
            ReadRequiredString(head, "sha"),
            ReadRequiredString(@base, "ref"),
            ReadRequiredString(@base, "sha"),
            merged.ValueKind == JsonValueKind.True,
            disposition);
    }

    private static void EnsureExactPullRequest(
        SourceControlPullRequest pullRequest,
        SourceControlPullRequestRequest request)
    {
        if (!string.Equals(pullRequest.HeadBranch, request.HeadBranch, StringComparison.Ordinal) ||
            !string.Equals(pullRequest.HeadSha, request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pullRequest.BaseBranch, request.BaseBranch, StringComparison.Ordinal) ||
            !string.Equals(pullRequest.BaseSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase))
            throw new SourceControlOperationException(
                SourceControlFailureCode.PullRequestMismatch,
                "The GitHub pull request does not match the exact requested head and base.");
    }

    private static SourceControlRepositoryIdentity ReadRepositoryName(JsonElement pullRequestOrRepository)
    {
        var repository = pullRequestOrRepository.TryGetProperty("repo", out var repo)
            ? repo
            : pullRequestOrRepository;
        var fullName = ReadRequiredString(repository, "full_name");
        return ParseRepositoryIdentity(fullName);
    }

    private static SourceControlRepositoryIdentity ParseRepositoryIdentity(string fullName)
    {
        var parts = fullName.Split('/');
        if (parts.Length != 2)
            throw InvalidResponse("GitHub returned an invalid repository identity.");
        try
        {
            return new SourceControlRepositoryIdentity(parts[0], parts[1]);
        }
        catch (ArgumentException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidResponse,
                "GitHub returned an invalid repository identity.",
                innerException: exception);
        }
    }

    private static void EnsureRepositoryMatches(
        SourceControlRepositoryIdentity expected,
        SourceControlRepositoryIdentity actual)
    {
        if (!string.Equals(expected.Owner, actual.Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidBinding,
                "GitHub returned a different repository than the requested identity.");
    }

    private static Uri RepositoryUri(SourceControlRepositoryIdentity repository, string relative) =>
        new(
            new Uri("https://api.github.com/"),
            "repos/" + Uri.EscapeDataString(repository.Owner) + "/" +
            Uri.EscapeDataString(repository.Name) +
            (relative.Length == 0 ? string.Empty : "/" + relative));

    private static void ValidateContext(SourceControlOperationContext context, string capability)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Binding is null ||
            context.Binding.Seam != ProviderSeam.SourceControl ||
            context.Binding.Resource.Seam != ProviderSeam.SourceControl ||
            !string.Equals(context.Binding.ProviderId, SourceControlProviderIds.GitHub, StringComparison.Ordinal) ||
            !string.Equals(
                context.Binding.Resource.ProviderId, SourceControlProviderIds.GitHub, StringComparison.Ordinal) ||
            !context.Binding.NegotiatedCapabilities.Contains(capability) ||
            context.Credential is null ||
            string.IsNullOrWhiteSpace(context.Binding.RunId))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidBinding,
                "The source-control operation is not bound to the selected GitHub provider, resource, and run.");
    }

    private static void ValidateIssueRequest(SourceControlIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 256 ||
            request.Body is { Length: > 65_536 })
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The issue title or body is invalid.");
    }

    private static void ValidatePullRequestRequest(SourceControlPullRequestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 256 ||
            request.Body is { Length: > 65_536 } ||
            !IsGitSha(request.ExpectedHeadSha) ||
            !IsGitSha(request.ExpectedBaseSha) ||
            !IsBranchName(request.HeadBranch) ||
            !IsBranchName(request.BaseBranch))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The pull request title, branch, or expected head is invalid.");
    }

    private static void ValidateMergeRequest(SourceControlMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePullRequestNumber(request.PullRequestNumber);
        if (!IsGitSha(request.ExpectedHeadSha) ||
            !IsGitSha(request.ExpectedBaseSha) ||
            !Enum.IsDefined(request.Method))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The merge request must contain exact head and base revisions and a supported method.");
    }

    private static void ValidateMergeReadinessRequest(SourceControlMergeReadinessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePullRequestNumber(request.PullRequestNumber);
        if (!IsBranchName(request.ExpectedHeadBranch) ||
            !IsGitSha(request.ExpectedHeadSha) ||
            !IsBranchName(request.ExpectedBaseBranch) ||
            !IsGitSha(request.ExpectedBaseSha))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The merge-readiness request must contain exact branch names and head/base revisions.");
    }

    private static bool IsBranchName(string branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        branch.Length <= 255 &&
        branch[0] != '-' &&
        branch.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not ("." or "..") &&
            !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.' ));

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static void ValidatePullRequestNumber(long number)
    {
        if (number <= 0)
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "A positive pull request number is required.");
    }

    private static JsonElement ReadObject(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
            return value;
        throw InvalidResponse("GitHub returned an incomplete pull request response.");
    }

    private static string ReadRequiredString(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw InvalidResponse("GitHub returned an incomplete response.");
    }

    private static string ReadNestedRequiredString(
        JsonElement source,
        string parent,
        string property) =>
        ReadRequiredString(ReadObject(source, parent), property);

    private static long ReadPositiveInt64(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) &&
            value.TryGetInt64(out var number) &&
            number > 0)
            return number;
        throw InvalidResponse("GitHub returned an invalid numeric identifier.");
    }

    private static bool ReadRequiredBoolean(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        throw InvalidResponse("GitHub returned an incomplete response.");
    }

    private static bool ReadOptionalBoolean(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset ReadDateTimeOffset(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
            return parsed;
        throw InvalidResponse("GitHub returned an invalid timestamp.");
    }

    private static Uri ReadGitHubUrl(JsonElement source, string property)
    {
        if (Uri.TryCreate(ReadRequiredString(source, property), UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return uri;
        throw InvalidResponse("GitHub returned an invalid web URL.");
    }

    private static SourceControlOperationException InvalidResponse(string message) =>
        new(SourceControlFailureCode.InvalidResponse, message);

    private sealed record RepositoryInfo(
        long Id,
        SourceControlRepositoryIdentity Repository,
        string DefaultBranch,
        bool IsPrivate,
        DateTimeOffset CreatedAt,
        bool CanPull,
        bool CanPush);

    private sealed record RequiredCheck(string Context, long? AppId);

    private sealed record ObservedCheck(
        string Context,
        long? AppId,
        string CommitSha,
        SourceControlCheckState State);
}
