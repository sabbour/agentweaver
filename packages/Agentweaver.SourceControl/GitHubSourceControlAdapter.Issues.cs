using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed partial class GitHubSourceControlAdapter
{
    /// <summary>Lists a bounded stable-order page, including closed issues for recovery, but never PRs.</summary>
    public async Task<SourceControlIssuePage> ListIssuesAsync(
        SourceControlOperationContext context, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            throw new SourceControlOperationException(SourceControlFailureCode.InvalidRequest,
                "Issue page bounds are invalid.");
        ValidateContext(context, SourceControlCapabilities.RepositoryRead);
        using var response = await SendAsync(HttpMethod.Get,
            RepositoryUri(context.Repository, string.Create(CultureInfo.InvariantCulture,
                $"issues?state=all&sort=created&direction=asc&per_page={pageSize}&page={page}")),
            context.Credential, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub issue listing");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() > pageSize)
            throw new SourceControlOperationException(SourceControlFailureCode.InvalidResponse,
                "GitHub returned an invalid issue page.");
        var issues = document.RootElement.EnumerateArray()
            .Where(item => !item.TryGetProperty("pull_request", out _))
            .Select(ReadIssueContent).ToImmutableArray();
        return new(issues, document.RootElement.GetArrayLength() == pageSize ? checked(page + 1) : null);
    }

    /// <summary>Reads one mapped issue, rejecting a PR or a mismatched issue identity.</summary>
    public async Task<SourceControlIssueContent> ReadIssueAsync(
        SourceControlOperationContext context, long number, CancellationToken cancellationToken)
    {
        ValidateIssueNumber(number);
        ValidateContext(context, SourceControlCapabilities.RepositoryRead);
        using var response = await SendAsync(HttpMethod.Get,
            RepositoryUri(context.Repository, $"issues/{number.ToString(CultureInfo.InvariantCulture)}"),
            context.Credential, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub issue read");
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var issue = ReadIssueContent(document.RootElement);
        if (issue.Number != number)
            throw new SourceControlOperationException(SourceControlFailureCode.InvalidResponse,
                "GitHub returned another issue.");
        return issue;
    }

    /// <summary>Updates title, body and lifecycle together; uncertain writes are reconciled by reading next cycle.</summary>
    public async Task<SourceControlIssueContent> UpdateIssueAsync(
        SourceControlOperationContext context, SourceControlIssueContent issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ValidateIssueNumber(issue.Number);
        ValidateIssueRequest(new(issue.Title, issue.Body));
        ValidateContext(context, SourceControlCapabilities.IssueWrite);
        using var response = await SendAsync(HttpMethod.Patch,
            RepositoryUri(context.Repository, $"issues/{issue.Number.ToString(CultureInfo.InvariantCulture)}"),
            context.Credential,
            JsonContent.Create(new { title = issue.Title, body = issue.Body, state = issue.Closed ? "closed" : "open" }),
            cancellationToken, operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
        EnsureSuccess(response, "GitHub issue update", writeMayHaveChangedRemoteState: true);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var result = ReadIssueContent(document.RootElement);
        if (result != issue)
            throw new SourceControlOperationException(SourceControlFailureCode.RemoteOutcomeUncertain,
                "GitHub did not confirm the requested issue content.");
        return result;
    }

    private static void ValidateIssueNumber(long number)
    {
        if (number <= 0)
            throw new SourceControlOperationException(SourceControlFailureCode.InvalidRequest,
                "A positive issue number is required.");
    }

    private static SourceControlIssueContent ReadIssueContent(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("pull_request", out _) ||
            !item.TryGetProperty("body", out var body) || body.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new SourceControlOperationException(SourceControlFailureCode.InvalidResponse,
                "GitHub returned invalid issue content.");
        var state = ReadRequiredString(item, "state");
        if (state is not ("open" or "closed"))
            throw new SourceControlOperationException(SourceControlFailureCode.InvalidResponse,
                "GitHub returned an unsupported issue state.");
        var result = new SourceControlIssueContent(ReadPositiveInt64(item, "number"),
            ReadRequiredString(item, "title"), body.GetString() ?? string.Empty, state == "closed");
        ValidateIssueRequest(new(result.Title, result.Body));
        return result;
    }
}
