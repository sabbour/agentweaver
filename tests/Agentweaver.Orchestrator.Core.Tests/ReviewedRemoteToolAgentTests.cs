using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ReviewedRemoteToolAgentTests
{
    [Fact]
    public async Task AuthorizationDenialDoesNotSerializeOrSendTheRemoteCall()
    {
        var rig = CreateRig();
        rig.Authority.AuthorizationFailure = new RuntimeAuthorizationException("runtime_action_denied");

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => rig.Agent.InvokeAsync(
            new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal(0, rig.Transport.SerializeCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
    }

    [Fact]
    public async Task OversizedSerializedCallIsRejectedBeforeTransportOrResultStorage()
    {
        var rig = CreateRig();
        rig.Transport.SerializedCall = new byte[SessionMaterialValidation.MaximumBytes + 1];

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => rig.Agent.InvokeAsync(
            new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal(1, rig.Transport.SerializeCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
    }

    [Fact]
    public async Task NodeNotPresentInAcceptedSnapshotIsRejectedBeforeAuthorization()
    {
        var rig = CreateRig();
        var invocation = Invocation() with { NodeId = "other-node" };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => rig.Agent.InvokeAsync(
            new DefaultHttpContext(), invocation, CancellationToken.None));

        Assert.Null(rig.Authority.AuthorizedRequest);
        Assert.Equal(0, rig.Transport.SerializeCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
    }

    [Fact]
    public async Task ResultIsNotReturnedUntilMatchingDurableMaterialAcknowledgmentArrives()
    {
        var rig = CreateRig();
        var invoke = rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None);
        var write = await rig.Results.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(invoke.IsCompleted);
        Assert.Equal(1, rig.Transport.SendCount);
        Assert.Equal(SessionMaterialResultContentClass.TextOnly, write.Proof.ContentClass);

        rig.Results.Acknowledgment.TrySetResult(CreateAcknowledgment(write.Proof, write.Bytes));
        var result = await invoke.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(write.Proof.CallEventId, result.CallEventId);
        Assert.Equal(SessionMaterialValidation.RemoteToolResultEventId(write.Proof), result.ResultEventId);
        Assert.Equal(write.Proof.CallInputHash, result.InputHash);
        Assert.Equal(write.Bytes, result.Content.ToArray());
        Assert.Equal("tool.read", rig.Authority.AuthorizedRequest?.ActionId);
        Assert.True(rig.Authority.AuthorizedRequest?.IsToolInvocation);
        Assert.Equal(write.Proof.CallEventId, rig.Authority.AuthorizedRequest?.EventId);
        Assert.Equal(write.Proof.CallInputHash, rig.Authority.AuthorizedRequest?.InputHash);
        Assert.Equal(1, rig.Results.WriteCount);
        Assert.True(rig.Authority.VerifyCount >= 2);
    }

    [Fact]
    public async Task MismatchedResultAcknowledgmentIsRejected()
    {
        var rig = CreateRig();
        var invoke = rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None);
        var write = await rig.Results.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acknowledgment = CreateAcknowledgment(write.Proof, write.Bytes) with { EventId = Guid.NewGuid() };
        rig.Results.Acknowledgment.TrySetResult(acknowledgment);

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            invoke.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static Rig CreateRig()
    {
        var snapshot = CreateSnapshot();
        var selectionJson = JsonSerializer.Serialize(new
        {
            projectConfiguration = new
            {
                reviewedRemoteToolSnapshots = new[] { snapshot.Reference }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(selectionJson);
        var selection = new EffectiveRunSelection(
            "project-1", "run-1", 2, 3, 4, "context-5", document.RootElement.Clone());
        var acceptedHash = CoordinationOwnerStore.HashSelection(selection).ToLowerInvariant();
        var binding = new RuntimeBinding(
            "https://identity.test/",
            "actor-1",
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            "agent-1",
            "turn-1",
            2,
            3,
            4,
            "context-5",
            acceptedHash,
            7,
            "environment-1",
            "placement-1",
            1,
            "profile-1",
            new Uri("https://environment.test/configure"),
            new Uri("https://environment.test/observe"))
        {
            WorkflowStepId = "step-1"
        };
        var registration = new RuntimeRegistration(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            5,
            binding,
            RuntimeRegistrationState.Active,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var authority = new TestAuthority(
            registration,
            new AuthorizedRunSelection(selection, null!));
        var transport = new TestTransport();
        var results = new TestResultWriter();
        var agent = new ReviewedRemoteToolAgent(
            authority,
            new TestSnapshotResolver(snapshot),
            transport,
            results);
        return new(agent, authority, transport, results);
    }

    private static ReviewedRemoteToolInvocation Invocation() =>
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "agent-1",
            "node-1",
            "native-call-7",
            "remote.lookup",
            """{"query":"x"}""");

    private static ReviewedRemoteToolSnapshot CreateSnapshot() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "project-1",
            "agent-1",
            "node-1",
            "connection-1",
            6,
            4,
            8,
            "Enabled",
            new string('a', 64),
            "https://mcp.example.test/api",
            "https://mcp.example.test/resource",
            "DelegatedOAuth",
            null,
            "StreamableHttp20250618",
            null,
            "catalog-r8",
            new string('b', 64),
            "remote.lookup",
            "tool-r2",
            new string('c', 64),
            "schema-r3",
            new string('d', 64),
            """{"description":"Lookup","name":"remote.lookup"}""",
            """{"properties":{"query":{"type":"string"}},"type":"object"}""",
            new ReviewedRemoteToolPermissionMetadata(
                "tool.read",
                "runtime.execution",
                """{"source":"explicit-review","version":1}"""));

    private static SessionMaterialAcknowledgment CreateAcknowledgment(
        RemoteToolResultProof proof, byte[] bytes)
    {
        var material = new SessionMaterialBinding(
            1,
            SessionMaterialKind.RemoteToolResult,
            proof.TenantId,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            proof.RuntimeInstanceId,
            proof.RegistrationRevision,
            proof.ExecutionFence,
            proof.AcceptedSelectionHash,
            "sdk-1",
            "runtime-1",
            "model-selection-1",
            "model-1")
        {
            RemoteToolResultProof = proof
        };
        var reference = new SessionObjectReference(
            new ObjectKey("session/material/result"),
            SessionMaterialValidation.Purpose(SessionMaterialKind.RemoteToolResult),
            bytes.Length)
        {
            Material = material
        };
        return new(
            1,
            new SessionIdentity(proof.ProjectId, proof.RunId, proof.SessionId),
            SessionMaterialValidation.RemoteToolResultEventId(proof),
            1,
            reference);
    }

    private sealed class TestAuthority(
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted) : IReviewedRemoteToolExecutionAuthority
    {
        public Exception? AuthorizationFailure { get; set; }
        public RuntimeActionRequest? AuthorizedRequest { get; private set; }
        public int VerifyCount { get; private set; }

        public Task<RuntimeRegistration> ReadCurrentRegistrationAsync(
            HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken) =>
            Task.FromResult(registration);

        public Task<AuthorizedRunSelection> ReadAcceptedSelectionWithAuthorityAsync(
            HttpContext context, string projectId, string runId, CancellationToken cancellationToken) =>
            Task.FromResult(accepted);

        public Task<RuntimeActionAdmission> AuthorizeAsync(
            HttpContext context, RuntimeActionRequest request, CancellationToken cancellationToken)
        {
            if (AuthorizationFailure is { } failure)
                return Task.FromException<RuntimeActionAdmission>(failure);
            AuthorizedRequest = request;
            return Task.FromResult(new RuntimeActionAdmission(
                1,
                request,
                request.EventId.ToString("N"),
                "1",
                request.EventId,
                PolicyEvaluationOutcome.Allow,
                PolicyEvaluationReasonCode.Allowed));
        }

        public Task<RuntimeActionAdmission> VerifyAsync(
            HttpContext context, RuntimeActionAdmission admission, CancellationToken cancellationToken)
        {
            VerifyCount++;
            return Task.FromResult(admission);
        }
    }

    private sealed class TestSnapshotResolver(ReviewedRemoteToolSnapshot snapshot)
        : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference, CancellationToken cancellationToken) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(snapshot);
    }

    private sealed class TestTransport : IReviewedRemoteToolTransport
    {
        public byte[] SerializedCall { get; set; } = [1, 2, 3];
        public int SerializeCount { get; private set; }
        public int SendCount { get; private set; }

        public byte[] SerializeCall(ReviewedRemoteToolSnapshot snapshot, ReviewedRemoteToolCall call)
        {
            SerializeCount++;
            return SerializedCall;
        }

        public Task<ReviewedRemoteToolTransportResult> SendAsync(
            ReviewedRemoteToolSnapshot snapshot,
            ReadOnlyMemory<byte> serializedCall,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new ReviewedRemoteToolTransportResult(
                "result"u8.ToArray(),
                SessionMaterialResultContentClass.TextOnly));
        }
    }

    private sealed class TestResultWriter : IReviewedRemoteToolResultWriter
    {
        public int WriteCount { get; private set; }
        public TaskCompletionSource<(RemoteToolResultProof Proof, byte[] Bytes)> WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SessionMaterialAcknowledgment> Acknowledgment { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionMaterialAcknowledgment> WriteAndAcknowledgeAsync(
            RemoteToolResultProof proof,
            ReadOnlyMemory<byte> resultBytes,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            var bytes = resultBytes.ToArray();
            WriteStarted.TrySetResult((proof, bytes));
            return await Acknowledgment.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record Rig(
        ReviewedRemoteToolAgent Agent,
        TestAuthority Authority,
        TestTransport Transport,
        TestResultWriter Results);
}
