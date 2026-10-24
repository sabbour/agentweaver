using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Purpose values used when Source Control requests API, checkout, webhook, or app credentials.</summary>
public static class SourceControlSecretPurposes
{
    /// <summary>Purpose for repository API calls.</summary>
    public const string Api = "source-control.api";
    /// <summary>Purpose for checkout operations.</summary>
    public const string Checkout = "source-control.checkout";
    /// <summary>Purpose for webhook signature verification.</summary>
    public const string Webhook = "source-control.webhook";
    /// <summary>Purpose for a GitHub App private key held by Identity.</summary>
    public const string GitHubAppPrivateKey = "source-control.github-app.private-key";
}

/// <summary>Credential ownership model configured for a Source Control repository.</summary>
[JsonConverter(typeof(SourceControlAuthModeJsonConverter))]
public enum SourceControlAuthMode
{
    Secret,
    GitHubApp
}

/// <summary>Maps the stable wire values for Source Control authentication modes.</summary>
public sealed class SourceControlAuthModeJsonConverter : JsonConverter<SourceControlAuthMode>
{
    /// <summary>Reads the supported secret or GitHub App spelling and rejects other JSON values.</summary>
    /// <exception cref="JsonException">The wire value is not a supported string.</exception>
    public override SourceControlAuthMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "secret" => SourceControlAuthMode.Secret,
                "githubApp" => SourceControlAuthMode.GitHubApp,
                "gitHubApp" => SourceControlAuthMode.GitHubApp,
                _ => throw new JsonException("The SourceControl auth mode is unsupported.")
            }
            : throw new JsonException("The SourceControl auth mode must be a string.");

    /// <summary>Writes the canonical lower-camel wire value for the authentication mode.</summary>
    /// <exception cref="JsonException">The enum value is not supported.</exception>
    public override void Write(
        Utf8JsonWriter writer,
        SourceControlAuthMode value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            SourceControlAuthMode.Secret => "secret",
            SourceControlAuthMode.GitHubApp => "githubApp",
            _ => throw new JsonException("The SourceControl auth mode is unsupported.")
        });
    }
}

/// <summary>Capabilities advertised by a Source Control provider adapter.</summary>
public static class SourceControlCapabilities
{
    public const string RepositoryRead = "repository.read";
    public const string RepositoryCheckout = "repository.checkout";
    public const string IssueWrite = "issue.write";
    public const string PullRequestRead = "pull-request.read";
    public const string PullRequestWrite = "pull-request.write";
    public const string ReviewRead = "review.read";
    public const string Merge = "merge";
}

/// <summary>Stable provider catalog IDs for Source Control implementations.</summary>
public static class SourceControlProviderIds
{
    public const string GitHub = "github";
}

/// <summary>Owner and repository name pinned by project Source Control settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceControlRepositoryIdentity
{
    /// <summary>Creates a repository identity with provider-compatible owner and name segments.</summary>
    /// <exception cref="ArgumentException">Either segment is invalid or exceeds its length limit.</exception>
    public SourceControlRepositoryIdentity(string owner, string name)
    {
        Owner = ValidateSegment(owner, nameof(owner), allowDots: false);
        Name = ValidateSegment(name, nameof(name), allowDots: true);
    }

    public string Owner { get; }
    public string Name { get; }
    /// <summary>Canonical owner/name form used for display and provider requests.</summary>
    [JsonIgnore]
    public string FullName => Owner + "/" + Name;

    /// <summary>Checks one repository path segment, allowing dots only in repository names.</summary>
    private static string ValidateSegment(string value, string paramName, bool allowDots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.Length > 100 ||
            value is "." or ".." ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' &&
                    !(allowDots && character is ('.' or '_'))))
            throw new ArgumentException("Repository identity contains an invalid owner or name.", paramName);
        return value;
    }
}

