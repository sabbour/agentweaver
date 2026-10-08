using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public static class SourceControlSecretPurposes
{
    public const string Api = "source-control.api";
    public const string Checkout = "source-control.checkout";
    public const string Webhook = "source-control.webhook";
}

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

public static class SourceControlProviderIds
{
    public const string GitHub = "github";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceControlRepositoryIdentity
{
    public SourceControlRepositoryIdentity(string owner, string name)
    {
        Owner = ValidateSegment(owner, nameof(owner), allowDots: false);
        Name = ValidateSegment(name, nameof(name), allowDots: true);
    }

    public string Owner { get; }
    public string Name { get; }
    [JsonIgnore]
    public string FullName => Owner + "/" + Name;

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceControlProjectSettings
{
    [JsonConstructor]
    public SourceControlProjectSettings(
        SourceControlRepositoryIdentity repository,
        SecretRef apiSecretReference,
        SecretRef? checkoutSecretReference = null,
        SecretRef? webhookSecretReference = null)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ApiSecretReference = apiSecretReference ??
            throw new ArgumentNullException(nameof(apiSecretReference));
        CheckoutSecretReference = checkoutSecretReference;
        WebhookSecretReference = webhookSecretReference;
    }

    public SourceControlRepositoryIdentity Repository { get; }
    public SecretRef ApiSecretReference { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? CheckoutSecretReference { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? WebhookSecretReference { get; }
}

public sealed record SourceControlRepositoryNegotiation(
    SourceControlRepositoryIdentity Repository,
    ResourceNegotiation Resource,
    long ProviderRepositoryId,
    string DefaultBranch,
    bool IsPrivate);

public sealed record SourceControlOperationContext(
    PinnedProviderBinding Binding,
    SourceControlRepositoryIdentity Repository,
    SecretCredential Credential);

public sealed record SourceControlIssueRequest(string Title, string? Body);

public sealed record SourceControlIssue(
    long Number,
    string Title,
    Uri HtmlUrl,
    string State);

public sealed record SourceControlPullRequestRequest(
    string Title,
    string? Body,
    string HeadBranch,
    string ExpectedHeadSha,
    string BaseBranch,
    string ExpectedBaseSha,
    bool Draft);

public enum SourceControlPullRequestDisposition
{
    Created,
    Reused,
    Observed
}

public sealed record SourceControlPullRequest(
    long Number,
    Uri HtmlUrl,
    string State,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    bool Merged,
    SourceControlPullRequestDisposition Disposition);

public enum SourceControlReviewState
{
    Approved,
    ChangesRequested,
    Commented,
    Pending,
    Dismissed,
    Unknown
}

public sealed record SourceControlReview(
    long Id,
    string Reviewer,
    SourceControlReviewState State,
    DateTimeOffset? SubmittedAt);

public enum SourceControlMergeMethod
{
    Merge,
    Squash,
    Rebase
}

public sealed record SourceControlMergeRequest(
    long PullRequestNumber,
    string ExpectedHeadSha,
    string ExpectedBaseSha,
    SourceControlMergeMethod Method);

public sealed record SourceControlMergeReadinessRequest(
    long PullRequestNumber,
    string ExpectedHeadBranch,
    string ExpectedHeadSha,
    string ExpectedBaseBranch,
    string ExpectedBaseSha);

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

public enum SourceControlCheckEvidenceSource
{
    CheckRun,
    CommitStatus,
    Unknown
}

public sealed record SourceControlRequiredCheckEvidence(
    string Context,
    long? AppId,
    string? CommitSha,
    SourceControlCheckState State,
    SourceControlCheckEvidenceSource Source);

public sealed record SourceControlMergeReadiness(
    SourceControlMergeReadinessRequest Request,
    SourceControlPullRequest PullRequest,
    bool RequirementsKnown,
    ImmutableArray<SourceControlRequiredCheckEvidence> RequiredChecks)
{
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

public sealed record SourceControlMergeOutcome(
    string MergeSha,
    SourceControlMergeMethod Method);

public interface ISourceControlAdapter
{
    string ProviderId { get; }
    ImmutableHashSet<string> AdvertisedCapabilities { get; }

    Task<SourceControlRepositoryNegotiation> NegotiateRepositoryAsync(
        ProviderCandidate candidate,
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        CancellationToken cancellationToken);

    Task VerifyCurrentBindingAsync(
        SourceControlOperationContext context,
        string requiredCapability,
        CancellationToken cancellationToken);

    Task<SourceControlIssue> CreateIssueAsync(
        SourceControlOperationContext context,
        SourceControlIssueRequest request,
        CancellationToken cancellationToken);

    Task<SourceControlPullRequest> CreateOrReusePullRequestAsync(
        SourceControlOperationContext context,
        SourceControlPullRequestRequest request,
        CancellationToken cancellationToken);

    Task<SourceControlPullRequest> ReadPullRequestAsync(
        SourceControlOperationContext context,
        long pullRequestNumber,
        CancellationToken cancellationToken);

    Task<ImmutableArray<SourceControlReview>> ReadReviewsAsync(
        SourceControlOperationContext context,
        long pullRequestNumber,
        CancellationToken cancellationToken);

    Task<SourceControlMergeReadiness> ReadMergeReadinessAsync(
        SourceControlOperationContext context,
        SourceControlMergeReadinessRequest request,
        CancellationToken cancellationToken);

    Task<SourceControlMergeOutcome> MergePullRequestAsync(
        SourceControlOperationContext context,
        SourceControlMergeRequest request,
        CancellationToken cancellationToken);
}
