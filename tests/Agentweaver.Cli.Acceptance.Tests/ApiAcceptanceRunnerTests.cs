using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;
using Agentweaver.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AcceptanceTestHost = Agentweaver.Cli.Acceptance.TestHost.TestHostMarker;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class ApiAcceptanceRunnerTests
{
    [Theory]
    [InlineData(false, false, false, 0, "passed", "cleaned", null)]
    [InlineData(true, false, false, 1, "failed", "indeterminate", null)]
    [InlineData(false, true, false, 1, "failed", "indeterminate", null)]
    [InlineData(false, false, true, 1, "failed", "indeterminate", null)]
    [InlineData(false, false, false, 1, "failed", "cleaned", "edit-rejection")]
    [InlineData(false, false, false, 1, "failed", "indeterminate", "negative-revision")]
    [InlineData(false, false, false, 1, "failed", "indeterminate", "missing-revision")]
    [InlineData(false, false, false, 1, "failed", "indeterminate", "fractional-revision")]
    [InlineData(false, false, false, 1, "failed", "indeterminate", "string-revision")]
    [InlineData(false, false, false, 1, "failed", "indeterminate", "malformed-task")]
    [InlineData(false, false, false, 1, "failed", "indeterminate", "ambiguous-capture")]
    [InlineData(false, false, false, 1, "failed", "cleaned", "owner-gateway-problem")]
    [InlineData(false, false, false, 1, "failed", "cleaned", "owner-bare-403")]
    [InlineData(false, false, false, 1, "failed", "cleaned", "owner-graph-changed")]
    public async Task BoardDeleteExercisesTypedConflictPermissionDeletionAndCleanupOperations(
        bool failCleanup,
        bool ambiguousDelete,
        bool preserveCompactedTaskRevision,
        int expectedExitCode,
        string expectedFeatureStatus,
        string expectedCleanupState,
        string? boardScenario)
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();

            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup,
                ambiguousBoardDelete: ambiguousDelete, preserveCompactedTaskRevision: preserveCompactedTaskRevision,
                ambiguousBoardCapture: boardScenario == "ambiguous-capture",
                failFirstBoardTaskEdit: boardScenario == "edit-rejection",
                malformedBoardSnapshot: boardScenario is null or "edit-rejection"
                    or "owner-gateway-problem" or "owner-bare-403" or "owner-graph-changed" ? null : boardScenario,
                ownerDenialScenario: boardScenario is "owner-gateway-problem" or "owner-bare-403" or "owner-graph-changed"
                    ? boardScenario : null);
            handler.GatewayOrigin = new Uri("https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/");
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace, "images.json"), "{}");
            await File.WriteAllTextAsync(inputPath, CreateBoardManifest().ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath],
                runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();

            Assert.True(exitCode == expectedExitCode,
                $"Board execution failed: {result["failure"]?["message"]}; " +
                $"operations={string.Join(",", handler.Requests.Select(request => request.OperationId))}");
            Assert.Equal("board-acceptance-result", result["kind"]?.GetValue<string>());
            Assert.Equal(expectedFeatureStatus, result["featureStatus"]?.GetValue<string>());
            Assert.Equal("not-evaluated", result["fullP1Status"]?.GetValue<string>());
            Assert.Equal(expectedCleanupState, result["observations"]?["boardFixture"]?["cleanup"]?["state"]?.GetValue<string>());
            if (boardScenario == "edit-rejection")
            {
                Assert.Equal(2, handler.Requests.Count(request => request.OperationId == "deleteProjectBacklogTask"
                    && request.Body?["expectedTaskRevision"]?.GetValue<long>() == 0));
                Assert.Equal("verified", result["observations"]?["boardFixture"]?["cleanup"]?["taskDeletion"]?.GetValue<string>());
                Assert.Equal("archived", result["observations"]?["boardFixture"]?["cleanup"]?["projectState"]?.GetValue<string>());
                Assert.Contains(handler.Requests, request => request.OperationId == "revokeProjectRoleAssignment");
                return;
            }
            if (boardScenario == "ambiguous-capture")
            {
                JsonObject fixture = result["observations"]?["boardFixture"] as JsonObject ?? [];
                Assert.Equal(AcceptanceGatewayHandler.BoardProjectId, fixture["projectId"]?.GetValue<string>());
                Assert.Equal(1, fixture["projectAcknowledgedRevision"]?.GetValue<long>());
                Assert.Equal("22222222-2222-4222-8222-222222222222", fixture["ownerAssignmentId"]?.GetValue<string>());
                Assert.Equal(1, fixture["ownerAssignmentRevision"]?.GetValue<long>());
                JsonObject attempt = Assert.Single(fixture["taskCaptureAttempts"]!.AsArray().OfType<JsonObject>());
                string invocationId = fixture["invocationId"]!.GetValue<string>();
                Assert.Equal(ApiAcceptanceSupport.StableUuid(invocationId, "board-task-a"), attempt["taskId"]?.GetValue<string>());
                Assert.Equal("unknown", attempt["outcome"]?.GetValue<string>());
                Assert.Empty(fixture["ownedTaskIds"]!.AsArray());
                Assert.Equal(1, handler.Requests.Count(request => request.OperationId == "captureBacklogTask"));
                Assert.DoesNotContain(handler.Requests, request => request.OperationId is
                    "deleteProjectBacklogTask" or "updateProject" or "revokeProjectRoleAssignment");
                return;
            }
            if (boardScenario is "owner-gateway-problem" or "owner-bare-403" or "owner-graph-changed")
            {
                string failure = result["failure"]?["message"]?.GetValue<string>() ?? "";
                if (boardScenario == "owner-graph-changed")
                    Assert.Contains("A rejected backlog mutation changed the authoritative graph", failure);
                else
                    Assert.Contains("without backlog_project_permission_denied", failure);
                Assert.DoesNotContain(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                    step["id"]?.GetValue<string>() == "owner-denial" && step["status"]?.GetValue<string>() == "passed");
                Assert.DoesNotContain(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                    step["id"]?.GetValue<string>() is "delete-task-with-dependent-edge" or "delete-empty-board");
                Assert.Equal(1, handler.Requests.Count(request => request.OperationId == "deleteProjectBacklogTask"
                    && request.Headers["Authorization"].Contains("projects.orchestrator", StringComparison.Ordinal)));
                Assert.Equal("verified", result["observations"]?["boardFixture"]?["cleanup"]?["taskDeletion"]?.GetValue<string>());
                Assert.Equal("archived", result["observations"]?["boardFixture"]?["cleanup"]?["projectState"]?.GetValue<string>());
                return;
            }
            if (boardScenario is not null)
            {
                Assert.Contains(handler.Requests, request => request.OperationId == "captureBacklogTask");
                Assert.Contains("invalid task snapshot", result["failure"]?["message"]?.GetValue<string>());
                Assert.DoesNotContain(handler.Requests, request => request.OperationId is
                    "deleteProjectBacklogTask" or "updateProject" or "revokeProjectRoleAssignment");
                return;
            }
            if (!ambiguousDelete && !preserveCompactedTaskRevision)
                Assert.Equal(0, result["observations"]?["board"]?["finalTaskCount"]?.GetValue<int>());
            Assert.Contains(handler.Requests, request => request.OperationId == "deleteProjectBacklogTask"
                && request.Body?["expectedGraphRevision"]?.GetValue<long>() > 0
                && request.Body?["expectedTaskRevision"]?.GetValue<long>() >= 0);
            Assert.Contains(handler.Requests, request => request.OperationId == "deleteProjectBacklogTask"
                && request.Body?["expectedTaskRevision"]?.GetValue<long>() == 0);
            if (!ambiguousDelete && !preserveCompactedTaskRevision)
                Assert.Contains(handler.Requests, request => request.OperationId == "revokeProjectRoleAssignment");
            else
                Assert.Equal(5, handler.Requests.Count(request => request.OperationId == "deleteProjectBacklogTask"));
            if (preserveCompactedTaskRevision)
            {
                Assert.Equal(0, handler.LastCompactedTaskRevision);
                Assert.Equal(0, handler.LastCompactedTaskPosition);
                Assert.DoesNotContain(handler.Requests, request => request.OperationId == "revokeProjectRoleAssignment");
            }
            JsonObject ownerDenialStep = Assert.Single(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                step["id"]?.GetValue<string>() == "owner-denial");
            Assert.Equal("passed", ownerDenialStep["status"]?.GetValue<string>());
            Assert.Equal(403, ownerDenialStep["httpStatus"]?.GetValue<int>());
            Assert.Equal("backlog_project_permission_denied", ownerDenialStep["ownerIssueCode"]?.GetValue<string>());
            if (expectedExitCode == 0)
            {
                Assert.Contains(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                    step["id"]?.GetValue<string>() == "delete-task-with-dependent-edge" && step["status"]?.GetValue<string>() == "passed");
                Assert.Contains(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                    step["id"]?.GetValue<string>() == "delete-empty-board" && step["status"]?.GetValue<string>() == "passed");
                Assert.Equal("verified", result["observations"]?["boardFixture"]?["cleanup"]?["taskDeletion"]?.GetValue<string>());
            }
            Assert.Contains(handler.Requests, request => request.OperationId == "deleteProjectBacklogTask"
                && request.Headers["Authorization"].Contains("projects.orchestrator", StringComparison.Ordinal));
            Assert.Equal(AcceptanceGatewayHandler.BoardProjectId,
                result["observations"]?["boardFixture"]?["projectId"]?.GetValue<string>());
            JsonObject successfulFixture = result["observations"]?["boardFixture"] as JsonObject ?? [];
            Assert.Equal(1, successfulFixture["projectAcknowledgedRevision"]?.GetValue<long>());
            Assert.Equal("22222222-2222-4222-8222-222222222222",
                successfulFixture["ownerAssignmentId"]?.GetValue<string>());
            Assert.Equal(1, successfulFixture["ownerAssignmentRevision"]?.GetValue<long>());
            JsonArray attempts = successfulFixture["taskCaptureAttempts"]!.AsArray();
            string successfulInvocationId = successfulFixture["invocationId"]!.GetValue<string>();
            Assert.Collection(attempts,
                attempt =>
                {
                    Assert.Equal(ApiAcceptanceSupport.StableUuid(successfulInvocationId, "board-task-a"), attempt?["taskId"]?.GetValue<string>());
                    Assert.Equal("acknowledged", attempt?["outcome"]?.GetValue<string>());
                },
                attempt =>
                {
                    Assert.Equal(ApiAcceptanceSupport.StableUuid(successfulInvocationId, "board-task-b"), attempt?["taskId"]?.GetValue<string>());
                    Assert.Equal("acknowledged", attempt?["outcome"]?.GetValue<string>());
                });
            Assert.Equal(2, successfulFixture["ownedTaskIds"]!.AsArray().Count);
            if (expectedCleanupState == "cleaned")
                Assert.Equal(2, successfulFixture["projectRevision"]?.GetValue<long>());
            Assert.All(handler.Requests.Where(request => request.ProjectId.Length > 0),
                request => Assert.Equal(AcceptanceGatewayHandler.BoardProjectId, request.ProjectId));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("project-readback-unavailable")]
    [InlineData("assignment-readback-contradictory")]
    public async Task SessionHistoryMetricCleanupRemainsIndeterminateUntilBothFinalReadbacksPass(string scenario)
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();
            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(
                openApi, trace, failCleanup: false, metricScenario: scenario)
            {
                GatewayOrigin = new Uri(gatewayAddress),
            };
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath,
                CreateMetricManifest(workspace, handler.GatewayOrigin, omitSelectionContext: true).ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath], runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();
            JsonObject cleanup = result["observations"]?["fixtureSetup"]?["cleanup"] as JsonObject ?? [];
            RecordedRequest[] requests = handler.Requests.ToArray();

            Assert.Equal(1, exitCode);
            Assert.Equal("failed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal("indeterminate", cleanup["state"]?.GetValue<string>());
            Assert.DoesNotContain("cleaned", cleanup["state"]?.GetValue<string>());
            Assert.Contains(requests, request => request.OperationId == "cancelProjectRun");
            Assert.Contains(requests, request =>
                request.OperationId == "updateProject" && request.Body?["state"]?.GetValue<string>() == "archived");
            Assert.Contains(requests, request => request.OperationId == "revokeProjectRoleAssignment");
            Assert.Null(cleanup["projectReadback"]);
            Assert.Null(cleanup["assignmentsReadback"]);
            // Setup reads the creator Owner, so only post-archive requests prove cleanup readback behavior.
            RecordedRequest[] cleanupReadbacks = requests
                .SkipWhile(request => !(request.OperationId == "updateProject"
                    && request.Body?["state"]?.GetValue<string>() == "archived"))
                .ToArray();
            if (scenario == "project-readback-unavailable")
                Assert.DoesNotContain(cleanupReadbacks, request => request.OperationId == "listProjectRoleAssignments");
            else
                Assert.Contains(cleanupReadbacks, request => request.OperationId == "listProjectRoleAssignments");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task BoardCreatorOwnerReadbackFailureAvoidsUnsafeCleanup()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();

            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup: false, failBoardOwnerReadback: true)
            {
                GatewayOrigin = new Uri("https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/"),
            };
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace, "images.json"), "{}");
            await File.WriteAllTextAsync(inputPath, CreateBoardManifest().ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath],
                runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();

            Assert.Equal(1, exitCode);
            Assert.Equal("failed", result["featureStatus"]?.GetValue<string>());
            Assert.Equal("indeterminate", result["observations"]?["boardFixture"]?["cleanup"]?["state"]?.GetValue<string>());
            Assert.Equal(AcceptanceGatewayHandler.BoardProjectId,
                result["observations"]?["boardFixture"]?["projectId"]?.GetValue<string>());
            Assert.Contains(handler.Requests, request => request.OperationId == "listProjectRoleAssignments");
            Assert.DoesNotContain(handler.Requests, request => request.OperationId == "assignProjectRole");
            Assert.DoesNotContain(handler.Requests, request => request.OperationId == "updateProject");
            Assert.DoesNotContain(handler.Requests, request => request.OperationId == "revokeProjectRoleAssignment");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task BoardDeleteRejectsAnInterruptedReservationBeforeProviderOrAuthentication()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace, "images.json"), "{}");
            await File.WriteAllTextAsync(inputPath, CreateBoardManifest().ToJsonString());
            JsonObject reservation = ApiAcceptanceSupport.SetupInvocation(
                Guid.NewGuid().ToString("D"), new string('a', 64), "board-setup-invocation");
            string originalEvidence = ApiAcceptanceSupport.EvidenceJson(reservation) + "\n";
            await File.WriteAllTextAsync(evidencePath, originalEvidence);

            var trace = new ConcurrentQueue<string>();
            using var http = new HttpClient(new NoRequestsHandler());
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var provider = new TestDeploymentEvidenceProvider();
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, provider, output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath],
                runtime);

            Assert.Equal(2, exitCode);
            Assert.Contains("reconcile", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalEvidence, await File.ReadAllTextAsync(evidencePath));
            Assert.Equal(0, provider.PrepareCalls);
            Assert.Empty(trace);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task BoardDeleteExclusivelyAdmitsOnlyOneConcurrentInvocationForAnEvidencePath()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();

            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup: false)
            {
                GatewayOrigin = new Uri("https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/"),
            };
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var provider = new BlockingDeploymentEvidenceProvider();
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, provider, output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace, "images.json"), "{}");
            await File.WriteAllTextAsync(inputPath, CreateBoardManifest().ToJsonString());

            Task<int> firstRun = CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath],
                runtime);
            await provider.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            string ownedReservation = await File.ReadAllTextAsync(evidencePath);
            try
            {
                int secondExitCode = await CliApplication.RunAsync(
                    ["test", "--input", inputPath, "--output", evidencePath],
                    runtime);

                Assert.Equal(2, secondExitCode);
                Assert.Equal(ownedReservation, await File.ReadAllTextAsync(evidencePath));
                Assert.Equal(1, provider.PrepareCalls);
                Assert.Empty(handler.Requests);
                Assert.Empty(trace);
            }
            finally
            {
                provider.Release();
            }

            Assert.Equal(0, await firstRun.WaitAsync(TimeSpan.FromSeconds(60)));
            string[] records = await File.ReadAllLinesAsync(evidencePath);
            JsonObject invocation = JsonNode.Parse(records[0])!.AsObject();
            JsonObject result = JsonNode.Parse(records[^1])!.AsObject();
            Assert.Equal("board-setup-invocation", invocation["kind"]?.GetValue<string>());
            Assert.Equal(invocation["invocationId"]?.GetValue<string>(),
                result["observations"]?["boardFixture"]?["invocationId"]?.GetValue<string>());
            Assert.Single(handler.Requests, request => request.OperationId == "createProject");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptanceInputRejectsLegacyImageReceiptsAndPublicationPaths()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            JsonObject manifest = CreateManifest(workspace, new Uri("http://127.0.0.1:49241/"));
            string inputPath = Path.Combine(workspace, "manifest.json");
            manifest["pins"]!["imageReceipt"] = new JsonObject();
            await File.WriteAllTextAsync(inputPath, manifest.ToJsonString());

            InvalidDataException receiptFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ApiAcceptanceSupport.ReadManifestAsync(inputPath, default));
            Assert.Contains("caller-supplied image receipts are not accepted", receiptFailure.Message, StringComparison.Ordinal);

            manifest["pins"]!.AsObject().Remove("imageReceipt");
            manifest["deploymentEvidence"]!["publicationPath"] = "publication.json";
            await File.WriteAllTextAsync(inputPath, manifest.ToJsonString());
            InvalidDataException pathFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ApiAcceptanceSupport.ReadManifestAsync(inputPath, default));
            Assert.Contains("deployment evidence", pathFailure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void UnverifiedDeploymentEvidenceKeepsFullAcceptanceBlockedAfterJourneyPasses()
    {
        JsonObject source = new()
        {
            ["services"] = new JsonObject { ["status"] = "blocked" },
            ["agentHostImageMeasurement"] = new JsonObject { ["status"] = "blocked" },
        };

        JsonObject result = ApiAcceptanceSupport.Result(
            "passed", source, [], new JsonObject(), null, [], null);

        Assert.Equal("passed", result["surfaceStatus"]?.GetValue<string>());
        Assert.Equal("blocked", result["fullP1Status"]?.GetValue<string>());
        Assert.Equal(2, result["exitCode"]?.GetValue<int>());
        Assert.Equal("blocked", result["probes"]!.AsArray().OfType<JsonObject>()
            .Single(probe => probe["id"]?.GetValue<string>() == "service-deployment-source-binding")["status"]?.GetValue<string>());
        Assert.Equal("blocked", result["probes"]!.AsArray().OfType<JsonObject>()
            .Single(probe => probe["id"]?.GetValue<string>() == "agenthost-image-measurement")["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task DryRunValidatesRealPlanWithoutTransportAuthenticationOrEvidenceWrites()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var trace = new ConcurrentQueue<string>();
            using var http = new HttpClient(new NoRequestsHandler());
            var authentication = new TestAuthentication(trace);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(
                new GatewayClientFactory(http, authentication),
                authentication,
                new TestDeploymentEvidenceProvider(),
                output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: new GatewayClientFactory(http, authentication),
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath, CreateMetricManifest(
                workspace, new Uri("http://127.0.0.1:49241/"), omitSelectionContext: true).ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath, "--dry-run", "--interactive"],
                runtime);

            Assert.Equal(0, exitCode);
            Assert.Contains("Dry run only", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(trace);
            Assert.False(File.Exists(evidencePath));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("message", false, false)]
    [InlineData("message", true, false)]
    [InlineData("message", false, true)]
    [InlineData("journal", false, false)]
    [InlineData("gate-answer", false, false)]
    [InlineData("gate-approve", false, false)]
    [InlineData("gate-reject", false, false)]
    public async Task TestCommandRunsSelectedJourneyThroughGatewayTransportAndPersistsCleanupOutcome(
        string journey,
        bool interactive,
        bool failCleanup)
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();

            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup);
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            JsonObject manifest = CreateManifest(workspace, handler.GatewayOrigin, journey);
            if (journey == "journal")
                Assert.Empty(handler.JournalEvents);
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath, manifest.ToJsonString());

            string[] args = interactive
                ? ["test", "--input", inputPath, "--output", evidencePath, "--interactive"]
                : ["test", "--input", inputPath, "--output", evidencePath];
            int exitCode = await CliApplication.RunAsync(args, runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();

            Assert.Equal("p1-acceptance-result", result["kind"]?.GetValue<string>());
            Assert.True(exitCode == (failCleanup ? 1 : 2),
                $"Unexpected CLI result {exitCode}; surface={result["surfaceStatus"]}; full={result["fullP1Status"]}; " +
                $"recorded={result["exitCode"]}; failure={result["failure"]?["message"]}; " +
                $"persistenceError={output.ToString().Split('\n').FirstOrDefault(line => line.StartsWith("Could not persist", StringComparison.Ordinal))}; " +
                $"operations={string.Join(",", handler.Requests.Select(request => request.OperationId))}");
            Assert.Equal(exitCode, result["exitCode"]?.GetValue<int>());
            Assert.Equal(failCleanup ? "failed" : "passed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal(failCleanup ? "failed" : "blocked", result["fullP1Status"]?.GetValue<string>());
            Assert.Equal(failCleanup ? "indeterminate" : "cleaned",
                result["observations"]?["fixtureSetup"]?["cleanup"]?["state"]?.GetValue<string>());
            string journeyOperation = journey switch
            {
                "message" => "sendMessage",
                "journal" => "replayRunEvents",
                "gate-answer" => "answerGate",
                "gate-approve" => "approveGate",
                _ => "rejectGate",
            };
            Assert.Contains(handler.Requests, request => request.OperationId == journeyOperation);
            if (journey == "message")
            {
                Assert.Contains(handler.Requests, request =>
                    request.OperationId == "sendMessage" &&
                    request.Body?["payload"]?["text"]?.GetValue<string>() == "Controlled CLI message.");
                foreach (string secret in new[]
                {
                    "refresh-token-canary",
                    "descriptor-secret-canary",
                    "query-secret-canary",
                })
                {
                    Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, await File.ReadAllTextAsync(evidencePath), StringComparison.Ordinal);
                }
            }

            if (journey is "message" or "gate-answer" or "gate-approve" or "gate-reject")
            {
                string receiptOperation = journey switch
                {
                    "message" => "sendMessage",
                    "gate-answer" => "answerGate",
                    "gate-approve" => "approveGate",
                    _ => "rejectGate",
                };
                Assert.Contains(result["observations"]?["actionReceipts"]?.AsArray().OfType<JsonObject>() ?? [],
                    receipt => receipt["operationId"]?.GetValue<string>() == receiptOperation
                        && receipt["receipt"] is JsonObject
                        && receipt["httpStatus"]?.GetValue<int>() is >= 200 and < 300);
            }
            if (journey == "journal")
            {
                Assert.Equal(1, result["observations"]?["journal"]?["eventCount"]?.GetValue<int>());
                Assert.Equal(1, result["observations"]?["journal"]?["pageCount"]?.GetValue<int>());
                RecordedRequest[] requests = handler.Requests.ToArray();
                RecordedRequest seed = Assert.Single(requests, request =>
                    request.OperationId == "sendProjectRunMessage");
                RecordedRequest replay = Assert.Single(requests, request =>
                    request.OperationId == "replayRunEvents");
                JsonObject receipt = Assert.IsType<JsonObject>(handler.JournalMessageReceipt);
                JsonObject journalEvent = Assert.Single(handler.JournalEvents);
                JsonObject replayedEvent = Assert.IsType<JsonObject>(Assert.Single(handler.LastReplayedEvents!));
                JsonObject identity = Assert.IsType<JsonObject>(journalEvent["identity"]);
                Assert.Equal(receipt["messageId"]?.GetValue<string>(), journalEvent["eventId"]?.GetValue<string>());
                Assert.Equal(receipt["messageId"]?.GetValue<string>(), replayedEvent["eventId"]?.GetValue<string>());
                Assert.Equal(receipt["projectId"]?.GetValue<string>(), identity["projectId"]?.GetValue<string>());
                Assert.Equal(receipt["runId"]?.GetValue<string>(), identity["runId"]?.GetValue<string>());
                Assert.Equal(receipt["rootSessionId"]?.GetValue<string>(), identity["sessionId"]?.GetValue<string>());
                Assert.Equal(seed.ProjectId, replay.ProjectId);
                Assert.Equal(seed.RunId, replay.RunId);
                Assert.Equal(1, journalEvent["position"]?.GetValue<int>());
                Assert.Equal("Controlled P1 journal fixture event.", seed.Body?["text"]?.GetValue<string>());

                int rootAcceptedIndex = Array.FindIndex(requests, request => request.OperationId == "acceptRunRoot");
                int runReadback = Array.FindIndex(requests, request => request.OperationId == "readRunStatus");
                int sessionReadback = Array.FindIndex(requests, request => request.OperationId == "readSessionStatus");
                int seedIndex = Array.FindIndex(requests, request => request.OperationId == "sendProjectRunMessage");
                int replayIndex = Array.FindIndex(requests, request => request.OperationId == "replayRunEvents");
                Assert.True(rootAcceptedIndex < runReadback && rootAcceptedIndex < sessionReadback
                    && runReadback < seedIndex && sessionReadback < seedIndex && seedIndex < replayIndex,
                    $"The empty root must be read back before the owner message is accepted and replayed: " +
                    string.Join(", ", requests.Select(request => request.OperationId)));
            }
            Assert.Contains(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                step["id"]?.GetValue<string>() == journey &&
                step["status"]?.GetValue<string>() == "passed");

            string[] events = trace.ToArray();
            int selectionObserved = Array.FindIndex(events, item => item == "http:getRunSelection");
            int selectionAuthorization = Array.FindIndex(events, item =>
                item.StartsWith($"auth:owner:{(interactive ? InteractionPolicy.Allow : InteractionPolicy.Never)}:unbound:",
                    StringComparison.Ordinal)
                && item.EndsWith(",projects.orchestrator", StringComparison.Ordinal));
            int selectionAccepted = Array.FindIndex(events, item => item == "http:acceptRunSelection");
            int runAuthorization = Array.FindIndex(events, item =>
                item.StartsWith($"auth:owner:{(interactive ? InteractionPolicy.Allow : InteractionPolicy.Never)}:project-bound:",
                    StringComparison.Ordinal)
                && item.EndsWith(",projects.orchestrator", StringComparison.Ordinal));
            int rootAccepted = Array.FindIndex(events, item => item == "http:acceptRunRoot");
            Assert.True(selectionAuthorization >= 0 && selectionAccepted > selectionAuthorization
                && selectionObserved > selectionAccepted && runAuthorization > selectionObserved
                && rootAccepted > runAuthorization,
                $"Run-bound Broker authorization must follow selection readback and precede root acceptance: {string.Join(" | ", events)}");
            foreach (RecordedRequest selection in handler.Requests.Where(request =>
                request.OperationId is "acceptRunSelection" or "getRunSelection"))
            {
                string[] credential = selection.Headers["Authorization"]["Bearer ".Length..].Split('~');
                Assert.Equal("-", credential[2]);
                Assert.Equal("-", credential[3]);
                Assert.Contains("projects.orchestrator", credential[4].Split(',', StringSplitOptions.RemoveEmptyEntries));
                Assert.DoesNotContain("projects.admin", credential[4].Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
            Assert.StartsWith(
                $"auth:owner:{(interactive ? InteractionPolicy.Allow : InteractionPolicy.Never)}:unbound:",
                events[0],
                StringComparison.Ordinal);
            Assert.EndsWith(",projects.admin", events[0], StringComparison.Ordinal);
            Assert.True(Array.FindIndex(events, item => item == "http:createProject") > 0,
                "The unbound setup credential must be acquired before fixture creation.");
            foreach (RecordedRequest cleanup in handler.Requests.Where(request =>
                request.OperationId is "revokeProjectRoleAssignment" or "updateProject"))
            {
                string[] credential = cleanup.Headers["Authorization"]["Bearer ".Length..].Split('~');
                Assert.Equal("-", credential[2]);
                Assert.Equal("-", credential[3]);
                Assert.Contains("projects.admin", credential[4].Split(',', StringSplitOptions.RemoveEmptyEntries));
                Assert.DoesNotContain("projects.orchestrator", credential[4].Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
            Assert.Equal(!failCleanup, handler.Requests.Any(request => request.OperationId == "updateProject"));
            Assert.Equal(failCleanup ? 1 : 2,
                handler.Requests.Count(request => request.OperationId == "revokeProjectRoleAssignment"));
            if (failCleanup)
            {
                Assert.DoesNotContain("retry", result["failure"]?["message"]?.GetValue<string>() ?? "",
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("reconcile", result["failure"]?["message"]?.GetValue<string>() ?? "",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("empty-events")]
    [InlineData("non-object-event")]
    [InlineData("missing-has-more")]
    [InlineData("non-boolean-has-more")]
    [InlineData("null-terminal-cursor")]
    [InlineData("foreign-event")]
    public async Task JournalJourneyRejectsPagesOutsideTheLegacyReplayContract(string mutation)
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();
            var trace = new ConcurrentQueue<string>();
            using var http = new HttpClient(new AcceptanceGatewayHandler(
                openApi, trace, failCleanup: false, malformedJournalCase: mutation));
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath,
                CreateManifest(workspace, new Uri("http://127.0.0.1:49241/"), "journal").ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath], runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();

            Assert.Equal(1, exitCode);
            Assert.Equal("failed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal("cleaned", result["observations"]?["fixtureSetup"]?["cleanup"]?["state"]?.GetValue<string>());
            Assert.NotEmpty(result["failure"]?["message"]?.GetValue<string>() ?? "");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledJourneyStillCleansUpAndPersistsTerminalInvocation()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();
            var trace = new ConcurrentQueue<string>();
            using var cancellation = new CancellationTokenSource();
            using var handler = new AcceptanceGatewayHandler(
                openApi, trace, failCleanup: false, cancelOnMessage: cancellation);
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath,
                CreateManifest(workspace, handler.GatewayOrigin, "message").ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath], runtime, cancellation.Token);
            string[] lines = await File.ReadAllLinesAsync(evidencePath);
            JsonObject result = JsonNode.Parse(lines.Last())!.AsObject();

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, exitCode);
            Assert.Equal("failed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal("cleaned", result["observations"]?["fixtureSetup"]?["cleanup"]?["state"]?.GetValue<string>());
            Assert.Contains(handler.Requests, request => request.OperationId == "sendMessage");
            Assert.Contains(handler.Requests, request => request.OperationId == "revokeProjectRoleAssignment");
            Assert.Contains(handler.Requests, request => request.OperationId == "updateProject");
            Assert.Single(
                lines.Select(line => JsonNode.Parse(line)).OfType<JsonObject>(),
                record => record["kind"]?.GetValue<string>() == "p1-acceptance-result");

            int retryExitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath], runtime);
            Assert.NotEqual(0, retryExitCode);
            Assert.Equal(lines.Length, (await File.ReadAllLinesAsync(evidencePath)).Length);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SupplementalTestHostProcessRunsAcceptanceCommandThroughActualHttpTransport()
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();

            var trace = new ConcurrentQueue<string>();
            using var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup: false);
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            // Keep the CLI in a child process while routing its real HTTP client to the controlled Gateway fixture.
            await using WebApplication proxy = await StartAcceptanceGatewayProxyAsync(invoker);
            handler.GatewayOrigin = new Uri(proxy.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath, CreateManifest(workspace, handler.GatewayOrigin).ToJsonString());

            string hostAssembly = typeof(AcceptanceTestHost).Assembly.Location;
            string testAssembly = typeof(ApiAcceptanceRunnerTests).Assembly.Location;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    WorkingDirectory = workspace,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (string argument in new[]
            {
                "exec",
                "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
                hostAssembly,
                "test",
                "--input", inputPath,
                "--output", evidencePath,
            })
                process.StartInfo.ArgumentList.Add(argument);

            Assert.True(process.Start());
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The supplemental acceptance test host did not exit within 30 seconds.");
            }
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            Assert.Equal(2, process.ExitCode);
            Assert.Empty(stderr);
            Assert.Contains("sendMessage", handler.Requests.Select(request => request.OperationId));
            Assert.Contains("getAuthorizationContext", handler.Requests.Select(request => request.OperationId));
            Assert.All(handler.Requests, request =>
                Assert.StartsWith("Bearer ", request.Headers["Authorization"], StringComparison.Ordinal));

            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();
            Assert.Equal("p1-acceptance-result", result["kind"]?.GetValue<string>());
            Assert.Equal("passed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal("blocked", result["fullP1Status"]?.GetValue<string>());
            Assert.Equal(2, result["exitCode"]?.GetValue<int>());
            Assert.Contains(result["steps"]!.AsArray().OfType<JsonObject>(), step =>
                step["id"]?.GetValue<string>() == "message" &&
                step["status"]?.GetValue<string>() == "passed");
            Assert.Contains(result["observations"]!["actionReceipts"]!.AsArray().OfType<JsonObject>(), receipt =>
                receipt["operationId"]?.GetValue<string>() == "sendMessage"
                && receipt["receipt"]?["ownerMessageId"]?.GetValue<string>() == "message-owner-receipt");
            Assert.Contains("\"surfaceStatus\":\"passed\"", stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExactConfiguredApphostRunsWithoutDotnetOrNodeFallback()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("AGENTWEAVER_CLI_PATH");
        Assert.False(string.IsNullOrWhiteSpace(configuredPath),
            "Set AGENTWEAVER_CLI_PATH to the exact built agentweaver apphost for process integration tests.");
        string path = Path.GetFullPath(configuredPath!);
        Assert.True(File.Exists(path), $"AGENTWEAVER_CLI_PATH does not exist: {path}");
        Assert.EndsWith(
            OperatingSystem.IsWindows() ? ".exe" : "",
            path,
            StringComparison.OrdinalIgnoreCase);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--version");
        Assert.True(process.Start());
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.StartsWith("Agentweaver 1.x CLI 1.0.0", stdout.Trim(), StringComparison.Ordinal);
        Assert.Contains("(source ", stdout, StringComparison.Ordinal);
        Assert.Contains(", tree ", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task ExactConfiguredApphostRunsValidInteractiveDryRunWithoutAuthOrEvidenceWrites()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("AGENTWEAVER_CLI_PATH");
        Assert.False(string.IsNullOrWhiteSpace(configuredPath),
            "Set AGENTWEAVER_CLI_PATH to the exact built agentweaver apphost for process integration tests.");
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath,
                CreateManifest(workspace, new Uri("http://127.0.0.1:49241/")).ToJsonString());

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(Path.GetFullPath(configuredPath!))
                {
                    UseShellExecute = false,
                    WorkingDirectory = workspace,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (string argument in new[]
            {
                "test", "--dry-run", "--interactive", "--input", inputPath, "--output", evidencePath,
            })
                process.StartInfo.ArgumentList.Add(argument);

            Assert.True(process.Start());
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The configured CLI apphost dry-run did not exit within 30 seconds.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Dry run only", stdout, StringComparison.Ordinal);
            Assert.Empty(stderr);
            Assert.False(File.Exists(evidencePath));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SessionHistoryMetricUsesOwnedRunAndVerifiesTerminalCleanup(
        bool interactiveSelection,
        bool wrongMetricCount)
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();

            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup: false,
                metricScenario: "metric-success", metricCount: wrongMetricCount ? 3 : 2)
            {
                GatewayOrigin = new Uri(gatewayAddress),
            };
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            JsonObject manifest = CreateMetricManifest(
                workspace, handler.GatewayOrigin, omitSelectionContext: interactiveSelection);
            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath, manifest.ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath], runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();
            RecordedRequest[] requests = handler.Requests.ToArray();

            Assert.True(exitCode == (wrongMetricCount ? 1 : 2),
                $"Metric execution failed: {result["failure"]?["message"]}; " +
                $"cleanup={result["observations"]?["fixtureSetup"]?["cleanup"]}; " +
                $"operations={string.Join(",", requests.Select(request => request.OperationId))}");
            Assert.Equal(wrongMetricCount ? "failed" : "passed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal(wrongMetricCount ? "failed" : "blocked", result["fullP1Status"]?.GetValue<string>());
            Assert.Equal("cleaned", result["observations"]?["fixtureSetup"]?["cleanup"]?["state"]?.GetValue<string>());
            if (wrongMetricCount)
                Assert.Null(result["observations"]?["sessionHistoryMetric"]);
            else
                Assert.Equal(2, result["observations"]?["sessionHistoryMetric"]?["currentHistoryRecords"]?.GetValue<int>());

            RecordedRequest rootRequest = Assert.Single(requests, request => request.OperationId == "acceptRunRoot");
            JsonObject fixture = result["observations"]?["fixtureSetup"] as JsonObject ?? [];
            JsonObject selection = fixture["selection"] as JsonObject ?? [];
            string runId = ApiAcceptanceSupport.GetString(selection, "runId")!;
            string rootSessionId = rootRequest.Body?["sessionId"]?.GetValue<string>()!;
            Assert.Equal(runId, rootRequest.RunId);
            Assert.Equal(rootSessionId, fixture["rootSessionId"]?.GetValue<string>());
            Assert.Equal(AcceptanceGatewayHandler.MetricProjectId,
                fixture["setup"]?["projectId"]?.GetValue<string>());
            Assert.Equal(interactiveSelection ? "interactive-v1" : "selection-v1",
                ApiAcceptanceSupport.GetString(selection, "contextRevision"));

            if (interactiveSelection)
            {
                RecordedRequest create = Assert.Single(requests, request => request.OperationId == "createProjectRun");
                Assert.Equal("Acceptance fixture request; no work is dispatched.",
                    create.Body?["firstMessage"]?.GetValue<string>());
                Assert.Equal(2, create.Body?.Count);
                Assert.Equal(1, create.Body?["expectedProjectConfigRevision"]?.GetValue<long>());
                Assert.Equal(ApiAcceptanceSupport.StableUuid(
                    ApiAcceptanceSupport.GetString(fixture, "invocationId")!, "metric-create-run"),
                    create.Headers["Idempotency-Key"]);
                Assert.Equal(AcceptanceGatewayHandler.InteractiveRunId, runId);
                Assert.Equal(AcceptanceGatewayHandler.InteractiveRootSessionId, rootSessionId);
                Assert.DoesNotContain(requests, request => request.OperationId == "acceptRunSelection");
                Assert.Equal(1, requests.Count(request => request.OperationId == "getRunSelection"));
                Assert.Equal("effective", selection["snapshot"]?["selection"]?.GetValue<string>());
                Assert.NotEqual(ApiAcceptanceSupport.StableUuid(
                    ApiAcceptanceSupport.GetString(fixture, "invocationId")!, "run"), runId);
                Assert.NotEqual(ApiAcceptanceSupport.StableUuid(
                    ApiAcceptanceSupport.GetString(fixture, "invocationId")!, "root-session"), rootSessionId);
            }
            else
            {
                Assert.Single(requests, request => request.OperationId == "acceptRunSelection");
                Assert.DoesNotContain(requests, request => request.OperationId == "createProjectRun");
            }

            Assert.DoesNotContain(requests, request => request.OperationId is
                "startProjectRun" or "sendProjectRunMessage" or "sendMessage" or "selectWorkflow");
            RecordedRequest cancel = Assert.Single(requests, request => request.OperationId == "cancelProjectRun");
            Assert.Equal(runId, cancel.RunId);
            Assert.Equal(ApiAcceptanceSupport.StableUuid(
                ApiAcceptanceSupport.GetString(fixture, "invocationId")!, "metric-cancel-run"),
                cancel.Headers["Idempotency-Key"]);
            Assert.Equal("Acceptance fixture cleanup.", cancel.Body?["reason"]?.GetValue<string>());
            string[] cancellationToken = cancel.Headers["Authorization"]["Bearer ".Length..].Split('~');
            Assert.Equal("-", cancellationToken[2]);
            Assert.Equal("-", cancellationToken[3]);
            Assert.Contains("projects.orchestrator", cancellationToken[4].Split(',', StringSplitOptions.RemoveEmptyEntries));

            int cancelIndex = Array.FindIndex(requests, request => request.OperationId == "cancelProjectRun");
            int finalRunStatusIndex = Array.FindLastIndex(requests, request => request.OperationId == "readRunStatus");
            int finalTreeIndex = Array.FindLastIndex(requests, request => request.OperationId == "readSessionTree");
            int detachedStatusIndex = Array.FindLastIndex(requests, request =>
                request.OperationId == "readSessionStatus"
                && request.RunId == runId
                && request.Headers["Authorization"]["Bearer ".Length..].Split('~').ElementAtOrDefault(2) != "-");
            int archiveIndex = Array.FindIndex(requests, request =>
                request.OperationId == "updateProject" && request.Body?["state"]?.GetValue<string>() == "archived");
            Assert.True(cancelIndex >= 0 && cancelIndex < finalRunStatusIndex
                && finalRunStatusIndex < finalTreeIndex && finalTreeIndex < detachedStatusIndex
                && detachedStatusIndex < archiveIndex,
                $"Cancellation, terminal readbacks, and archive order were incorrect: {string.Join(", ", requests.Select(request => request.OperationId))}");
            RecordedRequest[] revokeRequests = requests.Where(request => request.OperationId == "revokeProjectRoleAssignment").ToArray();
            Assert.Equal(2, revokeRequests.Length);
            int lastRevokeIndex = Array.FindLastIndex(requests, request => request.OperationId == "revokeProjectRoleAssignment");
            int firstRevokeIndex = Array.FindIndex(requests, request => request.OperationId == "revokeProjectRoleAssignment");
            Assert.True(firstRevokeIndex < archiveIndex && archiveIndex < lastRevokeIndex,
                "The Orchestrator must be revoked before archival, and Owner must remain until after archival.");
            Assert.All(requests.Where(request =>
                (request.OperationId is "readRunStatus" or "readSessionTree" or "readSessionStatus")
                && request.RunId == runId && request.ProjectId.Length > 0),
                request =>
                {
                    string[] token = request.Headers["Authorization"]["Bearer ".Length..].Split('~');
                    Assert.Equal(fixture["setup"]?["projectId"]?.GetValue<string>(), token[2]);
                    Assert.Equal(runId, token[3]);
                    Assert.Contains("projects.orchestrator", token[4].Split(',', StringSplitOptions.RemoveEmptyEntries));
                });
            if (wrongMetricCount)
                Assert.Contains("archived-inclusive, detached-exclusive", result["failure"]?["message"]?.GetValue<string>());

            Assert.Equal("verified", fixture["cleanup"]?["runTerminalization"]?["acceptedRunReadback"]?.GetValue<string>());
            Assert.Equal("verified", fixture["cleanup"]?["runTerminalization"]?["attachedHistoryReadback"]?.GetValue<string>());
            Assert.Equal("verified", fixture["cleanup"]?["runTerminalization"]?["detachedSessionReadback"]?.GetValue<string>());
            Assert.Equal(2, fixture["cleanup"]?["runTerminalization"]?["executionFence"]?.GetValue<long>());
            Assert.Equal(1, fixture["cleanup"]?["runTerminalization"]?["stateVersion"]?.GetValue<long>());
            Assert.Equal(ApiAcceptanceSupport.StableUuid(runId, "metric-detached-child"),
                fixture["cleanup"]?["runTerminalization"]?["detachedSessionId"]?.GetValue<string>());
            Assert.Equal("archived", fixture["cleanup"]?["projectReadback"]?.GetValue<string>());
            Assert.Equal("empty", fixture["cleanup"]?["assignmentsReadback"]?.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("create-lost-response")]
    [InlineData("selection-revision")]
    [InlineData("selection-malformed-revision")]
    [InlineData("selection-lost-response")]
    [InlineData("run-auth-unavailable")]
    [InlineData("cancel-failure")]
    public async Task SessionHistoryMetricKeepsAmbiguousSetupOrCleanupIndeterminate(string scenario)
    {
        string workspace = Path.Combine(AppContext.BaseDirectory, "acceptance-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var gateway = await GatewayOpenApiParityTests.StartGatewayAsync();
            string gatewayAddress = gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var artifactClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage artifactResponse = await artifactClient.GetAsync(
                new Uri(new Uri(gatewayAddress), "/openapi/v1.json"));
            artifactResponse.EnsureSuccessStatusCode();
            JsonObject openApi = JsonNode.Parse(await artifactResponse.Content.ReadAsStringAsync())!.AsObject();
            var trace = new ConcurrentQueue<string>();
            var handler = new AcceptanceGatewayHandler(openApi, trace, failCleanup: false, metricScenario: scenario)
            {
                GatewayOrigin = new Uri(gatewayAddress),
            };
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication(trace,
                failRunBoundRunId: scenario == "run-auth-unavailable"
                    ? AcceptanceGatewayHandler.InteractiveRunId
                    : null);
            var factory = new GatewayClientFactory(http, authentication);
            var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(factory, authentication, new TestDeploymentEvidenceProvider(), output);
            var runtime = new CliRuntimeOptions(
                HttpClient: http,
                GatewayClientFactory: factory,
                Authentication: authentication,
                AcceptanceRunner: runner,
                Output: output,
                Error: output);

            string inputPath = Path.Combine(workspace, "manifest.json");
            string evidencePath = Path.Combine(workspace, "evidence.jsonl");
            await File.WriteAllTextAsync(inputPath,
                CreateMetricManifest(workspace, handler.GatewayOrigin, omitSelectionContext: true).ToJsonString());

            int exitCode = await CliApplication.RunAsync(
                ["test", "--input", inputPath, "--output", evidencePath], runtime);
            JsonObject result = JsonNode.Parse((await File.ReadAllLinesAsync(evidencePath)).Last())!.AsObject();
            JsonObject fixture = result["observations"]?["fixtureSetup"] as JsonObject ?? [];
            JsonObject cleanup = fixture["cleanup"] as JsonObject ?? [];
            RecordedRequest[] requests = handler.Requests.ToArray();

            Assert.Equal(1, exitCode);
            Assert.Equal("failed", result["surfaceStatus"]?.GetValue<string>());
            Assert.Equal("indeterminate", cleanup["state"]?.GetValue<string>());
            Assert.NotEqual("not-created", cleanup["state"]?.GetValue<string>());

            if (scenario == "create-lost-response")
            {
                Assert.Single(requests, request => request.OperationId == "createProjectRun");
                Assert.DoesNotContain(requests, request => request.OperationId == "cancelProjectRun");
            }
            else if (scenario is "selection-revision" or "selection-malformed-revision"
                or "selection-lost-response" or "run-auth-unavailable")
            {
                Assert.Contains(requests, request => request.OperationId == "cancelProjectRun");
                Assert.DoesNotContain(requests, request => request.OperationId is "acceptRunRoot" or "registerChild");
                if (scenario == "run-auth-unavailable")
                    Assert.DoesNotContain(requests, request => request.OperationId == "readRunStatus");
            }
            else
            {
                Assert.Single(requests, request => request.OperationId == "cancelProjectRun");
                Assert.Contains(requests, request => request.OperationId == "readProjectMetrics");
            }
            Assert.DoesNotContain(requests, request => request.OperationId is
                "revokeProjectRoleAssignment" or "updateProject");
            Assert.DoesNotContain(cleanup["steps"]?.AsArray().OfType<JsonObject>() ?? [],
                step => step["step"]?.GetValue<string>() is "archive-project" or "revoke-owner");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    private static JsonObject CreateManifest(string workspace, Uri gateway, string journey = "message")
    {
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sourceTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        File.WriteAllText(Path.Combine(workspace, "foundation.json"), "{}");
        File.WriteAllText(Path.Combine(workspace, "images.json"),
            $"{{\"images\":{{\"agentweaver-agenthost\":\"sha-{sourceSha}\"}}}}");
        return new JsonObject
        {
            ["executionIntent"] = "execute",
            ["journey"] = journey,
            ["environment"] = new JsonObject
            {
                ["baseUrl"] = gateway.GetLeftPart(UriPartial.Authority) + "/",
                ["issuer"] = "https://issuer.example/",
                ["audience"] = "agentweaver",
                ["actorId"] = "owner",
                ["tenantId"] = "tenant-owner",
            },
            ["pins"] = new JsonObject
            {
                ["context"] = "acceptance-test",
                ["namespace"] = "agentweaver",
                ["providerReceipt"] = new JsonObject
                {
                    ["configMapName"] = "provider-catalog",
                    ["sourceCommit"] = sourceSha,
                    ["uid"] = "provider-uid",
                    ["resourceVersion"] = "1",
                },
                ["runtimeReceipt"] = new JsonObject { ["revision"] = 1 },
                ["storageReceipt"] = new JsonObject { ["shareId"] = "storage-test" },
                ["sourceReceipt"] = new JsonObject { ["sourceSha"] = sourceSha, ["sourceTree"] = sourceTree },
            },
            ["deploymentEvidence"] = new JsonObject
            {
                ["manifestPath"] = "foundation.json",
                ["imageManifestPath"] = "images.json",
            },
            ["fixturePlan"] = new JsonObject
            {
                ["expectedPlatformRuntimeRevision"] = 1,
                ["selectionContext"] = new JsonObject
                {
                    ["revision"] = "selection-v1",
                    ["availableModelSelectionReferences"] = new JsonArray(),
                    ["providerRequirements"] = new JsonArray(),
                    ["requiredEgress"] = new JsonArray(),
                },
                ["gate"] = journey.StartsWith("gate-", StringComparison.Ordinal)
                    ? new JsonObject
                    {
                        ["prompt"] = "Controlled gate prompt.",
                        ["allowedChoices"] = new JsonArray("approve", "reject", "answer"),
                        ["allowsFreeform"] = false,
                        ["answer"] = "answer",
                    }
                    : null,
                ["message"] = journey == "message"
                    ? new JsonObject { ["text"] = "Controlled CLI message." }
                    : null,
                ["deniedActor"] = new JsonObject { ["actorId"] = "denied", ["tenantId"] = "tenant-denied" },
            },
        };
    }

    private static JsonObject CreateMetricManifest(string workspace, Uri gateway, bool omitSelectionContext)
    {
        JsonObject manifest = CreateManifest(workspace, gateway, "session-history-metric");
        JsonObject plan = manifest["fixturePlan"]!.AsObject();
        plan.Remove("deniedActor");
        if (omitSelectionContext)
            plan.Remove("selectionContext");
        return manifest;
    }

    private static JsonObject CreateBoardManifest() => new()
    {
        ["executionIntent"] = "execute",
        ["journey"] = "board-delete",
        ["environment"] = new JsonObject
        {
            ["baseUrl"] = "https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/",
            ["issuer"] = "https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/",
            ["audience"] = "https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/",
            ["tenantId"] = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        },
        ["pins"] = new JsonObject
        {
            ["context"] = "board-test-context",
            ["namespace"] = "agentweaver",
            ["sourceReceipt"] = new JsonObject
            {
                ["sourceSha"] = new string('a', 40),
                ["sourceTree"] = new string('b', 40),
            },
            ["orchestratorSourceReceipt"] = new JsonObject
            {
                ["sourceSha"] = new string('e', 40),
                ["sourceTree"] = new string('f', 40),
            },
            ["authoritySourceReceipt"] = new JsonObject
            {
                ["sourceSha"] = new string('c', 40),
                ["sourceTree"] = new string('d', 40),
            },
        },
        ["deploymentEvidence"] = new JsonObject
        {
            ["manifestPath"] = "foundation.json",
            ["imageManifestPath"] = "images.json",
        },
        ["fixturePlan"] = new JsonObject { ["kind"] = "owned-disposable-board" },
    };

    private static async Task<WebApplication> StartAcceptanceGatewayProxyAsync(HttpMessageInvoker invoker)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            var target = new Uri(
                $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}");
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            using HttpResponseMessage response = await invoker.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in response.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync();
        return app;
    }

    private sealed class TestDeploymentEvidenceProvider : IAcceptanceDeploymentEvidenceProvider
    {
        public int PrepareCalls { get; private set; }

        public Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct)
        {
            PrepareCalls++;
            return Task.FromResult(new JsonObject
            {
                ["source"] = new JsonObject
                {
                    ["sourceSha"] = manifest.Pins["sourceReceipt"]!["sourceSha"]!.DeepClone(),
                    ["sourceTree"] = manifest.Pins["sourceReceipt"]!["sourceTree"]!.DeepClone(),
                },
                ["services"] = new JsonObject { ["status"] = "passed" },
                ["agentHostImageMeasurement"] = new JsonObject { ["status"] = "passed" },
            });
        }

    }

    private sealed class BlockingDeploymentEvidenceProvider : IAcceptanceDeploymentEvidenceProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prepareCalls;

        public Task Entered => _entered.Task;
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);

        public void Release() => _release.TrySetResult();

        public async Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct)
        {
            Interlocked.Increment(ref _prepareCalls);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return await new TestDeploymentEvidenceProvider().PrepareAsync(manifest, ct);
        }
    }

    private sealed class NoRequestsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("The dry-run path must not issue HTTP requests.");
    }

    private sealed class TestAuthentication(
        ConcurrentQueue<string> trace,
        string? failRunBoundRunId = null) : ICliAuthentication
    {
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "owner", identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "owner", identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            // The controlled Gateway uses loopback HTTP; validate scopes and bindings against production HTTPS origins.
            _ = CliAuthIdentity.Create(identity with
            {
                Target = identity.Target with
                {
                    Gateway = new Uri("https://gateway.test/"),
                    Issuer = new Uri("https://broker.test/"),
                },
            });
            string actor = identity.Target.TenantId == "tenant-denied" ? "denied" : "owner";
            string binding = identity.ProjectId is null ? "unbound" : "project-bound";
            string scopes = string.Join(',', identity.Scopes.Order(StringComparer.Ordinal));
            trace.Enqueue($"auth:{actor}:{interaction}:{binding}:{scopes}");
            if (failRunBoundRunId is not null && identity.RunId == failRunBoundRunId)
                throw new InvalidOperationException("Controlled run-bound authorization failure.");
            return Task.FromResult(string.Join('~', actor, identity.Target.TenantId,
                identity.ProjectId ?? "-", identity.RunId ?? "-", scopes));
        }
    }

    private sealed class AcceptanceGatewayHandler : HttpMessageHandler
    {
        public const string BoardProjectId = "11111111111141118111111111111111";
        public const string MetricProjectId = "cccccccccccccccccccccccccccccccc";
        public const string InteractiveRunId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string InteractiveRootSessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string P1ProjectId = "11111111-1111-4111-8111-111111111111";
        private readonly JsonObject _openApi;
        private readonly IReadOnlyDictionary<string, GatewayOperation> _operations;
        private readonly ConcurrentQueue<string> _trace;
        private readonly bool _failCleanup;
        private bool _failFirstBoardTaskEdit;
        private readonly bool _failBoardOwnerReadback;
        private bool _ambiguousBoardCapture;
        private readonly bool _preserveCompactedTaskRevision;
        private bool _ambiguousBoardDelete;
        private readonly string? _malformedJournalCase;
        private readonly string? _malformedBoardSnapshot;
        private readonly string? _ownerDenialScenario;
        private readonly CancellationTokenSource? _cancelOnMessage;
        private readonly string? _metricScenario;
        private readonly int _metricCount;
        private int _revokeCalls;
        private string? _rootSessionId;
        private string? _runId;
        private string? _childSessionId;
        private string? _metricArchivedChildId;
        private string? _metricDetachedChildId;
        private JsonObject? _selection;
        private JsonObject? _pendingGate;
        private long _stateVersion;
        private long _executionFence;
        private long _runStateVersion;
        private long _rootStateVersion;
        private long _archivedChildStateVersion;
        private long _detachedChildStateVersion;
        private bool _runCancelled;
        private bool _metricArchived;
        private bool _metricDetached;
        private bool _metricChildCompleted;
        private readonly List<JsonObject> _boardTasks = [];
        private readonly List<JsonObject> _assignments = [];
        private readonly List<(string TaskId, string PrerequisiteId)> _boardDependencies = [];
        private long _boardGraphRevision;
        private long _boardProjectRevision = 1;
        private long _projectRevision = 1;
        private string? _projectId;
        private string? _boardProjectName;
        private bool _projectArchived;
        private bool _boardOwner;
        private bool _boardProject;
        private readonly List<JsonObject> _journalEvents = [];
        public JsonArray? LastReplayedEvents { get; private set; }
        public long? LastCompactedTaskRevision { get; private set; }
        public long? LastCompactedTaskPosition { get; private set; }
        public IReadOnlyList<JsonObject> JournalEvents => _journalEvents;
        public JsonObject? JournalMessageReceipt { get; private set; }

        public AcceptanceGatewayHandler(
            JsonObject openApi,
            ConcurrentQueue<string> trace,
            bool failCleanup,
            string? malformedJournalCase = null,
            CancellationTokenSource? cancelOnMessage = null,
            bool ambiguousBoardCapture = false,
            bool ambiguousBoardDelete = false,
            bool failBoardOwnerReadback = false,
            bool preserveCompactedTaskRevision = false,
            bool failFirstBoardTaskEdit = false,
            string? malformedBoardSnapshot = null,
            string? ownerDenialScenario = null,
            string? metricScenario = null,
            int metricCount = 2)
        {
            _openApi = (JsonObject)openApi.DeepClone();
            _operations = GatewayContract.Parse(_openApi);
            _trace = trace;
            _failCleanup = failCleanup;
            _failFirstBoardTaskEdit = failFirstBoardTaskEdit;
            _failBoardOwnerReadback = failBoardOwnerReadback;
            _ambiguousBoardCapture = ambiguousBoardCapture;
            _preserveCompactedTaskRevision = preserveCompactedTaskRevision;
            _ambiguousBoardDelete = ambiguousBoardDelete;
            _malformedJournalCase = malformedJournalCase;
            _malformedBoardSnapshot = malformedBoardSnapshot;
            _ownerDenialScenario = ownerDenialScenario;
            _cancelOnMessage = cancelOnMessage;
            _metricScenario = metricScenario;
            _metricCount = metricCount;
            GatewayOrigin = new Uri("http://127.0.0.1:49241/");
        }

        public Uri GatewayOrigin { get; set; }
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/openapi/v1.json")
                return Json(HttpStatusCode.OK, _openApi);

            GatewayOperation operation = MatchOperation(request.Method.Method, path);
            Dictionary<string, string> parameters = MatchPath(operation.Path, path);
            JsonObject? body = request.Content is null
                ? null
                : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            Dictionary<string, string> query = ParseQuery(request.RequestUri.Query);
            string[] token = (request.Headers.Authorization?.Parameter ?? "").Split('~');
            string actor = token.ElementAtOrDefault(0) ?? "";
            string tenant = token.ElementAtOrDefault(1) ?? "";
            string projectId = parameters.GetValueOrDefault("projectId") ?? "";
            string runId = parameters.GetValueOrDefault("runId") ?? "";
            Requests.Enqueue(new RecordedRequest(operation.OperationId, projectId, runId,
                body is null ? null : (JsonObject)body.DeepClone(), request.Headers.ToDictionary(
                    header => header.Key, header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase)));
            _trace.Enqueue($"http:{operation.OperationId}");
            if (operation.OperationId == "sendMessage" && _cancelOnMessage is not null)
            {
                _cancelOnMessage.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            if (operation.OperationId == "getAuthorizationContext")
            {
                JsonArray authority = _boardProject && _boardOwner && actor == "owner"
                    ? new JsonArray(new JsonObject
                    {
                        ["resourceType"] = "project",
                        ["resourceId"] = BoardProjectId,
                        ["permissions"] = new JsonArray(
                            new JsonObject { ["permission"] = "readProjects", ["roleRevision"] = 1 },
                            new JsonObject { ["permission"] = "writeProjects", ["roleRevision"] = 1 }),
                    })
                    : token.ElementAtOrDefault(2) is { } authorizedProject and not "-"
                        ? new JsonArray(new JsonObject
                        {
                            ["resourceType"] = "project",
                            ["resourceId"] = authorizedProject,
                            ["permissions"] = new JsonArray(new JsonObject
                            {
                                ["permission"] = "accessPrivateKnowledge",
                                ["roleRevision"] = 1,
                            }),
                        })
                        : new JsonArray();
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["contractVersion"] = 1,
                    ["issuer"] = GatewayOrigin.Host.Equals(
                        "api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io", StringComparison.OrdinalIgnoreCase)
                        ? "https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/"
                        : "https://issuer.example/",
                    ["actorId"] = actor,
                    ["tenantId"] = tenant,
                    ["membershipRevision"] = 1,
                    ["boundProjectId"] = token.ElementAtOrDefault(2) is { } boundProject and not "-" ? boundProject : null,
                    ["boundRunId"] = token.ElementAtOrDefault(3) is { } boundRun and not "-" ? boundRun : null,
                    ["effectiveAuthority"] = authority,
                });
            }

            string[] scopes = token.ElementAtOrDefault(4)?.Split([',', '+'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            string? requiredScope = operation.OperationId switch
            {
                "createProject" or "assignProjectRole" or "listProjectRoleAssignments" or "getProjectConfiguration"
                    or "revokeProjectRoleAssignment" or "updateProject" => "projects.admin",
                "acceptRunSelection" or "getRunSelection" or "createProjectRun" or "cancelProjectRun"
                    or "acceptRunRoot" or "readRunStatus"
                    or "readSessionStatus" or "registerChild" or "readSessionTree" or "readDecisions"
                    or "askQuestion" or "requestApproval" or "answerGate" or "approveGate" or "rejectGate"
                    or "sendProjectRunMessage" or "replayRunEvents" or "sendMessage"
                    or "advanceTurnBoundary" or "completeTurn" or "archiveChild" or "detachSession"
                    or "readProjectMetrics" => "projects.orchestrator",
                _ => null,
            };
            bool selectionOperation = operation.OperationId is "acceptRunSelection" or "getRunSelection" or "createProjectRun";
            bool unboundRunOperation = operation.OperationId is "createProjectRun" or "cancelProjectRun";
            bool invalidBinding = requiredScope == "projects.orchestrator"
                && (selectionOperation || unboundRunOperation
                    ? token.ElementAtOrDefault(2) != "-" || token.ElementAtOrDefault(3) != "-"
                    : token.ElementAtOrDefault(2) == "-" || token.ElementAtOrDefault(3) == "-");
            if (requiredScope is not null
                && (!scopes.Contains(requiredScope, StringComparer.Ordinal)
                    || scopes.Contains("projects.admin", StringComparer.Ordinal)
                        && scopes.Contains("projects.orchestrator", StringComparer.Ordinal)
                    || invalidBinding))
                return Json(HttpStatusCode.Forbidden, new JsonObject { ["title"] = "Denied" }, "application/problem+json");

            bool backlogOperation = operation.OperationId is "readProjectBacklog" or "captureBacklogTask"
                or "editBacklogTaskContent" or "addProjectBacklogDependency" or "deleteProjectBacklogTask";
            if (backlogOperation && !scopes.Contains("projects.admin", StringComparer.Ordinal)
                && !scopes.Contains("projects.orchestrator", StringComparer.Ordinal))
                return Json(HttpStatusCode.Forbidden, new JsonObject { ["title"] = "Denied" }, "application/problem+json");
            if (operation.OperationId == "deleteProjectBacklogTask"
                && scopes.Contains("projects.orchestrator", StringComparer.Ordinal))
            {
                if (_ownerDenialScenario == "owner-gateway-problem")
                    return Json(HttpStatusCode.Forbidden, new JsonObject
                    {
                        ["title"] = "Forbidden",
                        ["status"] = 403,
                        ["code"] = "forbidden",
                    }, "application/problem+json");
                if (_ownerDenialScenario == "owner-bare-403")
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (_ownerDenialScenario == "owner-graph-changed")
                    _boardGraphRevision++;
                return Json(HttpStatusCode.Forbidden,
                    new JsonObject { ["error"] = "backlog_project_permission_denied" });
            }

            if (actor == "denied" && operation.OperationId == "readRunStatus")
                return Json(HttpStatusCode.Forbidden, new JsonObject { ["title"] = "Denied" }, "application/problem+json");

            return operation.OperationId switch
            {
                "createProject" => CreateProject(body, actor),
                "getProject" => ReadProject(projectId),
                "assignProjectRole" => Assignment(body, projectId, actor),
                "listProjectRoleAssignments" => ListAssignments(projectId),
                "getProjectConfiguration" => Json(HttpStatusCode.OK, new JsonObject
                {
                    ["projectId"] = projectId,
                    ["revision"] = 1,
                }),
                "acceptRunSelection" => AcceptSelection(body, projectId, runId),
                "getRunSelection" => GetRunSelection(),
                "createProjectRun" => CreateInteractiveRun(body, projectId),
                "cancelProjectRun" => CancelRun(projectId, runId),
                "acceptRunRoot" => AcceptRoot(body, projectId, runId),
                "readRunStatus" => RunStatus(projectId, runId),
                "readSessionStatus" => SessionStatus(projectId, runId, parameters.GetValueOrDefault("sessionId") ?? ""),
                "registerChild" => RegisterChild(body, projectId, runId),
                "readSessionTree" => SessionTree(projectId, runId),
                "readDecisions" => ReadDecisions(),
                "advanceTurnBoundary" => AdvanceTurnBoundary(body, parameters.GetValueOrDefault("sessionId") ?? ""),
                "completeTurn" => CompleteTurn(body, parameters.GetValueOrDefault("sessionId") ?? ""),
                "archiveChild" => ArchiveChild(body, parameters),
                "detachSession" => DetachChild(body, parameters.GetValueOrDefault("sessionId") ?? ""),
                "readProjectMetrics" => ReadProjectMetrics(projectId),
                "askQuestion" or "requestApproval" => CreateGate(body, actor),
                "answerGate" or "approveGate" or "rejectGate" => ResolveGate(),
                "sendProjectRunMessage" => JournalMessage(projectId, runId, actor),
                "replayRunEvents" => ReplayEvents(projectId, runId),
                "sendMessage" => Json(HttpStatusCode.OK, new JsonObject
                {
                    ["ownerMessageId"] = "message-owner-receipt",
                    ["recipientSessionId"] = body?["recipientSessionId"]?.DeepClone(),
                    ["status"] = "accepted",
                    ["refreshToken"] = "refresh-token-canary",
                    ["headers"] = new JsonObject { ["name"] = "secret", ["value"] = "descriptor-secret-canary" },
                    ["details"] = "https://example.test/callback?token=query-secret-canary",
                }),
                "revokeProjectRoleAssignment" => Revoke(parameters, query, projectId, actor),
                "updateProject" => UpdateProject(body, projectId),
                "readProjectBacklog" => ReadBoard(projectId),
                "captureBacklogTask" => CaptureBoardTask(body, projectId),
                "editBacklogTaskContent" => EditBoardTask(body, parameters, projectId),
                "addProjectBacklogDependency" => AddBoardDependency(body, parameters, projectId),
                "deleteProjectBacklogTask" => DeleteBoardTask(body, parameters, projectId),
                _ => Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = operation.OperationId }),
            };
        }

        private GatewayOperation MatchOperation(string method, string path) =>
            _operations.Values.SingleOrDefault(operation =>
                operation.Method.Equals(method, StringComparison.OrdinalIgnoreCase) &&
                MatchPathOrNull(operation.Path, path) is not null)
            ?? throw new InvalidOperationException($"No OpenAPI route matches {method} {path}.");

        private static Dictionary<string, string> MatchPath(string template, string path) =>
            MatchPathOrNull(template, path) ?? throw new InvalidOperationException($"No route match for {path}.");

        private static Dictionary<string, string>? MatchPathOrNull(string template, string path)
        {
            string pattern = "^" + Regex.Replace(Regex.Escape(template), @"\\\{(?<name>[^}]+)\}",
                match => $"(?<{match.Groups["name"].Value}>[^/]+)") + "$";
            Match match = Regex.Match(path, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
                return null;
            return Regex.Matches(template, @"\{(?<name>[^}]+)\}")
                .ToDictionary(item => item.Groups["name"].Value,
                    item => Uri.UnescapeDataString(match.Groups[item.Groups["name"].Value].Value),
                    StringComparer.Ordinal);
        }

        private HttpResponseMessage AcceptSelection(JsonObject? body, string projectId, string runId)
        {
            JsonObject context = body?["context"] as JsonObject ?? [];
            _runId = runId;
            _selection = new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = runId,
                ["projectRevision"] = 1,
                ["projectConfigurationRevision"] = body?["expectedProjectConfigRevision"]?.DeepClone(),
                ["platformRuntimeRevision"] = body?["expectedPlatformRuntimeRevision"]?.DeepClone(),
                ["contextRevision"] = context["revision"]?.DeepClone(),
            };
            return Json(HttpStatusCode.OK, (JsonObject)_selection.DeepClone());
        }

        private HttpResponseMessage CreateInteractiveRun(JsonObject? body, string projectId)
        {
            _runId = InteractiveRunId;
            _rootSessionId = InteractiveRootSessionId;
            _executionFence = 0;
            _runStateVersion = 0;
            _rootStateVersion = 0;
            _runCancelled = false;
            _selection = new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = _runId,
                ["projectRevision"] = 1,
                ["projectConfigurationRevision"] = body?["expectedProjectConfigRevision"]?.DeepClone(),
                ["platformRuntimeRevision"] = 1,
                ["contextRevision"] = "interactive-v1",
                ["snapshot"] = new JsonObject { ["selection"] = "effective" },
            };
            if (_metricScenario == "selection-revision")
                _selection["projectConfigurationRevision"] = 2L;
            if (_metricScenario == "selection-malformed-revision")
                _selection["platformRuntimeRevision"] = "1";
            if (_metricScenario == "create-lost-response")
                return Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["title"] = "Creation response lost" });
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["runId"] = _runId,
                ["rootSessionId"] = _rootSessionId,
                ["state"] = "awaiting_binding",
                ["binding"] = new JsonObject { ["projectId"] = projectId, ["runId"] = _runId },
            });
        }

        private HttpResponseMessage GetRunSelection() =>
            _metricScenario == "selection-lost-response"
                ? Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["title"] = "Selection readback unavailable" })
                : Json(HttpStatusCode.OK, (JsonObject)_selection!.DeepClone());

        private HttpResponseMessage CancelRun(string projectId, string runId)
        {
            if (_metricScenario == "cancel-failure")
                return Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["title"] = "Cancellation unavailable" });
            if (_executionFence > 0 && !_runCancelled)
            {
                _runCancelled = true;
                _executionFence++;
                _runStateVersion++;
                _rootStateVersion++;
                _archivedChildStateVersion++;
                _detachedChildStateVersion++;
            }
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["runId"] = runId,
                ["state"] = "cancelled",
                ["cancelledAt"] = "2026-11-03T17:00:00Z",
                ["alreadyTerminal"] = false,
            });
        }

        private HttpResponseMessage AcceptRoot(JsonObject? body, string projectId, string runId)
        {
            _rootSessionId = body?["sessionId"]?.GetValue<string>();
            _runId = runId;
            _executionFence = 1;
            _runStateVersion = 0;
            _rootStateVersion = 0;
            _runCancelled = false;
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = runId,
                ["rootSessionId"] = _rootSessionId,
                ["executionFence"] = _executionFence,
                ["stateVersion"] = _rootStateVersion,
            });
        }

        private HttpResponseMessage RunStatus(string projectId, string runId) =>
            _executionFence == 0
                ? Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "No accepted run" })
                : Json(HttpStatusCode.OK, new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = runId,
                ["rootSessionId"] = _rootSessionId,
                ["executionFence"] = _executionFence,
                ["stateVersion"] = _runStateVersion,
                ["executionState"] = _runCancelled ? "cancelled" : "running",
            });

        private HttpResponseMessage SessionStatus(string projectId, string runId, string sessionId)
        {
            if (_executionFence == 0)
                return Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "No accepted run" });
            string rootSessionId = sessionId == _metricDetachedChildId && _metricDetached
                ? _metricDetachedChildId!
                : _rootSessionId!;
            string lifecycle = sessionId == _rootSessionId
                ? _runCancelled ? "cancelled" : "active"
                : sessionId == _metricArchivedChildId
                    ? _metricArchived ? "archived" : _metricChildCompleted ? "completed" : "active"
                    : sessionId == _metricDetachedChildId
                        ? _runCancelled ? "cancelled" : "active"
                        : "active";
            long stateVersion = sessionId == _rootSessionId
                ? _rootStateVersion
                : sessionId == _metricArchivedChildId
                    ? _archivedChildStateVersion
                    : sessionId == _metricDetachedChildId
                        ? _detachedChildStateVersion
                        : _stateVersion;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["identity"] = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["runId"] = runId,
                    ["sessionId"] = sessionId,
                },
                ["rootSessionId"] = rootSessionId,
                ["detached"] = sessionId == _metricDetachedChildId && _metricDetached,
                ["lifecycle"] = lifecycle,
                ["executionFence"] = _executionFence,
                ["stateVersion"] = stateVersion,
                ["blockers"] = _pendingGate is null
                    ? new JsonArray()
                    : new JsonArray(new JsonObject
                    {
                        ["requestId"] = _pendingGate["requestId"]?.DeepClone(),
                        ["kind"] = _pendingGate["kind"]?.GetValue<string>() == "question"
                            ? "awaitingInput"
                            : "awaitingApproval",
                        ["choices"] = _pendingGate["allowedChoices"]?.DeepClone(),
                        ["allowsFreeform"] = _pendingGate["allowsFreeform"]?.DeepClone(),
                    }),
            });
        }

        private HttpResponseMessage RegisterChild(JsonObject? body, string projectId, string runId)
        {
            string? sessionId = body?["sessionId"]?.GetValue<string>();
            _runId = runId;
            if (sessionId == ApiAcceptanceSupport.StableUuid(runId, "metric-archived-child"))
            {
                _metricArchivedChildId = sessionId;
                _archivedChildStateVersion = 1;
                _metricArchived = false;
                _metricChildCompleted = false;
            }
            else if (sessionId == ApiAcceptanceSupport.StableUuid(runId, "metric-detached-child"))
            {
                _metricDetachedChildId = sessionId;
                _detachedChildStateVersion = 1;
                _metricDetached = false;
            }
            else
            {
                _childSessionId = sessionId;
            }
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["identity"] = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["runId"] = runId,
                    ["sessionId"] = sessionId,
                },
            });
        }

        private HttpResponseMessage SessionTree(string projectId, string runId)
        {
            JsonArray nodes = new(Node(projectId, runId, _rootSessionId!));
            if (_metricArchivedChildId is not null)
                nodes.Add(Node(projectId, runId, _metricArchivedChildId));
            if (_metricDetachedChildId is not null && !_metricDetached)
                nodes.Add(Node(projectId, runId, _metricDetachedChildId));
            if (_childSessionId is not null)
                nodes.Add(Node(projectId, runId, _childSessionId));
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["rootSessionId"] = _rootSessionId,
                ["nodes"] = nodes,
            });
        }

        private HttpResponseMessage AdvanceTurnBoundary(JsonObject? body, string sessionId)
        {
            if (sessionId != _metricArchivedChildId
                || body?["executionFence"]?.GetValue<long>() != _executionFence
                || body?["expectedStateVersion"]?.GetValue<long>() != _archivedChildStateVersion
                || _metricArchived)
                return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Stale metric child" });
            _archivedChildStateVersion++;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["session"] = new JsonObject { ["sessionId"] = sessionId },
                ["executionFence"] = _executionFence,
                ["stateVersion"] = _archivedChildStateVersion,
            });
        }

        private HttpResponseMessage CompleteTurn(JsonObject? body, string sessionId)
        {
            if (sessionId != _metricArchivedChildId
                || body?["executionFence"]?.GetValue<long>() != _executionFence
                || body?["expectedStateVersion"]?.GetValue<long>() != _archivedChildStateVersion
                || _metricArchived)
                return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Stale metric child" });
            _archivedChildStateVersion++;
            _metricChildCompleted = true;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["session"] = new JsonObject { ["sessionId"] = sessionId },
                ["executionFence"] = _executionFence,
                ["stateVersion"] = _archivedChildStateVersion,
                ["executionState"] = "completed",
            });
        }

        private HttpResponseMessage ArchiveChild(JsonObject? body, IReadOnlyDictionary<string, string> parameters)
        {
            string childSessionId = parameters.GetValueOrDefault("childSessionId") ?? "";
            if (parameters.GetValueOrDefault("parentSessionId") != _rootSessionId
                || childSessionId != _metricArchivedChildId
                || body?["executionFence"]?.GetValue<long>() != _executionFence
                || _archivedChildStateVersion < 3)
                return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Metric child is not archivable" });
            _metricArchived = true;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["command"] = "archive",
                ["sourceSessionId"] = _rootSessionId,
                ["targetSessionId"] = childSessionId,
                ["state"] = "archived",
                ["executionFence"] = _executionFence,
            });
        }

        private HttpResponseMessage DetachChild(JsonObject? body, string sessionId)
        {
            if (sessionId != _metricDetachedChildId
                || body?["executionFence"]?.GetValue<long>() != _executionFence
                || _metricDetached)
                return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Metric child is not detachable" });
            _metricDetached = true;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["command"] = "detach",
                ["sourceSessionId"] = sessionId,
                ["state"] = "detached",
                ["executionFence"] = _executionFence,
            });
        }

        private HttpResponseMessage ReadProjectMetrics(string projectId) =>
            projectId == _projectId && _metricArchived && _metricDetached
                ? Json(HttpStatusCode.OK, new JsonObject
                {
                    ["projectId"] = projectId,
                    ["from"] = "2000-01-01T00:00:00Z",
                    ["to"] = "2000-01-02T00:00:00Z",
                    ["runsInRange"] = 0,
                    ["runsTotal"] = 1,
                    ["sessionHistoryRecordsTotal"] = _metricCount,
                })
                : Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Metric fixture is incomplete" });

        private JsonObject Node(string projectId, string runId, string sessionId) => new()
        {
            ["identity"] = new JsonObject
            {
                ["projectId"] = projectId,
                ["runId"] = runId,
                ["sessionId"] = sessionId,
            },
            ["parentSessionId"] = sessionId == _rootSessionId ? null : _rootSessionId,
            ["rootSessionId"] = _rootSessionId,
            ["detached"] = sessionId == _metricDetachedChildId && _metricDetached,
            ["lifecycle"] = sessionId == _rootSessionId
                ? _runCancelled ? "cancelled" : "active"
                : sessionId == _metricArchivedChildId && _metricArchived
                    ? "archived"
                    : sessionId == _metricArchivedChildId && _metricChildCompleted
                        ? "completed"
                        : _runCancelled ? "cancelled" : "active",
            ["executionFence"] = _executionFence,
            ["stateVersion"] = sessionId == _rootSessionId
                ? _rootStateVersion
                : sessionId == _metricArchivedChildId
                    ? _archivedChildStateVersion
                    : sessionId == _metricDetachedChildId
                        ? _detachedChildStateVersion
                        : 0L,
        };

        private HttpResponseMessage CreateProject(JsonObject? body, string actor)
        {
            string name = body?["name"]?.GetValue<string>() ?? "";
            _boardProject = name.StartsWith("board-", StringComparison.Ordinal);
            _boardProjectName = name;
            _boardOwner = false;
            _boardGraphRevision = 0;
            _boardProjectRevision = 1;
            _projectRevision = 1;
            _projectArchived = false;
            _assignments.Clear();
            _boardTasks.Clear();
            _boardDependencies.Clear();
            string projectId = _boardProject
                ? BoardProjectId
                : _metricScenario is not null ? MetricProjectId : P1ProjectId;
            _projectId = projectId;
            _assignments.Add(new JsonObject
            {
                ["assignmentId"] = "22222222-2222-4222-8222-222222222222",
                ["projectId"] = projectId,
                ["principalId"] = actor,
                ["role"] = "owner",
                ["state"] = "active",
                ["revision"] = 1,
                ["grantedBy"] = actor,
                ["createdAt"] = "2026-11-02T00:00:00Z",
            });
            if (_boardProject) _boardOwner = actor == "owner";
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["projectId"] = projectId,
                ["name"] = name,
                ["state"] = "active",
                ["revision"] = _boardProjectRevision,
                ["configurationRevision"] = 1,
            });
        }

        private HttpResponseMessage Assignment(JsonObject? body, string projectId, string actor)
        {
            string role = body?["role"]?.GetValue<string>() ?? "";
            if (_boardProject && projectId == BoardProjectId && role == "owner")
                _boardOwner = actor == "owner";
            JsonObject assignment = new()
            {
                ["assignmentId"] = role == "owner"
                    ? "22222222-2222-4222-8222-222222222222"
                    : "33333333-3333-4333-8333-333333333333",
                ["projectId"] = projectId,
                ["principalId"] = body?["principalId"]?.DeepClone(),
                ["role"] = role,
                ["state"] = "active",
                ["revision"] = 1,
                ["grantedBy"] = actor,
            };
            _assignments.Add((JsonObject)assignment.DeepClone());
            return Json(HttpStatusCode.Created, assignment);
        }

        private HttpResponseMessage ReadProject(string projectId)
        {
            if (_metricScenario == "project-readback-unavailable"
                && projectId == _projectId && _projectArchived)
                return Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["title"] = "Project readback unavailable" });
            return projectId == BoardProjectId && _boardProject
                ? Json(HttpStatusCode.OK, new JsonObject
                {
                    ["projectId"] = BoardProjectId,
                    ["name"] = _boardProjectName,
                    ["state"] = _projectArchived ? "archived" : "active",
                    ["revision"] = _boardProjectRevision,
                    ["configurationRevision"] = 1L,
                    ["createdAt"] = "2026-11-02T00:00:00Z",
                    ["updatedAt"] = "2026-11-02T00:00:00Z",
                    ["effectiveRoles"] = new JsonArray(),
                })
                : projectId == _projectId
                    ? Json(HttpStatusCode.OK, new JsonObject
                    {
                        ["projectId"] = _projectId,
                        ["name"] = _boardProjectName,
                            ["state"] = _projectArchived && _metricScenario != "project-readback-contradictory"
                                ? "archived" : "active",
                            ["revision"] = _projectRevision,
                        ["configurationRevision"] = 1L,
                        ["createdAt"] = "2026-11-03T00:00:00Z",
                        ["updatedAt"] = "2026-11-03T00:00:00Z",
                        ["effectiveRoles"] = new JsonArray(),
                    })
                : Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Unknown project" });
        }

        private HttpResponseMessage ListAssignments(string projectId)
        {
            if (_failBoardOwnerReadback && _boardProject)
                return Json(HttpStatusCode.ServiceUnavailable,
                    new JsonObject { ["title"] = "Controlled owner assignment readback failure" });
            JsonArray assignments = new(_assignments
                .Where(assignment => ApiAcceptanceSupport.GetString(assignment, "state") == "active")
                .Select(assignment => new JsonObject
                {
                    ["assignmentId"] = assignment["assignmentId"]?.DeepClone(),
                    ["principalId"] = assignment["principalId"]?.DeepClone(),
                    ["role"] = assignment["role"]?.DeepClone(),
                    ["revision"] = assignment["revision"]?.DeepClone(),
                    ["createdBy"] = assignment["grantedBy"]?.DeepClone(),
                    ["createdAt"] = assignment["createdAt"]?.DeepClone(),
                }).ToArray());
            if (_metricScenario == "assignment-readback-contradictory" && _projectArchived)
            {
                assignments.Add(new JsonObject
                {
                    ["assignmentId"] = "44444444-4444-4444-8444-444444444444",
                    ["principalId"] = "owner",
                    ["role"] = "owner",
                    ["revision"] = 1L,
                    ["createdBy"] = "owner",
                });
            }
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["projectId"] = projectId,
                ["assignments"] = assignments,
            });
        }

        private HttpResponseMessage ReadBoard(string projectId) =>
            projectId == BoardProjectId
                ? Json(HttpStatusCode.OK, BoardGraph())
                : Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Unknown project" });

        private HttpResponseMessage CaptureBoardTask(JsonObject? body, string projectId)
        {
            if (projectId != BoardProjectId)
                return Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Unknown project" });
            long expectedGraph = body?["expectedGraphRevision"]?.GetValue<long>() ?? -1;
            if (expectedGraph != _boardGraphRevision)
                return BoardIssue("staleGraphRevision");
            string taskId = body?["taskId"]?.GetValue<string>() ?? "";
            if (_boardTasks.Any(task => task["reference"]?["taskId"]?.GetValue<string>() == taskId))
                return BoardIssue("taskAlreadyExists");
            JsonObject content = body?["content"] as JsonObject ?? [];
            JsonObject task = new()
            {
                ["reference"] = new JsonObject { ["projectId"] = projectId, ["taskId"] = taskId },
                ["title"] = content["title"]?.DeepClone(),
                ["description"] = content["description"]?.DeepClone(),
                ["state"] = "backlog",
                ["isArchived"] = false,
                ["isAutomationInvocationPending"] = false,
                ["revision"] = 0L,
                ["position"] = _boardTasks.Count,
            };
            switch (_malformedBoardSnapshot)
            {
                case "negative-revision":
                    task["revision"] = -1L;
                    break;
                case "missing-revision":
                    task.Remove("revision");
                    break;
                case "fractional-revision":
                    task["revision"] = 0.5;
                    break;
                case "string-revision":
                    task["revision"] = "0";
                    break;
                case "malformed-task":
                    task.Remove("reference");
                    break;
            }
            _boardTasks.Add(task);
            _boardGraphRevision++;
            if (_ambiguousBoardCapture)
            {
                _ambiguousBoardCapture = false;
                return Json(HttpStatusCode.InternalServerError, new JsonObject { ["title"] = "Acknowledgement unavailable" });
            }
            return Json(HttpStatusCode.OK, BoardGraph());
        }

        private HttpResponseMessage EditBoardTask(
            JsonObject? body,
            IReadOnlyDictionary<string, string> parameters,
            string projectId)
        {
            JsonObject? task = FindBoardTask(parameters["taskId"]);
            if (projectId != BoardProjectId || task is null)
                return Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Unknown task" });
            if (body?["expectedGraphRevision"]?.GetValue<long>() != _boardGraphRevision)
                return BoardIssue("staleGraphRevision");
            if (body?["expectedTaskRevision"]?.GetValue<long>() != task["revision"]?.GetValue<long>())
                return BoardIssue("staleTaskRevision");
            if (_failFirstBoardTaskEdit)
            {
                _failFirstBoardTaskEdit = false;
                return BoardIssue("controlledEditRejection");
            }
            JsonObject content = body?["content"] as JsonObject ?? [];
            task["title"] = content["title"]?.DeepClone();
            task["description"] = content["description"]?.DeepClone();
            task["revision"] = task["revision"]!.GetValue<long>() + 1;
            _boardGraphRevision++;
            return Json(HttpStatusCode.OK, BoardGraph());
        }

        private HttpResponseMessage AddBoardDependency(
            JsonObject? body,
            IReadOnlyDictionary<string, string> parameters,
            string projectId)
        {
            if (projectId != BoardProjectId)
                return Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Unknown project" });
            if (body?["expectedGraphRevision"]?.GetValue<long>() != _boardGraphRevision)
                return BoardIssue("staleGraphRevision");
            string taskId = parameters["taskId"];
            string prerequisiteId = parameters["prerequisiteTaskId"];
            if (FindBoardTask(taskId) is null || FindBoardTask(prerequisiteId) is null)
                return BoardIssue("taskNotFound");
            _boardDependencies.Add((taskId, prerequisiteId));
            _boardGraphRevision++;
            return Json(HttpStatusCode.OK, BoardGraph());
        }

        private HttpResponseMessage DeleteBoardTask(
            JsonObject? body,
            IReadOnlyDictionary<string, string> parameters,
            string projectId)
        {
            if (projectId != BoardProjectId)
                return Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Unknown project" });
            if (body?["expectedGraphRevision"]?.GetValue<long>() != _boardGraphRevision)
                return BoardIssue("staleGraphRevision");
            string taskId = parameters["taskId"];
            JsonObject? task = FindBoardTask(taskId);
            if (task is null)
                return BoardIssue("taskNotFound");
            if (body?["expectedTaskRevision"]?.GetValue<long>() != task["revision"]?.GetValue<long>())
                return BoardIssue("staleTaskRevision");
            if (_boardDependencies.Any(edge => edge.PrerequisiteId == taskId))
                return BoardIssue("taskHasDependents");
            _boardTasks.Remove(task);
            _boardDependencies.RemoveAll(edge => edge.TaskId == taskId || edge.PrerequisiteId == taskId);
            for (int position = 0; position < _boardTasks.Count; position++)
            {
                if (_boardTasks[position]["position"]?.GetValue<int>() == position)
                    continue;
                _boardTasks[position]["position"] = position;
                if (!_preserveCompactedTaskRevision)
                {
                    long revision = _boardTasks[position]["revision"]!.GetValue<long>() + 1;
                    _boardTasks[position]["revision"] = revision;
                }
                LastCompactedTaskPosition = position;
                LastCompactedTaskRevision = _boardTasks[position]["revision"]?.GetValue<long>();
            }
            _boardGraphRevision++;
            if (_ambiguousBoardDelete)
            {
                _ambiguousBoardDelete = false;
                return Json(HttpStatusCode.InternalServerError, new JsonObject { ["title"] = "Acknowledgement unavailable" });
            }
            return Json(HttpStatusCode.OK, BoardGraph());
        }

        private JsonObject BoardGraph() => new()
        {
            ["projectId"] = BoardProjectId,
            ["revision"] = _boardGraphRevision,
            ["tasks"] = new JsonArray(_boardTasks.Select(task => (JsonNode?)task.DeepClone()).ToArray()),
            ["dependencies"] = new JsonArray(_boardDependencies.Select(edge => (JsonNode?)new JsonObject
            {
                ["task"] = new JsonObject { ["projectId"] = BoardProjectId, ["taskId"] = edge.TaskId },
                ["prerequisite"] = new JsonObject { ["projectId"] = BoardProjectId, ["taskId"] = edge.PrerequisiteId },
            }).ToArray()),
        };

        private JsonObject? FindBoardTask(string taskId) =>
            _boardTasks.SingleOrDefault(task => task["reference"]?["taskId"]?.GetValue<string>() == taskId);

        private static HttpResponseMessage BoardIssue(string code) =>
            Json(HttpStatusCode.Conflict, new JsonObject { ["issues"] = new JsonArray(new JsonObject { ["code"] = code }) });

        private HttpResponseMessage ReadDecisions() =>
            Json(HttpStatusCode.OK, new JsonObject
            {
                ["executionFence"] = 1,
                ["stateVersion"] = _stateVersion,
                ["pendingGate"] = _pendingGate?.DeepClone(),
            });

        private HttpResponseMessage CreateGate(JsonObject? body, string actor)
        {
            bool question = body?.ContainsKey("questionId") == true;
            _stateVersion++;
            _pendingGate = new JsonObject
            {
                ["requestId"] = body?["requestId"]?.DeepClone(),
                ["kind"] = question ? "question" : "approval",
                ["subjectId"] = body?[question ? "questionId" : "subjectId"]?.DeepClone(),
                ["authorizedActorId"] = actor,
                ["fence"] = 1,
                ["allowedChoices"] = question
                    ? body?["allowedChoices"]?.DeepClone()
                    : new JsonArray("approve", "reject"),
                ["allowsFreeform"] = question ? body?["allowsFreeform"]?.DeepClone() : JsonValue.Create(false),
                ["prompt"] = body?["prompt"]?.DeepClone(),
                ["resolvesOutcomeClarification"] = false,
            };
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["accepted"] = true,
                ["pendingGate"] = _pendingGate.DeepClone(),
                ["stateVersion"] = _stateVersion,
                ["executionFence"] = 1,
            });
        }

        private HttpResponseMessage ResolveGate()
        {
            _stateVersion++;
            _pendingGate = null;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["accepted"] = true,
                ["decisionId"] = "decision-acceptance",
                ["stateVersion"] = _stateVersion,
                ["executionFence"] = 1,
                ["issues"] = new JsonArray(),
            });
        }

        private HttpResponseMessage JournalMessage(string projectId, string runId, string actor)
        {
            JsonObject receipt = new()
            {
                ["messageId"] = "44444444-4444-4444-8444-444444444444",
                ["ownerMessageId"] = "55555555-5555-4555-8555-555555555555",
                ["projectId"] = projectId,
                ["recipientSessionId"] = _rootSessionId,
                ["rootSessionId"] = _rootSessionId,
                ["runId"] = runId,
                ["sender"] = new JsonObject
                {
                    ["issuer"] = "https://issuer.example/",
                    ["subject"] = actor,
                },
                ["status"] = "accepted",
                ["threadId"] = "66666666-6666-4666-8666-666666666666",
                ["threadSequence"] = _journalEvents.Count + 1,
            };
            JournalMessageReceipt = (JsonObject)receipt.DeepClone();
            _journalEvents.Add(new JsonObject
            {
                ["eventId"] = receipt["messageId"]!.DeepClone(),
                ["identity"] = new JsonObject
                {
                    ["projectId"] = projectId,
                    ["runId"] = runId,
                    ["sessionId"] = _rootSessionId,
                },
                ["position"] = _journalEvents.Count + 1,
            });
            return Json(HttpStatusCode.OK, receipt);
        }

        private HttpResponseMessage ReplayEvents(string projectId, string runId)
        {
            JsonArray events = new();
            foreach (JsonObject journalEvent in _journalEvents)
                events.Add(journalEvent.DeepClone());
            JsonObject page = new()
            {
                ["events"] = events,
                ["hasMore"] = false,
                ["nextCursor"] = "terminal-cursor",
            };
            switch (_malformedJournalCase)
            {
                case "empty-events":
                    page["events"] = new JsonArray();
                    break;
                case "non-object-event":
                    page["events"] = new JsonArray(JsonValue.Create("not-an-event"));
                    break;
                case "missing-has-more":
                    page.Remove("hasMore");
                    break;
                case "non-boolean-has-more":
                    page["hasMore"] = "false";
                    break;
                case "null-terminal-cursor":
                    page["nextCursor"] = null;
                    break;
                case "foreign-event":
                    page["events"]![0]!["identity"]!["projectId"] = "foreign-project";
                    break;
            }
            LastReplayedEvents = (JsonArray)page["events"]!.DeepClone();
            return Json(HttpStatusCode.OK, page);
        }

        private HttpResponseMessage Revoke(
            IReadOnlyDictionary<string, string> parameters,
            IReadOnlyDictionary<string, string> query,
            string projectId,
            string actor)
        {
            if (_failCleanup && Interlocked.Increment(ref _revokeCalls) == 1)
                return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "cleanup blocked" }, "application/problem+json");
            if (!_failCleanup)
                Interlocked.Increment(ref _revokeCalls);
            string assignmentId = parameters["assignmentId"];
            string role = assignmentId.StartsWith("2222", StringComparison.Ordinal) ? "owner" : "orchestrator";
            long expectedRevision = long.Parse(query["expectedRevision"], System.Globalization.CultureInfo.InvariantCulture);
            JsonObject? assignment = _assignments.SingleOrDefault(value =>
                ApiAcceptanceSupport.GetString(value, "assignmentId") == assignmentId);
            if (assignment is not null)
            {
                assignment["state"] = "revoked";
                assignment["revision"] = expectedRevision + 1;
                assignment["revokedBy"] = actor;
            }
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["assignmentId"] = assignmentId,
                ["projectId"] = projectId,
                ["principalId"] = actor,
                ["role"] = role,
                ["state"] = "revoked",
                ["revision"] = expectedRevision + 1,
                ["grantedBy"] = actor,
                ["revokedBy"] = actor,
            });
        }

        private HttpResponseMessage UpdateProject(JsonObject? body, string projectId)
        {
            long expectedRevision = body?["expectedRevision"]?.GetValue<long>() ?? -1;
            long currentRevision = _boardProject ? _boardProjectRevision : _projectRevision;
            if (projectId != _projectId || expectedRevision != currentRevision
                || ApiAcceptanceSupport.GetString(body ?? [], "state") != "archived")
                return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Stale project revision" });
            _projectArchived = true;
            if (_boardProject)
                _boardProjectRevision++;
            else
                _projectRevision++;
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["projectId"] = projectId,
                ["name"] = body?["name"]?.DeepClone(),
                ["state"] = "archived",
                ["revision"] = currentRevision + 1,
                ["configurationRevision"] = 1,
            });
        }

        private static Dictionary<string, string> ParseQuery(string query) =>
            query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "",
                    StringComparer.Ordinal);

        private static HttpResponseMessage Json(
            HttpStatusCode status,
            JsonObject body,
            string contentType = "application/json") =>
            new(status)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType),
            };
    }

    private sealed record RecordedRequest(
        string OperationId,
        string ProjectId,
        string RunId,
        JsonObject? Body,
        IReadOnlyDictionary<string, string> Headers);
}