/// <summary>
/// Projects &amp; Config settings that select one repository and its auth mode.
/// Secret references are versioned identities only; secret bytes remain in Identity.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceControlProjectSettings
{
    /// <summary>Creates repository settings and enforces the selected auth mode's required references.</summary>
    /// <exception cref="ArgumentException">The auth mode and secret/Identity references are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The auth mode is unsupported.</exception>
    [JsonConstructor]
    public SourceControlProjectSettings(
        SourceControlRepositoryIdentity repository,
        SecretRef? apiSecretReference,
        SecretRef? checkoutSecretReference = null,
        SecretRef? webhookSecretReference = null,
        SourceControlAuthMode? authMode = null,
        string? identityConnectionId = null)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (authMode is not null and not (SourceControlAuthMode.Secret or SourceControlAuthMode.GitHubApp))
            throw new ArgumentOutOfRangeException(nameof(authMode));
        AuthMode = authMode;
        IdentityConnectionId = ValidateConnectionBinding(
            authMode, apiSecretReference, checkoutSecretReference, identityConnectionId);
        ApiSecretReference = apiSecretReference;
        CheckoutSecretReference = checkoutSecretReference;
        WebhookSecretReference = webhookSecretReference;
    }

    public SourceControlRepositoryIdentity Repository { get; }

    /// <summary>Authentication mode; absent in legacy settings and inferred by consumers as secret-based.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceControlAuthMode? AuthMode { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityConnectionId { get; }

    /// <summary>Exact Identity-owned API secret reference for secret-based authentication.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? ApiSecretReference { get; }

    /// <summary>Optional exact secret reference used by repository checkout.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? CheckoutSecretReference { get; }

    /// <summary>Optional exact secret reference used to verify Source Control webhooks.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? WebhookSecretReference { get; }

    /// <summary>Enforces mutually exclusive secret-based and Identity GitHub App configuration.</summary>
    private static string? ValidateConnectionBinding(
        SourceControlAuthMode? authMode,
        SecretRef? apiSecretReference,
        SecretRef? checkoutSecretReference,
        string? identityConnectionId)
    {
        if (authMode == SourceControlAuthMode.GitHubApp)
        {
            if (apiSecretReference is not null ||
                checkoutSecretReference is not null ||
                string.IsNullOrWhiteSpace(identityConnectionId) ||
                identityConnectionId.Length > 128 ||
                identityConnectionId.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                throw new ArgumentException(
                    "GitHub App settings require one Identity connection ID and cannot include API or checkout secrets.",
                    nameof(identityConnectionId));
            return identityConnectionId;
        }

        if (apiSecretReference is null)
            throw new ArgumentException(
                "Secret-authenticated SourceControl settings require an exact API secret reference.",
                nameof(apiSecretReference));
        if (identityConnectionId is not null)
            throw new ArgumentException(
                "Identity connection IDs are valid only for GitHub App SourceControl settings.",
                nameof(identityConnectionId));
        return null;
    }

}

/// <summary>Provider negotiation result for a configured repository and its opaque remote ID.</summary>
public sealed record SourceControlRepositoryNegotiation(
    SourceControlRepositoryIdentity Repository,
    ResourceNegotiation Resource,
    long ProviderRepositoryId,
    string DefaultBranch,
    bool IsPrivate);

/// <summary>Per-operation adapter input carrying the run-pinned provider, repository, and short-lived credential.</summary>
public sealed record SourceControlOperationContext(
    PinnedProviderBinding Binding,
    SourceControlRepositoryIdentity Repository,
    SecretCredential Credential);

/// <summary>Input for creating an issue in the selected repository.</summary>
public sealed record SourceControlIssueRequest(string Title, string? Body);

/// <summary>Provider-returned issue identity, URL, title, and state.</summary>
public sealed record SourceControlIssue(
    long Number,
    string Title,
    Uri HtmlUrl,
    string State);

/// <summary>Repository issue content used by periodic synchronization, excluding pull requests.</summary>
public sealed record SourceControlIssueContent(long Number, string Title, string Body, bool Closed);

/// <summary>A bounded page; the continuation is a provider page number, not a remote URL.</summary>
public sealed record SourceControlIssuePage(ImmutableArray<SourceControlIssueContent> Issues, int? NextPage);

/// <summary>Issue read and reconciliation capability, separate from run orchestration operations.</summary>
public interface ISourceControlIssueAdapter
{
    Task<SourceControlRepositoryNegotiation> NegotiateIssueRepositoryAsync(ProviderCandidate candidate,
        SourceControlRepositoryIdentity repository, SecretCredential credential, bool issueWriteGranted,
        CancellationToken cancellationToken);
    Task VerifyCurrentIssueBindingAsync(SourceControlOperationContext context, CancellationToken cancellationToken);
    Task<SourceControlIssuePage> ListIssuesAsync(SourceControlOperationContext context, int page,
        int pageSize, CancellationToken cancellationToken);
    Task<SourceControlIssueContent> ReadIssueAsync(SourceControlOperationContext context, long number,
        CancellationToken cancellationToken);
    Task<SourceControlIssueContent> UpdateIssueAsync(SourceControlOperationContext context,
        SourceControlIssueContent issue, CancellationToken cancellationToken);
}

