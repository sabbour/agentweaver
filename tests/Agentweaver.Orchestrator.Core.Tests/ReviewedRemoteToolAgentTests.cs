using System.Runtime.InteropServices;
using System.Text;
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

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"other","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"changed"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x","query":"x"}}}""")]
    [InlineData("not-json")]
    public async Task ChangedSerializedCallIsRejectedBeforeLateAuthorityTransportOrResultStorage(string wire)
    {
        var rig = CreateRig();
        rig.Transport.SerializedCall = Encoding.UTF8.GetBytes(wire);

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal("runtime_remote_tool_call_serialization_invalid", failure.Code);
        Assert.Equal(1, rig.Transport.SerializeCount);
        Assert.Equal(1, rig.Authority.VerifyCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
        Assert.False(rig.Results.WriteStarted.Task.IsCompleted);
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
    public async Task TransportReceivesOriginalContextAndExactFullProof()
    {
        var rig = CreateRig();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer original-actor";
        var invoke = rig.Agent.InvokeAsync(context, Invocation(), CancellationToken.None);
        var write = await rig.Results.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(context, rig.Transport.Context);
        Assert.Equal(write.Proof, rig.Transport.Proof);
        SessionMaterialValidation.ValidateRemoteToolResultProof(rig.Transport.Proof!);
        Assert.Equal(ReviewedRemoteToolAgent.SnapshotBinding(rig.Transport.Snapshot!), write.Proof.SnapshotBinding);
        Assert.Equal(rig.Transport.SerializedCall, rig.Transport.SentBytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(rig.Transport.SentBytes, out var sent));
        Assert.NotSame(rig.Transport.SerializedCall, sent.Array);
        Assert.Equal("native-call-7", rig.Transport.Proof!.NativeCallId);
        Assert.Equal("actor-1", rig.Transport.Proof.ActorId);
        Assert.Equal("""{"query":"x"}""", rig.Transport.Proof.CanonicalArgumentsJson);

        rig.Results.Acknowledgment.TrySetResult(CreateAcknowledgment(write.Proof, write.Bytes));
        await invoke.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuthorityChangeDuringSerializationDeniesTransportHandoff()
    {
        var rig = CreateRig();
        rig.Transport.OnSerialize = () => rig.Authority.RegistrationChanged = true;

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal("runtime_remote_tool_registration_changed", failure.Code);
        Assert.Equal(1, rig.Transport.SerializeCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Null(rig.Transport.Proof);
        Assert.Equal(0, rig.Results.WriteCount);
    }

    [Fact]
    public async Task SerializerBufferChangesDuringAuthorityCheckDoNotChangeTheValidatedPrivateCopy()
    {
        var rig = CreateRig();
        var expectedBytes = rig.Transport.SerializedCall.ToArray();
        rig.Authority.OnVerify = () =>
        {
            if (rig.Authority.VerifyCount == 2)
                Array.Fill(rig.Transport.SerializedCall, (byte)'!');
        };

        var invoke = rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None);
        var write = await rig.Results.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expectedBytes, rig.Transport.SentBytes.ToArray());
        Assert.All(rig.Transport.SerializedCall, value => Assert.Equal((byte)'!', value));
        rig.Results.Acknowledgment.TrySetResult(CreateAcknowledgment(write.Proof, write.Bytes));
        await invoke.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingForcedNetworkProofDoesNotRetryOrStoreAResult()
    {
        var rig = CreateRig();
        rig.Transport.SendFailure = new RuntimeAuthorizationException(
            "remote_mcp_forced_network_enforcement_unavailable");

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal("remote_mcp_forced_network_enforcement_unavailable", failure.Code);
        Assert.Equal(1, rig.Transport.SerializeCount);
        Assert.Equal(1, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
        Assert.False(rig.Results.WriteStarted.Task.IsCompleted);
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

    [Fact]
    public async Task MatchingSavedResultReplaysWithoutSerializingSendingOrWriting()
    {
        var rig = CreateRig();
        rig.Results.SavedResult = proof =>
        {
            var recorded = proof with { ContentClass = SessionMaterialResultContentClass.Mixed };
            var bytes = "saved-result"u8.ToArray();
            return new(CreateAcknowledgment(recorded, bytes), bytes);
        };

        var result = await rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None);

        Assert.Equal("saved-result"u8.ToArray(), result.Content.ToArray());
        Assert.Equal(SessionMaterialResultContentClass.Mixed, result.ContentClass);
        Assert.Equal(1, rig.Results.ReadCount);
        Assert.Equal(0, rig.Transport.SerializeCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
        Assert.Equal(2, rig.Authority.VerifyCount);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("length")]
    [InlineData("digest")]
    [InlineData("event")]
    [InlineData("position")]
    [InlineData("session")]
    [InlineData("runtime")]
    [InlineData("revision")]
    [InlineData("fence")]
    [InlineData("selection")]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("arguments")]
    [InlineData("snapshot")]
    [InlineData("permission")]
    [InlineData("content-class")]
    public async Task SavedResultRejectsChangedBytesOrBindingBeforeExternalDispatch(string changed)
    {
        var rig = CreateRig();
        rig.Results.SavedResult = proof =>
        {
            var bytes = "saved-result"u8.ToArray();
            var acknowledgment = CreateAcknowledgment(proof, bytes);
            var material = acknowledgment.Reference.Material!;
            acknowledgment = changed switch
            {
                "event" => acknowledgment with { EventId = Guid.NewGuid() },
                "position" => acknowledgment with { Position = 0 },
                "session" => acknowledgment with { Identity = new("project-1", "run-1", "other-session") },
                _ => acknowledgment
            };
            material = changed switch
            {
                "digest" => material with { Sha256 = new string('f', 64) },
                "runtime" => material with { RuntimeInstanceId = Guid.NewGuid() },
                "revision" => material with { RegistrationRevision = material.RegistrationRevision + 1 },
                "fence" => material with { ExecutionFence = material.ExecutionFence + 1 },
                "selection" => material with { AcceptedSelectionHash = new string('f', 64) },
                "tenant" => material with { TenantId = "other-tenant" },
                "actor" => material with { RemoteToolResultProof = proof with { ActorId = "other-actor" } },
                "arguments" => material with
                {
                    RemoteToolResultProof = proof with { CanonicalArgumentsJson = """{"query":"other"}""" }
                },
                "snapshot" => material with
                {
                    RemoteToolResultProof = proof with
                    {
                        SnapshotBinding = proof.SnapshotBinding with { ConnectionRowRevision = 99 }
                    }
                },
                "permission" => material with
                {
                    RemoteToolResultProof = proof with { ActionId = "tool.write" }
                },
                "content-class" => material with
                {
                    RemoteToolResultProof = proof with { ContentClass = (SessionMaterialResultContentClass)99 }
                },
                _ => material
            };
            acknowledgment = acknowledgment with
            {
                Reference = acknowledgment.Reference with
                {
                    Material = material,
                    ByteLength = changed == "length" ? bytes.Length + 1 : bytes.Length
                }
            };
            return new(acknowledgment, changed == "bytes" ? "changed"u8.ToArray() : bytes);
        };

        if (changed == "content-class")
            await Assert.ThrowsAsync<ArgumentException>(() =>
                rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None));
        else
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal(0, rig.Transport.SerializeCount);
        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityChangeDuringSavedReadDeniesReplayOrNewDispatch(bool saved)
    {
        var rig = CreateRig();
        rig.Results.SavedResult = proof =>
        {
            rig.Authority.RegistrationChanged = true;
            var bytes = "saved-result"u8.ToArray();
            return saved ? new(CreateAcknowledgment(proof, bytes), bytes) : null;
        };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None));

        Assert.Equal(0, rig.Transport.SendCount);
        Assert.Equal(0, rig.Results.WriteCount);
    }

    [Fact]
    public async Task AuthorityChangeDuringAcknowledgmentDeniesReturningNewResult()
    {
        var rig = CreateRig();
        var invoke = rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None);
        var write = await rig.Results.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Authority.RegistrationChanged = true;
        rig.Results.Acknowledgment.TrySetResult(CreateAcknowledgment(write.Proof, write.Bytes));

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => invoke.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(3, 1, 0)]
    [InlineData(4, 1, 1)]
    public async Task CurrentNativeGrantIsRequiredAfterWaitsBeforeDispatchWriteAndReturn(
        int failAtVerification, int expectedSends, int expectedWrites)
    {
        var rig = CreateRig();
        rig.Authority.FailAtVerification = failAtVerification;
        var invoke = rig.Agent.InvokeAsync(new DefaultHttpContext(), Invocation(), CancellationToken.None);
        if (expectedWrites != 0)
        {
            var write = await rig.Results.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rig.Results.Acknowledgment.TrySetResult(CreateAcknowledgment(write.Proof, write.Bytes));
        }

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            invoke.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("runtime_action_denied", failure.Code);
        Assert.Equal(expectedSends, rig.Transport.SendCount);
        Assert.Equal(expectedWrites, rig.Results.WriteCount);
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
        public Action? OnVerify { get; set; }
        public bool RegistrationChanged { get; set; }
        public int? FailAtVerification { get; set; }

        public Task<RuntimeRegistration> ReadCurrentRegistrationAsync(
            HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken) =>
            Task.FromResult(RegistrationChanged ? registration with { Revision = registration.Revision + 1 } : registration);

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
            OnVerify?.Invoke();
            if (VerifyCount == FailAtVerification)
                return Task.FromException<RuntimeActionAdmission>(new RuntimeAuthorizationException("runtime_action_denied"));
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
        public byte[] SerializedCall { get; set; } =
            """{"jsonrpc":"2.0","id":"rpc-local-1","method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}"""u8.ToArray();
        public int SerializeCount { get; private set; }
        public int SendCount { get; private set; }
        public HttpContext? Context { get; private set; }
        public RemoteToolResultProof? Proof { get; private set; }
        public ReviewedRemoteToolSnapshot? Snapshot { get; private set; }
        public ReadOnlyMemory<byte> SentBytes { get; private set; }
        public Action? OnSerialize { get; set; }
        public RuntimeAuthorizationException? SendFailure { get; set; }

        public byte[] SerializeCall(ReviewedRemoteToolSnapshot snapshot, ReviewedRemoteToolCall call)
        {
            SerializeCount++;
            OnSerialize?.Invoke();
            return SerializedCall;
        }

        public Task<ReviewedRemoteToolTransportResult> SendAsync(
            HttpContext context,
            RemoteToolResultProof proof,
            ReviewedRemoteToolSnapshot snapshot,
            ReadOnlyMemory<byte> serializedCall,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Context = context;
            Proof = proof;
            Snapshot = snapshot;
            SentBytes = serializedCall;
            if (SendFailure is { } failure)
                return Task.FromException<ReviewedRemoteToolTransportResult>(failure);
            return Task.FromResult(new ReviewedRemoteToolTransportResult(
                "result"u8.ToArray(),
                SessionMaterialResultContentClass.TextOnly));
        }
    }

    private sealed class TestResultWriter : IReviewedRemoteToolResultWriter
    {
        public int WriteCount { get; private set; }
        public int ReadCount { get; private set; }
        public Func<RemoteToolResultProof, SessionMaterialReadResult?>? SavedResult { get; set; }
        public TaskCompletionSource<(RemoteToolResultProof Proof, byte[] Bytes)> WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SessionMaterialAcknowledgment> Acknowledgment { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionMaterialAcknowledgment> WriteAndAcknowledgeAsync(
            HttpContext context,
            RemoteToolResultProof proof,
            ReadOnlyMemory<byte> resultBytes,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            var bytes = resultBytes.ToArray();
            WriteStarted.TrySetResult((proof, bytes));
            return await Acknowledgment.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<SessionMaterialReadResult?> ReadOptionalAsync(
            HttpContext context, RemoteToolResultProof proof, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(SavedResult?.Invoke(proof));
        }
    }

    private sealed record Rig(
        ReviewedRemoteToolAgent Agent,
        TestAuthority Authority,
        TestTransport Transport,
        TestResultWriter Results);
}
