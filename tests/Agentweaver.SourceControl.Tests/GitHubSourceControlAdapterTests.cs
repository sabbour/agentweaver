using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed class GitHubSourceControlAdapterTests
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task NegotiationUsesBrokerCredentialAndPinsLiveRepositoryIdentity()
    {
        var credential = NewCredential();
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/repos/octo/widget", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("transient-token", request.Headers.Authorization.Parameter);
            Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            return Task.FromResult(JsonResponse(RepositoryJson));
        });
        var resolver = CreateResolver();
        var candidate = ResolveCandidate(resolver);
        var adapter = CreateAdapter(handler);

        var negotiated = await adapter.NegotiateRepositoryAsync(
            candidate,
            new SourceControlRepositoryIdentity("octo", "widget"),
            credential,
            CancellationToken.None);

        Assert.Equal(123, negotiated.ProviderRepositoryId);
        Assert.Equal("123", negotiated.Resource.Resource.ResourceId);
        Assert.Equal(DateTimeOffset.Parse("2020-01-01T00:00:00Z").UtcDateTime.Ticks,
            negotiated.Resource.Resource.Generation);
        Assert.Contains(SourceControlCapabilities.Merge, negotiated.Resource.Capabilities);
        Assert.Contains(SourceControlCapabilities.RepositoryCheckout, negotiated.Resource.Capabilities);
        Assert.DoesNotContain("transient-token", credential.ToString());
    }

    [Fact]
    public async Task CreateIssueUsesPinnedRepositoryAndReturnsStructuredIssue()
    {
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/repos/octo/widget/issues", request.RequestUri!.AbsolutePath);
            Assert.Equal("transient-token", request.Headers.Authorization!.Parameter);
            var body = await ReadBodyAsync(request);
            Assert.Contains("\"title\":\"Bug\"", body, StringComparison.Ordinal);
            Assert.Contains("\"body\":\"Details\"", body, StringComparison.Ordinal);
            return JsonResponse(
                "{\"number\":17,\"title\":\"Bug\",\"html_url\":\"https://github.com/octo/widget/issues/17\"," +
                "\"state\":\"open\"}");
        });
        var adapter = CreateAdapter(handler);
        var context = CreateContext("run-1", SourceControlCapabilities.IssueWrite);

        var issue = await adapter.CreateIssueAsync(
            context,
            new SourceControlIssueRequest("Bug", "Details"),
            CancellationToken.None);

        Assert.Equal(17, issue.Number);
        Assert.Equal("Bug", issue.Title);
        Assert.Equal(new Uri("https://github.com/octo/widget/issues/17"), issue.HtmlUrl);
        Assert.Equal("open", issue.State);
    }

    [Fact]
    public async Task ReadReviewsReturnsStructuredStatesForPinnedPullRequest()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/repos/octo/widget/pulls/7/reviews", request.RequestUri!.AbsolutePath);
            Assert.Equal("100", request.RequestUri.Query.TrimStart('?').Split('=').Last());
            Assert.Equal("transient-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(JsonResponse(
                "[{\"id\":11,\"user\":{\"login\":\"reviewer\"},\"state\":\"APPROVED\"," +
                "\"submitted_at\":\"2026-10-07T12:00:00Z\"}," +
                "{\"id\":12,\"user\":{\"login\":\"second\"},\"state\":\"CHANGES_REQUESTED\"," +
                "\"submitted_at\":null}]"));
        });
        var adapter = CreateAdapter(handler);
        var context = CreateContext("run-1", SourceControlCapabilities.ReviewRead);

        var reviews = await adapter.ReadReviewsAsync(context, 7, CancellationToken.None);

        Assert.Equal(2, reviews.Length);
        Assert.Equal("reviewer", reviews[0].Reviewer);
        Assert.Equal(SourceControlReviewState.Approved, reviews[0].State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T12:00:00Z"), reviews[0].SubmittedAt);
        Assert.Equal("second", reviews[1].Reviewer);
        Assert.Equal(SourceControlReviewState.ChangesRequested, reviews[1].State);
        Assert.Null(reviews[1].SubmittedAt);
    }

    [Fact]
    public async Task CreatePullRequestReusesOnlyExact422Match()
    {
        var calls = 0;
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("transient-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(++calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    { Content = new StringContent("{\"message\":\"Validation failed\"}") },
                2 => JsonResponse("[" + PullRequestJson(7, HeadSha, BaseSha) + "]"),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });
        var adapter = CreateAdapter(handler);
        var context = CreateContext(
            "run-1",
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.PullRequestWrite);
        var request = new SourceControlPullRequestRequest(
            "Feature",
            "Body",
            "feature",
            HeadSha,
            "main",
            BaseSha,
            Draft: false);

        var result = await adapter.CreateOrReusePullRequestAsync(
            context, request, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(SourceControlPullRequestDisposition.Reused, result.Disposition);
        Assert.Equal(7, result.Number);
        Assert.Equal(HeadSha, result.HeadSha);
        Assert.Equal(BaseSha, result.BaseSha);
    }

    [Fact]
    public async Task CreatePullRequestRejectsExistingMismatchedHead()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(++calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity),
                2 => JsonResponse("[" + PullRequestJson(7, "cccccccccccccccccccccccccccccccccccccccc", BaseSha) + "]"),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            }));
        var adapter = CreateAdapter(handler);
        var context = CreateContext("run-1", SourceControlCapabilities.PullRequestWrite);
        var request = new SourceControlPullRequestRequest(
            "Feature", null, "feature", HeadSha, "main", BaseSha, Draft: false);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => adapter.CreateOrReusePullRequestAsync(context, request, CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.PullRequestMismatch, exception.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MergeUsesExpectedHeadAndDoesNotPerformAnUnadmittedPreflight()
    {
        var calls = 0;
        var handler = new StubHandler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/repos/octo/widget/pulls/7/merge", request.RequestUri!.AbsolutePath);
            return ReadBodyAsync(request).ContinueWith(
                body =>
                {
                    Assert.Contains($"\"sha\":\"{HeadSha}\"", body.Result, StringComparison.Ordinal);
                    Assert.Contains("\"merge_method\":\"rebase\"", body.Result, StringComparison.Ordinal);
                    return JsonResponse(
                        "{\"sha\":\"dddddddddddddddddddddddddddddddddddddddd\",\"merged\":true," +
                        "\"message\":\"Pull Request successfully merged\"}");
                },
                TaskScheduler.Default);
        });
        var adapter = CreateAdapter(handler);
        var context = CreateContext("run-1", SourceControlCapabilities.Merge);

        var result = await adapter.MergePullRequestAsync(
            context,
            new SourceControlMergeRequest(7, HeadSha, BaseSha, SourceControlMergeMethod.Rebase),
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", result.MergeSha);
        Assert.Equal(SourceControlMergeMethod.Rebase, result.Method);
    }

    [Fact]
    public async Task MergeReadinessUsesExactHeadRequiredContextAndRequiredApp()
    {
        var calls = new List<string>();
        var handler = ReadinessHandler(
            calls,
            RequiredStatusChecksJson("ci/build", "17"),
            CheckRunsJson(HeadSha, CheckRunJson("ci/build", HeadSha, "completed", "success", 17)),
            CommitStatusesJson(HeadSha));
        var adapter = CreateAdapter(handler);

        var result = await adapter.ReadMergeReadinessAsync(
            CreateContext("run-1", SourceControlCapabilities.Merge),
            ReadinessRequest(),
            CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(4, calls.Count);
        Assert.Equal(
            [
                "/repos/octo/widget/pulls/7",
                "/repos/octo/widget/rules/branches/main",
                "/repos/octo/widget/commits/" + HeadSha + "/check-runs",
                "/repos/octo/widget/commits/" + HeadSha + "/status"
            ],
            calls);
        Assert.Equal(SourceControlCheckState.Success, Assert.Single(result.RequiredChecks).State);
        Assert.Equal(17, result.RequiredChecks[0].AppId);
        Assert.Equal(HeadSha, result.RequiredChecks[0].CommitSha);
        Assert.Equal(SourceControlCheckEvidenceSource.CheckRun, result.RequiredChecks[0].Source);
    }

    [Theory]
    [InlineData("completed", "failure", "17", HeadSha, true, SourceControlCheckState.Failure)]
    [InlineData("in_progress", null, "17", HeadSha, true, SourceControlCheckState.Pending)]
    [InlineData("completed", "neutral", "17", HeadSha, true, SourceControlCheckState.Neutral)]
    [InlineData("completed", "future_conclusion", "17", HeadSha, true, SourceControlCheckState.Unknown)]
    [InlineData("completed", "success", "18", HeadSha, true, SourceControlCheckState.Unknown)]
    [InlineData("completed", "success", "17", BaseSha, true, SourceControlCheckState.Unknown)]
    [InlineData("completed", "success", "17", HeadSha, false, SourceControlCheckState.Unknown)]
    public async Task MergeReadinessDoesNotTreatNonExactOrNonSuccessEvidenceAsReady(
        string status,
        string? conclusion,
        string appId,
        string checkHeadSha,
        bool includeCheckRun,
        SourceControlCheckState expectedState)
    {
        var checkRuns = includeCheckRun
            ? CheckRunsJson(
                HeadSha,
                CheckRunJson("ci/build", checkHeadSha, status, conclusion, long.Parse(appId)))
            : CheckRunsJson(HeadSha);
        var handler = ReadinessHandler(
            [],
            RequiredStatusChecksJson("ci/build", "17"),
            checkRuns,
            CommitStatusesJson(HeadSha));
        var adapter = CreateAdapter(handler);

        var result = await adapter.ReadMergeReadinessAsync(
            CreateContext("run-1", SourceControlCapabilities.Merge),
            ReadinessRequest(),
            CancellationToken.None);

        var evidence = Assert.Single(result.RequiredChecks);
        Assert.Equal(expectedState, evidence.State);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task MergeReadinessAcceptsExactSuccessfulCommitStatusWhenNoAppIsRequired()
    {
        var handler = ReadinessHandler(
            [],
            RequiredStatusChecksJson("legacy-ci", "null"),
            CheckRunsJson(HeadSha),
            CommitStatusesJson(HeadSha, ("legacy-ci", "success")));
        var adapter = CreateAdapter(handler);

        var result = await adapter.ReadMergeReadinessAsync(
            CreateContext("run-1", SourceControlCapabilities.Merge),
            ReadinessRequest(),
            CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(SourceControlCheckEvidenceSource.CommitStatus, Assert.Single(result.RequiredChecks).Source);
    }

    [Fact]
    public async Task MergeReadinessRejectsChangedPullRequestBase()
    {
        var handler = ReadinessHandler(
            [],
            RequiredStatusChecksJson("ci/build", "17"),
            CheckRunsJson(HeadSha, CheckRunJson("ci/build", HeadSha, "completed", "success", 17)),
            CommitStatusesJson(HeadSha),
            PullRequestJson(7, HeadSha, "cccccccccccccccccccccccccccccccccccccccc"));
        var adapter = CreateAdapter(handler);

        var result = await adapter.ReadMergeReadinessAsync(
            CreateContext("run-1", SourceControlCapabilities.Merge),
            ReadinessRequest(),
            CancellationToken.None);

        Assert.False(result.IsReady);
    }

    [Theory]
    [InlineData("[{\"type\":\"merge_queue\"}]")]
    [InlineData("[{\"type\":\"required_workflows\"}]")]
    [InlineData("[{\"type\":\"future_required_rule\"}]")]
    public async Task MergeReadinessFailsClosedForUnsupportedOrUnknownRules(string rules)
    {
        var calls = new List<string>();
        var handler = ReadinessHandler(
            calls,
            rules,
            CheckRunsJson(HeadSha),
            CommitStatusesJson(HeadSha));
        var adapter = CreateAdapter(handler);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => adapter.ReadMergeReadinessAsync(
                CreateContext("run-1", SourceControlCapabilities.Merge),
                ReadinessRequest(),
                CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.CapabilityUnavailable, exception.Code);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task MergeReadinessFailsClosedWhenEffectiveRulesAreUnavailable()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? JsonResponse(PullRequestJson(7, HeadSha, BaseSha))
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var adapter = CreateAdapter(handler);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => adapter.ReadMergeReadinessAsync(
                CreateContext("run-1", SourceControlCapabilities.Merge),
                ReadinessRequest(),
                CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.CapabilityUnavailable, exception.Code);
    }

    [Fact]
    public async Task MergeTimeoutIsReportedAsUncertainOutcome()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("network lost"));
        var adapter = CreateAdapter(handler);
        var context = CreateContext("run-1", SourceControlCapabilities.Merge);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => adapter.MergePullRequestAsync(
                context,
                new SourceControlMergeRequest(7, HeadSha, BaseSha, SourceControlMergeMethod.Rebase),
                CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.RemoteOutcomeUncertain, exception.Code);
        Assert.DoesNotContain("transient-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WebhookVerifierSignsExactRawBytesAndInvalidatesItsCredential()
    {
        var rawBody = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        var key = Encoding.UTF8.GetBytes("webhook-secret");
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(key, rawBody)).ToLowerInvariant();
        var credential = NewCredential("webhook-secret");

        Assert.True(GitHubWebhookSignatureVerifier.VerifyRawBody(rawBody, signature, credential));
        Assert.Throws<InvalidOperationException>(() => credential.GetValue());

        var invalid = NewCredential("webhook-secret");
        var tampered = Encoding.UTF8.GetBytes("{\"action\":\"closed\"}");
        Assert.False(GitHubWebhookSignatureVerifier.VerifyRawBody(tampered, signature, invalid));
    }

    [Fact]
    public void WebhookParserRejectsRepositoryIdOrNameMismatch()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"repository\":{\"id\":123,\"full_name\":\"octo/widget\"}," +
            "\"action\":\"synchronize\",\"pull_request\":{\"number\":7," +
            "\"head\":{\"sha\":\"" + HeadSha + "\"},\"base\":{\"sha\":\"" + BaseSha + "\"}}}");

        var parsed = GitHubWebhookSignatureVerifier.ParseVerifiedPayload(
            payload, Guid.NewGuid().ToString(), "pull_request",
            new SourceControlRepositoryIdentity("octo", "widget"), 123);
        Assert.Equal(7, parsed.PullRequestNumber);
        Assert.Equal(HeadSha, parsed.HeadSha);

        var exception = Assert.Throws<SourceControlOperationException>(() =>
            GitHubWebhookSignatureVerifier.ParseVerifiedPayload(
                payload, Guid.NewGuid().ToString(), "pull_request",
                new SourceControlRepositoryIdentity("octo", "other"), 123));
        Assert.Equal(SourceControlFailureCode.InvalidBinding, exception.Code);
    }

    private static string RepositoryJson =>
        "{\"id\":123,\"full_name\":\"octo/widget\",\"default_branch\":\"main\"," +
        "\"private\":false,\"created_at\":\"2020-01-01T00:00:00Z\"," +
        "\"permissions\":{\"pull\":true,\"push\":true}}";

    private static string PullRequestJson(long number, string headSha, string baseSha) =>
        "{\"number\":" + number +
        ",\"html_url\":\"https://github.com/octo/widget/pull/" + number +
        "\",\"state\":\"open\",\"merged\":false," +
        "\"head\":{\"ref\":\"feature\",\"sha\":\"" + headSha +
        "\",\"repo\":{\"full_name\":\"octo/widget\"}}," +
        "\"base\":{\"ref\":\"main\",\"sha\":\"" + baseSha +
        "\",\"repo\":{\"full_name\":\"octo/widget\"}}}";

    private static SourceControlMergeReadinessRequest ReadinessRequest() =>
        new(7, "feature", HeadSha, "main", BaseSha);

    private static string RequiredStatusChecksJson(string context, string appId) =>
        "[{\"type\":\"required_status_checks\",\"parameters\":{\"required_status_checks\":[" +
        "{\"context\":\"" + context + "\",\"integration_id\":" + appId + "}]}}]";

    private static string CheckRunsJson(string sha, params string[] checkRuns) =>
        "{\"sha\":\"" + sha + "\",\"total_count\":" + checkRuns.Length +
        ",\"check_runs\":[" + string.Join(",", checkRuns) + "]}";

    private static string CheckRunJson(
        string name,
        string headSha,
        string status,
        string? conclusion,
        long appId)
    {
        var conclusionProperty = conclusion is null ? string.Empty : ",\"conclusion\":\"" + conclusion + "\"";
        return "{\"name\":\"" + name + "\",\"head_sha\":\"" + headSha +
               "\",\"status\":\"" + status + "\"" + conclusionProperty +
               ",\"app\":{\"id\":" + appId + "}}";
    }

    private static string CommitStatusesJson(
        string sha,
        params (string Context, string State)[] statuses) =>
        "{\"sha\":\"" + sha + "\",\"state\":\"success\",\"total_count\":" + statuses.Length +
        ",\"statuses\":[" + string.Join(",", statuses.Select(status =>
            "{\"context\":\"" + status.Context + "\",\"state\":\"" + status.State + "\"}")) + "]}";

    private static StubHandler ReadinessHandler(
        List<string> calls,
        string rules,
        string checkRuns,
        string statuses,
        string? pullRequest = null) =>
        new((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            return Task.FromResult(path switch
            {
                "/repos/octo/widget/pulls/7" =>
                    JsonResponse(pullRequest ?? PullRequestJson(7, HeadSha, BaseSha)),
                "/repos/octo/widget/rules/branches/main" => JsonResponse(rules),
                var value when value.EndsWith("/check-runs", StringComparison.Ordinal) =>
                    JsonResponse(checkRuns),
                var value when value.EndsWith("/status", StringComparison.Ordinal) =>
                    JsonResponse(statuses),
                _ => throw new InvalidOperationException("Unexpected GitHub request.")
            });
        });

    private static GitHubSourceControlAdapter CreateAdapter(StubHandler handler) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        });

    private static ProviderResolver CreateResolver()
    {
        var descriptor = GitHubSourceControlAdapter.CreateDescriptor();
        var registration = new ProviderRegistration(
            descriptor, Enabled: true, OptionsRevision: "source-control-v1",
            OptionsSchemaVersion: GitHubSourceControlAdapter.CurrentOptionsSchemaVersion);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)],
            []).Value!;
        return new ProviderResolver(catalog);
    }

    private static ProviderCandidate ResolveCandidate(ProviderResolver resolver) =>
        resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            ProjectOverrideId: null,
            RequiredAdapterVersion: new Version(1, 0, 0),
            RequiredOptionsSchemaVersion: GitHubSourceControlAdapter.CurrentOptionsSchemaVersion,
            RequiredCapabilities: ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)))
        .Value!.Candidate!;

    private static SourceControlOperationContext CreateContext(string runId, params string[] capabilities)
    {
        var resolver = CreateResolver();
        var candidate = ResolveCandidate(resolver);
        var negotiated = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl, SourceControlProviderIds.GitHub, "123", 637134336000000000);
        var binding = resolver.Pin(
            runId,
            candidate,
            resource.ResourceId,
            new ResourceNegotiation(resource, negotiated)).Value!;
        return new SourceControlOperationContext(
            binding,
            new SourceControlRepositoryIdentity("octo", "widget"),
            NewCredential());
    }

    private static SecretCredential NewCredential(string value = "transient-token") =>
        new(value, DateTimeOffset.UtcNow.AddHours(1));

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static async Task<string> ReadBodyAsync(HttpRequestMessage request) =>
        await request.Content!.ReadAsStringAsync();

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}