/// <summary>
/// Pull-request creation request pinned to expected head and base commits so the adapter
/// can reject an unexpected repository state before creating or reusing a PR.
/// </summary>
public sealed record SourceControlPullRequestRequest(
    string Title,
    string? Body,
    string HeadBranch,
    string ExpectedHeadSha,
    string BaseBranch,
    string ExpectedBaseSha,
    bool Draft);

/// <summary>Reports whether a matching pull request was newly created, reused, or only observed.</summary>
public enum SourceControlPullRequestDisposition
{
    Created,
    Reused,
    Observed
}

/// <summary>Provider-returned pull request branches, commit pins, state, and disposition.</summary>
/// <param name="Title">
/// The provider's current title, or null when the provider didn't return one. Merge intents clear it,
/// so their stored request data doesn't depend on a title that can change remotely.
/// </param>
public sealed record SourceControlPullRequest(
    long Number,
    Uri HtmlUrl,
    string State,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    bool Merged,
    SourceControlPullRequestDisposition Disposition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null);

/// <summary>Normalized review decisions returned by a Source Control provider.</summary>
public enum SourceControlReviewState
{
    Approved,
    ChangesRequested,
    Commented,
    Pending,
    Dismissed,
    Unknown
}

/// <summary>Review identity, reviewer, decision, and optional submission time.</summary>
public sealed record SourceControlReview(
    long Id,
    string Reviewer,
    SourceControlReviewState State,
    DateTimeOffset? SubmittedAt);

/// <summary>Supported pull-request integration strategies.</summary>
public enum SourceControlMergeMethod
{
    Merge,
    Squash,
    Rebase
}

/// <summary>Merge request pinned to the exact expected pull-request head and base commits.</summary>
public sealed record SourceControlMergeRequest(
    long PullRequestNumber,
    string ExpectedHeadSha,
    string ExpectedBaseSha,
    SourceControlMergeMethod Method);

/// <summary>Expected branches and commit IDs against which the provider assesses merge readiness.</summary>
public sealed record SourceControlMergeReadinessRequest(
    long PullRequestNumber,
    string ExpectedHeadBranch,
    string ExpectedHeadSha,
    string ExpectedBaseBranch,
    string ExpectedBaseSha);

/// <summary>Normalized result state for a required check or commit status.</summary>
public enum SourceControlCheckState
{
    Success,
    Pending,
    Failure,
    Error,
    Cancelled,
    TimedOut,
    ActionRequired,
    Skipped,
    Neutral,
    Unknown
}

/// <summary>Identifies whether check evidence came from a check run or commit status API.</summary>
public enum SourceControlCheckEvidenceSource
{
    CheckRun,
    CommitStatus,
    Unknown
}

/// <summary>One required check's exact context, app, commit, state, and evidence source.</summary>
public sealed record SourceControlRequiredCheckEvidence(
    string Context,
    long? AppId,
    string? CommitSha,
    SourceControlCheckState State,
    SourceControlCheckEvidenceSource Source);

