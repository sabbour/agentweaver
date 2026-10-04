using System.Diagnostics;
using System.Text.Json;
using Agentweaver.Api.Blueprints;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Tests.Blueprints;

public sealed class McpBlueprintToolsTests : IClassFixture<BlueprintsWebApplicationFactory>
{
    private readonly BlueprintsWebApplicationFactory _factory;

    public McpBlueprintToolsTests(BlueprintsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BlueprintGenerate_ReturnsDurableJob_ThenStatusAndResultToolsReadArtifact()
    {
        _factory.Generator.Response = """
            {
              "id": "mcp-blueprint",
              "name": "MCP Blueprint",
              "description": "Generated through MCP.",
              "roster": ["backend-engineer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;
        using (var setup = _factory.CreateAuthenticatedClient())
            await _factory.PrepareAiExecutionAsync(setup, "blueprint_generation");
        using var http = _factory.CreateClient();
        var tools = new BlueprintTools(new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", BlueprintsWebApplicationFactory.TestApiKey)));

        var acceptedJson = await tools.BlueprintGenerateAsync(
            "generate through MCP",
            idempotency_key: "mcp-blueprint-job",
            ct: CancellationToken.None);
        using var accepted = JsonDocument.Parse(acceptedJson);
        accepted.RootElement.GetProperty("status").GetString().Should().Be("queued");
        accepted.RootElement.GetProperty("provider_snapshot")
            .GetProperty("provider_kind").GetString().Should().NotBeNullOrWhiteSpace();
        accepted.RootElement.GetProperty("created_at").GetDateTimeOffset()
            .Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(1));
        accepted.RootElement.TryGetProperty("ai_execution_context", out _).Should().BeTrue();
        var jobId = accepted.RootElement.GetProperty("job_id").GetString()!;

        var worker = _factory.Services.GetServices<IHostedService>()
            .OfType<BlueprintGenerationJobWorker>()
            .Single();
        _ = await worker.RunOneAsync(CancellationToken.None);

        await WaitForCompletionAsync(tools, jobId);

        using var result = JsonDocument.Parse(
            await tools.BlueprintGenerationResultAsync(jobId, CancellationToken.None));
        result.RootElement.GetProperty("job_id").GetString().Should().Be(jobId);
        result.RootElement.GetProperty("artifact_id").GetString().Should().NotBeNullOrWhiteSpace();
        result.RootElement.GetProperty("logical_id").GetString().Should().Be("mcp-blueprint");
        result.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task BlueprintGenerate_HostedWorkerClaimDoesNotMakeManualIterationCompleteTarget()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Generator.Response = """
            {
              "id": "held-mcp-blueprint",
              "name": "Held MCP Blueprint",
              "description": "Generated after the held worker is released.",
              "roster": ["backend-engineer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;
        _factory.Generator.BeforeGenerateAsync = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var http = _factory.CreateClient();
        var tools = new BlueprintTools(new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", BlueprintsWebApplicationFactory.TestApiKey)));
        string jobId;
        try
        {
            using (var setup = _factory.CreateAuthenticatedClient())
                await _factory.PrepareAiExecutionAsync(setup, "blueprint_generation");
            using var accepted = JsonDocument.Parse(await tools.BlueprintGenerateAsync(
                "generate held blueprint through MCP",
                idempotency_key: $"held-mcp-blueprint-{Guid.NewGuid():N}",
                ct: CancellationToken.None));
            jobId = accepted.RootElement.GetProperty("job_id").GetString()!;

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var worker = _factory.Services.GetServices<IHostedService>()
                .OfType<BlueprintGenerationJobWorker>()
                .Single();
            (await worker.RunOneAsync(CancellationToken.None)).Should().BeFalse();
            using var status = JsonDocument.Parse(
                await tools.BlueprintGenerationStatusAsync(jobId, CancellationToken.None));
            status.RootElement.GetProperty("job_id").GetString().Should().Be(jobId);
            status.RootElement.GetProperty("status").GetString().Should().Be("running");
            var timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => WaitForCompletionAsync(tools, jobId, TimeSpan.FromMilliseconds(100)));
            timeout.Message.Should().Contain(jobId).And.Contain("running");
        }
        finally
        {
            release.TrySetResult();
            _factory.Generator.BeforeGenerateAsync = null;
        }

        await WaitForCompletionAsync(tools, jobId);
        using var result = JsonDocument.Parse(
            await tools.BlueprintGenerationResultAsync(jobId, CancellationToken.None));
        result.RootElement.GetProperty("job_id").GetString().Should().Be(jobId);
        result.RootElement.GetProperty("artifact_id").GetString().Should().NotBeNullOrWhiteSpace();
        result.RootElement.GetProperty("logical_id").GetString().Should().Be("held-mcp-blueprint");
        result.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task BlueprintGenerate_FailedJobReportsTargetAndFailureInsteadOfReadingResult()
    {
        _factory.Generator.ExceptionToThrow = new InvalidOperationException("test generator failure");
        try
        {
            using (var setup = _factory.CreateAuthenticatedClient())
                await _factory.PrepareAiExecutionAsync(setup, "blueprint_generation");
            using var http = _factory.CreateClient();
            var tools = new BlueprintTools(new AgentweaverApiClient(
                http,
                new McpConfig("http://localhost", BlueprintsWebApplicationFactory.TestApiKey)));
            using var accepted = JsonDocument.Parse(await tools.BlueprintGenerateAsync(
                "generate failing blueprint through MCP",
                idempotency_key: $"failed-mcp-blueprint-{Guid.NewGuid():N}",
                ct: CancellationToken.None));
            var jobId = accepted.RootElement.GetProperty("job_id").GetString()!;
            var worker = _factory.Services.GetServices<IHostedService>()
                .OfType<BlueprintGenerationJobWorker>()
                .Single();
            _ = await worker.RunOneAsync(CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitForCompletionAsync(tools, jobId));
            error.Message.Should().Contain(jobId).And.Contain("failed")
                .And.Contain("blueprint_generation_failed");
        }
        finally
        {
            _factory.Generator.ExceptionToThrow = null;
        }
    }

    private static async Task WaitForCompletionAsync(
        BlueprintTools tools, string jobId, TimeSpan? deadline = null)
    {
        var limit = deadline ?? TimeSpan.FromSeconds(20);
        var clock = Stopwatch.StartNew();
        var lastStatus = "not observed";
        var lastFailure = "null";
        while (clock.Elapsed < limit)
        {
            using var response = JsonDocument.Parse(
                await tools.BlueprintGenerationStatusAsync(jobId, CancellationToken.None));
            var job = response.RootElement;
            job.GetProperty("job_id").GetString().Should().Be(jobId);
            lastStatus = job.GetProperty("status").GetString()!;
            lastFailure = job.GetProperty("failure").GetRawText();
            if (lastStatus == "completed")
                return;
            if (lastStatus is not ("queued" or "running"))
                throw new InvalidOperationException(
                    $"Blueprint generation job {jobId} ended with status {lastStatus}; failure: {lastFailure}");
            await Task.Delay(25);
        }
        throw new TimeoutException(
            $"Blueprint generation job {jobId} did not complete within {limit}; last status: {lastStatus}; failure: {lastFailure}");
    }
}
