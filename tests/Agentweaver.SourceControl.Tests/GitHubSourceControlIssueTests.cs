using System.Net;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed partial class GitHubSourceControlAdapterTests
{
    [Fact]
    public async Task IssuePageIncludesClosedWorkButFiltersPullRequestsAndUsesBoundedRepositoryPath()
    {
        var adapter = CreateAdapter(new StubHandler((request, _) =>
        {
            Assert.Equal("/repos/octo/widget/issues?state=all&sort=created&direction=asc&per_page=2&page=3",
                request.RequestUri!.PathAndQuery);
            return Task.FromResult(JsonResponse("""
                [{"number":17,"title":"Closed","body":null,"state":"closed"},
                 {"number":18,"pull_request":{},"title":"PR","body":"Not work","state":"open"}]
                """));
        }));
        var page = await adapter.ListIssuesAsync(CreateContext("sync", SourceControlCapabilities.RepositoryRead),
            3, 2, CancellationToken.None);
        Assert.Equal(4, page.NextPage);
        Assert.Equal(new SourceControlIssueContent(17, "Closed", "", true), Assert.Single(page.Issues));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1000001, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task IssuePageRejectsOutOfBoundsBeforeExternalCalls(int page, int count)
    {
        var adapter = CreateAdapter(new StubHandler((_, _) => throw new InvalidOperationException()));
        var error = await Assert.ThrowsAsync<SourceControlOperationException>(() => adapter.ListIssuesAsync(
            CreateContext("sync", SourceControlCapabilities.RepositoryRead), page, count, CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.InvalidRequest, error.Code);
    }

    [Theory]
    [InlineData("open", false)]
    [InlineData("closed", true)]
    public async Task IssueReadAndUpdateConfirmTitleBodyAndLifecycle(string state, bool closed)
    {
        var calls = 0;
        var adapter = CreateAdapter(new StubHandler(async (request, _) =>
        {
            Assert.Equal("/repos/octo/widget/issues/17", request.RequestUri!.AbsolutePath);
            if (++calls == 2)
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(state, body.RootElement.GetProperty("state").GetString());
                Assert.Equal("Details", body.RootElement.GetProperty("body").GetString());
            }
            return JsonResponse($$"""{"number":17,"title":"Title","body":"Details","state":"{{state}}"}""");
        }));
        var context = CreateContext("sync", SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.IssueWrite);
        var issue = await adapter.ReadIssueAsync(context, 17, CancellationToken.None);
        Assert.Equal(new(17, "Title", "Details", closed), issue);
        Assert.Equal(issue, await adapter.UpdateIssueAsync(context, issue, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("""{"number":18,"title":"Title","body":"","state":"open"}""")]
    [InlineData("""{"number":17,"title":"Title","body":"","state":"unknown"}""")]
    [InlineData("""{"number":17,"title":"Title","body":"","state":"open","pull_request":{}}""")]
    [InlineData("""{"number":17,"title":"Title","body":4,"state":"open"}""")]
    public async Task IssueReadRejectsInvalidProviderContent(string json)
    {
        var adapter = CreateAdapter(new StubHandler((_, _) => Task.FromResult(JsonResponse(json))));
        await Assert.ThrowsAsync<SourceControlOperationException>(() => adapter.ReadIssueAsync(
            CreateContext("sync", SourceControlCapabilities.RepositoryRead), 17, CancellationToken.None));
    }

    [Fact]
    public async Task IssueUpdateTransportLossIsUncertainAndNeverRetriedByAdapter()
    {
        var calls = 0;
        var adapter = CreateAdapter(new StubHandler((_, _) =>
        {
            calls++;
            throw new HttpRequestException("test transport loss");
        }));
        var error = await Assert.ThrowsAsync<SourceControlOperationException>(() => adapter.UpdateIssueAsync(
            CreateContext("sync", SourceControlCapabilities.IssueWrite),
            new(17, "Title", "Details", true), CancellationToken.None));
        Assert.Equal(SourceControlFailureCode.RemoteOutcomeUncertain, error.Code);
        Assert.Equal(1, calls);
    }
}