/// <summary>
/// Readiness assessment tied to the requested branch and commit pins. Readiness is false
/// unless requirements are known, the PR remains open and unmerged, and all required checks
/// succeeded for the expected head SHA.
/// </summary>
public sealed record SourceControlMergeReadiness(
    SourceControlMergeReadinessRequest Request,
    SourceControlPullRequest PullRequest,
    bool RequirementsKnown,
    ImmutableArray<SourceControlRequiredCheckEvidence> RequiredChecks)
{
    /// <summary>Whether the exact requested PR state and every required check permit merge.</summary>
    public bool IsReady =>
        RequirementsKnown &&
        string.Equals(PullRequest.State, "open", StringComparison.OrdinalIgnoreCase) &&
        !PullRequest.Merged &&
        string.Equals(PullRequest.HeadBranch, Request.ExpectedHeadBranch, StringComparison.Ordinal) &&
        string.Equals(PullRequest.HeadSha, Request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(PullRequest.BaseBranch, Request.ExpectedBaseBranch, StringComparison.Ordinal) &&
        string.Equals(PullRequest.BaseSha, Request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase) &&
        !RequiredChecks.IsDefault &&
        RequiredChecks.All(check =>
            check.State == SourceControlCheckState.Success &&
            string.Equals(check.CommitSha, Request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Provider-confirmed merge commit and strategy.</summary>
public sealed record SourceControlMergeOutcome(
    string MergeSha,
    SourceControlMergeMethod Method);

/// <summary>
/// Provider-neutral operations for repository negotiation, issue/PR access, reviews,
/// check readiness, and merging. Implementations must honor each run binding and its
/// required capability before performing external provider calls.
/// </summary>
public interface ISourceControlAdapter
{
    /// <summary>Stable provider catalog ID for this adapter.</summary>
    string ProviderId { get; }
    /// <summary>Capabilities this adapter can provide for a selected repository.</summary>
    ImmutableHashSet<string> AdvertisedCapabilities { get; }

    /// <summary>Validates repository access and returns the opaque provider resource negotiation.</summary>
    Task<SourceControlRepositoryNegotiation> NegotiateRepositoryAsync(
        ProviderCandidate candidate,
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        CancellationToken cancellationToken);

    /// <summary>Rechecks the current run binding and required capability before an operation.</summary>
    Task VerifyCurrentBindingAsync(
        SourceControlOperationContext context,
        string requiredCapability,
        CancellationToken cancellationToken);

    /// <summary>Creates an issue in the bound repository.</summary>
    Task<SourceControlIssue> CreateIssueAsync(
        SourceControlOperationContext context,
        SourceControlIssueRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates or reuses a pull request only when the branch and commit pins match.</summary>
    Task<SourceControlPullRequest> CreateOrReusePullRequestAsync(
        SourceControlOperationContext context,
        SourceControlPullRequestRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads one pull request from the bound repository.</summary>
    Task<SourceControlPullRequest> ReadPullRequestAsync(
        SourceControlOperationContext context,
        long pullRequestNumber,
        CancellationToken cancellationToken);

    /// <summary>Reads reviews for one pull request from the bound repository.</summary>
    Task<ImmutableArray<SourceControlReview>> ReadReviewsAsync(
        SourceControlOperationContext context,
        long pullRequestNumber,
        CancellationToken cancellationToken);

    /// <summary>Reads branch, head/base commit, and required-check evidence for merge eligibility.</summary>
    Task<SourceControlMergeReadiness> ReadMergeReadinessAsync(
        SourceControlOperationContext context,
        SourceControlMergeReadinessRequest request,
        CancellationToken cancellationToken);

    /// <summary>Merges only the pull request whose expected head and base still match the request.</summary>
    Task<SourceControlMergeOutcome> MergePullRequestAsync(
        SourceControlOperationContext context,
        SourceControlMergeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>One regular file from a repository's explicit team-sync tree.</summary>
public sealed record SourceControlTeamSyncFile(string Path, string BlobSha, byte[] Content, bool Executable);

/// <summary>Current `.squad/` files and the repository revision from which they were read.</summary>
public sealed record SourceControlTeamSyncSnapshot(
    string Repository,
    string Reference,
    string CommitSha,
    ImmutableArray<SourceControlTeamSyncFile> Files);

/// <summary>A path update or deletion guarded by the blob SHA observed during preview.</summary>
public sealed record SourceControlTeamSyncChange(string Path, string? ExpectedBlobSha, byte[]? Content);

/// <summary>Expected repository head and a non-empty set of `.squad/` changes.</summary>
public sealed record SourceControlTeamSyncCommitRequest(
    string Branch,
    string ExpectedBaseSha,
    string Message,
    ImmutableArray<SourceControlTeamSyncChange> Changes)
{
    public override string ToString() => nameof(SourceControlTeamSyncCommitRequest) + " [REDACTED]";
}

/// <summary>Commit created on the configured repository branch.</summary>
public sealed record SourceControlTeamSyncCommit(string CommitSha, string Branch, Uri HtmlUrl);

/// <summary>Authenticated request to read the configured repository at a ref.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectTeamSyncReadRequest(string SelectionCode, string? Reference)
{
    public override string ToString() => nameof(ProjectTeamSyncReadRequest) + " [REDACTED]";
}

/// <summary>Authenticated request to apply previewed `.squad/` changes to the configured repository.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectTeamSyncCommitRequest(
    string SelectionCode,
    string ExpectedBaseSha,
    string Message,
    ImmutableArray<SourceControlTeamSyncChange> Changes)
{
    public override string ToString() => nameof(ProjectTeamSyncCommitRequest) + " [REDACTED]";
}

/// <summary>Explicit repository-backed team-sync operations, separate from run-bound provider operations.</summary>
public interface ISourceControlTeamSyncAdapter
{
    Task<SourceControlTeamSyncSnapshot> ReadTeamSyncTreeAsync(
        SourceControlRepositoryIdentity repository,
        string reference,
        SecretCredential credential,
        CancellationToken cancellationToken);

    Task<SourceControlTeamSyncCommit> CommitTeamSyncChangesAsync(
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        SourceControlTeamSyncCommitRequest request,
        CancellationToken cancellationToken);
}
