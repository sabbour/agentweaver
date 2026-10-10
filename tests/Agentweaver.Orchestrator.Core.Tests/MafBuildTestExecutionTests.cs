using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBuildTestExecutionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void TerminalAndRequiredOutputJoinAdvancesTheDependentModelFrontier()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var initial = new MafExecutionCheckpointSnapshot(new("root", "initial"),
            new(1, "plan-1", 3, MafExecutionProgress.Empty));
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        var completed = CoordinationEndpoints.PrepareBuildTestCompletionCheckpoint(
            new(new("root", intent.CheckpointId), running), intent, Operation(intent))!;
        var recovered = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(completed));
        var definition = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Platform("build-test", 0, [], WorkflowPlatformGate.BuildTest) with
            {
                BuildTestCommand = intent.Command
            },
            WorkflowTestData.Open("implement", 1, ["build-test"])));
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            definition, WorkflowTestData.Plan(WorkflowTestData.Item("implementation", providerId: "sandbox-1")),
            WorkflowTestData.SelectionContext(WorkflowTestData.PinnedSandboxBinding(
                "sandbox-1", SandboxCapabilities.BuildTestCommandPod, "sandbox.workspace.write"))).Value!;

        var blocked = MafExecutionPlanner.BuildFrontier(
            plan, running.Progress, 0, 0, 8, 2, MafBuildTestCommandContract.IsExecutable);
        var ready = MafExecutionPlanner.BuildFrontier(
            plan, recovered.Progress, 0, 0, 8, 2, MafBuildTestCommandContract.IsExecutable);

        Assert.Empty(blocked.ReadyActions);
        Assert.Equal("implementation", Assert.Single(ready.ReadyActions).WorkItem!.Id);
        Assert.Equal(MafExecutionTaskStatus.Succeeded, recovered.Progress.NonModelSteps["build-test"]);
        Assert.Equal(intent.OperationId, recovered.BuildTestReceipts["build-test"].OperationId);
        var summary = CoordinationEndpoints.BuildTestResultSummary(recovered.BuildTestReceipts["build-test"]);
        Assert.Contains(recovered.BuildTestReceipts["build-test"].CollectorManifestSha256!, summary);
        Assert.True(summary.Length < AddressedMessageValidation.MaximumTextLength);
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("collector")]
    [InlineData("output")]
    [InlineData("truncated")]
    [InlineData("binding")]
    [InlineData("operation")]
    [InlineData("fingerprint")]
    public void LogsOrExitZeroCannotReplaceTheCompleteBoundTerminalJoin(string fault)
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var operation = Operation(intent);
        operation = fault switch
        {
            "terminal" => operation with { Terminal = null },
            "collector" => operation with { CollectorTerminal = null },
            "output" => operation with
            {
                OutputEvidence = [operation.OutputEvidence[0] with
                {
                    Exists = false, CapturedBytes = 0, CapturedSha256 = null
                }]
            },
            "truncated" => operation with
            {
                Output = operation.Output! with
                {
                    Truncated = true, AtLeastObservedBytes = operation.Output.CapturedByteCount + 1
                }
            },
            "binding" => operation with
            {
                CurrentBinding = operation.CurrentBinding! with { DataGeneration = 99 }
            },
            "operation" => operation with { OperationId = Guid.NewGuid() },
            "fingerprint" => operation with { RequestFingerprint = new string('c', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var failure = Record.Exception(() => MafBuildTestCommandContract.Classify(intent, operation));

        Assert.True(failure is ArgumentException or CoordinationException,
            $"Expected a contract rejection, got {failure?.GetType().Name ?? "success"}.");
    }

    [Fact]
    public void InterruptedAndFailedOperationsBlockDownstreamAndCannotCreateSuccessProgress()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var completed = Operation(intent);
        var failed = completed with
        {
            Status = SandboxBuildTestOperationStatus.Failed,
            Terminal = completed.Terminal! with { ExitCode = 1 },
            FailureCode = "command_failed"
        };
        var interrupted = completed with
        {
            Status = SandboxBuildTestOperationStatus.Interrupted,
            Terminal = completed.Terminal! with { Kind = SandboxBuildTestTerminationKind.TimedOut, ExitCode = 137 },
            FailureCode = "command_timeout"
        };

        Assert.Equal(MafExecutionTaskStatus.Failed, MafBuildTestCommandContract.Classify(intent, failed));
        Assert.Equal(MafExecutionTaskStatus.Indeterminate, MafBuildTestCommandContract.Classify(intent, interrupted));
        var initial = new MafExecutionCheckpointSnapshot(new("root", "initial"),
            new(1, "plan-1", 3, MafExecutionProgress.Empty));
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.Serialize(
            running with
            {
                Progress = running.Progress with
                {
                    NonModelSteps = running.Progress.NonModelSteps.SetItem("build-test", MafExecutionTaskStatus.Succeeded)
                }
            }));
    }

    [Fact]
    public void TerminalCheckpointReplayRetainsTheOriginalOperationAndReceipt()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var initial = new MafExecutionCheckpointSnapshot(new("root", "initial"),
            new(1, "plan-1", 3, MafExecutionProgress.Empty));
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        var operation = Operation(intent);
        var completed = CoordinationEndpoints.PrepareBuildTestCompletionCheckpoint(
            new(new("root", intent.CheckpointId), running), intent, operation)!;

        Assert.Null(CoordinationEndpoints.PrepareBuildTestCompletionCheckpoint(
            new(new("root", "completed"), completed), intent,
            operation with { UpdatedAt = operation.UpdatedAt.AddSeconds(1) }));
        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(
            completed, completed with { Revision = 4, BuildTestReceipts = completed.BuildTestReceipts.Clear() }));
    }

    [Fact]
    public void MaximumLogCaptureDoesNotExpandTheDurableReceiptOrDownstreamPrompt()
    {
        var intent = MafBuildTestCheckpointTests.Intent() with
        {
            ExecutionOptions = MafBuildTestCheckpointTests.ExecutionOptions() with { MaximumOutputBytes = 1_048_576 }
        };
        var operation = Operation(intent, new byte[1_048_576]);
        var receipt = MafBuildTestTerminalReceipt.Create(intent, operation);

        Assert.Equal(1_048_576, receipt.OutputBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(receipt).Length < 65_536);
        Assert.True(CoordinationEndpoints.BuildTestResultSummary(receipt).Length <
            AddressedMessageValidation.MaximumTextLength);
    }

    [Fact]
    public async Task LostPostAcknowledgmentReconcilesTheSameOperationWithoutPreparingOrStartingAgain()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        SandboxBuildTestOperationSnapshot? stored = null;
        var starts = 0;
        var reconciles = 0;
        var handler = new Handler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return stored is null ? Response(HttpStatusCode.NotFound, null) :
                    Response(HttpStatusCode.OK, new SandboxBuildTestOperationResult(stored, true));
            var raw = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("executionOptions", raw);
            Assert.DoesNotContain("acceptedCommand", raw);
            var refs = JsonSerializer.Deserialize<SandboxBuildTestApiRequest>(raw, JsonOptions)!;
            Assert.Equal(intent.CheckpointReference(), refs.Checkpoint);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(intent.ExpectedBinding, JsonOptions),
                JsonSerializer.SerializeToElement(refs.ExpectedBinding, JsonOptions)));
            if (path.EndsWith("/reconcile", StringComparison.Ordinal))
            {
                reconciles++;
                Assert.Contains(intent.OperationId.ToString("D"), path);
                stored = Operation(intent);
                return Response(HttpStatusCode.OK, new SandboxBuildTestOperationResult(stored, true));
            }
            starts++;
            stored = RunningOperation(intent);
            throw new HttpRequestException("The owner accepted the command but the acknowledgment was lost.");
        });
        using var http = new HttpClient(handler);
        var client = new MafBuildTestEnvironmentClient(http, new(new("https://environment.test/")));
        var credential = new SecretCredential("fixture-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, "tenant-1");
        try
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                client.ExecuteOrReconcileAsync(actor, intent, CancellationToken.None));
            Assert.Equal("maf_execution_build_test_owner_unavailable", failure.Code);
            var recovered = await client.ExecuteOrReconcileAsync(actor, intent, CancellationToken.None);
            Assert.Equal(MafExecutionTaskStatus.Succeeded, MafBuildTestCommandContract.Classify(intent, recovered));
            var replayed = await client.ExecuteOrReconcileAsync(actor, intent, CancellationToken.None);
            Assert.Equal(MafExecutionTaskStatus.Succeeded, MafBuildTestCommandContract.Classify(intent, replayed));
            Assert.Equal(1, starts);
            Assert.Equal(1, reconciles);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task InvalidOwnerReadShapeCannotStartOrReconcileACommand()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var reads = 0;
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            reads++;
            return Task.FromResult(Response(HttpStatusCode.OK, Operation(intent)));
        });
        using var http = new HttpClient(handler);
        var client = new MafBuildTestEnvironmentClient(http, new(new("https://environment.test/")));
        var credential = new SecretCredential("fixture-token", DateTimeOffset.UtcNow.AddMinutes(2), TimeProvider.System);
        try
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                client.ExecuteOrReconcileAsync(new(credential, "tenant-1"), intent, CancellationToken.None));
            Assert.Equal("maf_execution_build_test_owner_unavailable", failure.Code);
            Assert.Equal(1, reads);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    internal static SandboxBuildTestOperationSnapshot Operation(
        MafExecutionBuildTestIntent intent,
        byte[]? logBytes = null)
    {
        var accepted = intent.ToAcceptedCommand();
        var expected = intent.ExpectedBinding;
        var fingerprint = intent.ToApiRequest().ComputeRequestFingerprint(accepted);
        var commandPolicy = new SandboxBuildTestCommandNetworkPolicyBinding(
            "command-policy", new string('a', 64), expected.NetworkPolicyGeneration, Labels("command"));
        var collectorPolicy = new SandboxBuildTestOutputCollectorNetworkPolicyBinding(
            "collector-policy", new string('b', 64), expected.NetworkPolicyGeneration, Labels("collector"));
        var pod = new SandboxBuildTestPodReference("namespace", "command", "command-uid", "1");
        var collector = new SandboxBuildTestPodReference("namespace", "collector", "collector-uid", "2");
        var start = DateTimeOffset.Parse("2026-10-10T01:00:00Z");
        var terminal = new SandboxBuildTestTerminalEvidence(
            pod.Uid, "build-test", SandboxBuildTestTerminationKind.Exited, 0, start, start.AddSeconds(1), null);
        var collectorTerminal = new SandboxBuildTestTerminalEvidence(
            collector.Uid, SandboxBuildTestLimits.OutputCollectorContainerName,
            SandboxBuildTestTerminationKind.Exited, 0, start.AddSeconds(1), start.AddSeconds(2), null);
        var bytes = logBytes ?? Encoding.UTF8.GetBytes("compiler output, not a success assertion");
        var output = new SandboxBuildTestOutputCapture(
            pod.Uid, terminal.ContainerName, bytes.ToImmutableArray(), bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), false, bytes.Length);
        var evidence = new SandboxBuildTestOutputEvidence(
            collector.Uid, SandboxBuildTestLimits.OutputCollectorContainerName, accepted.Outputs[0].Name,
            accepted.Outputs[0].RelativePath, true, accepted.Outputs[0].MaximumBytes,
            true, 3, Convert.ToHexStringLower(SHA256.HashData("xml"u8)));
        var receipt = new SandboxBuildTestOutputCollectorReceipt(
            1, intent.OperationId, accepted.ImmutableHash, fingerprint, accepted.Checkpoint,
            collector.Uid, SandboxBuildTestLimits.OutputCollectorContainerName, [evidence], "");
        var manifest = SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(receipt);
        return new(intent.OperationId, accepted.ImmutableHash, fingerprint, SandboxBuildTestOperationStatus.Completed,
            expected, commandPolicy,
            new(expected.Fence, expected.SandboxLeaseOperationId, expected.SandboxResource,
                expected.ProviderFencingGeneration, expected.WorkspaceVolume, expected.DataGeneration,
                expected.NetworkPolicyGeneration, commandPolicy),
            pod, terminal, output, collector, collectorTerminal, collectorPolicy, manifest, [evidence],
            null, start, start.AddSeconds(3));

        ImmutableDictionary<string, string> Labels(string role) =>
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(SandboxBuildTestLimits.PolicyOperationLabel, intent.OperationId.ToString("N"))
                .Add(SandboxBuildTestLimits.PolicyRoleLabel, role);
    }

    private static SandboxBuildTestOperationSnapshot RunningOperation(MafExecutionBuildTestIntent intent) =>
        Operation(intent) with
        {
            Status = SandboxBuildTestOperationStatus.Running,
            Terminal = null, CollectorPod = null, CollectorTerminal = null,
            CollectorManifestSha256 = null, OutputEvidence = []
        };

    private static HttpResponseMessage Response(HttpStatusCode status, object? payload)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (payload is not null)
            response.Content = JsonContent.Create(payload, payload.GetType(), options: JsonOptions);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
