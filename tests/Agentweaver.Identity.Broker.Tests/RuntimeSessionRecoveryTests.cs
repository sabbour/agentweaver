using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeSessionRecoveryTests
{
    [Fact]
    public void JournalPageDeserializesTheReadOnlyEnvelopeAndTypedTurnPayload()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var identity = new SessionIdentity("project", "run", "session");
        var eventId = Guid.NewGuid();
        var content = new SessionObjectReference(new ObjectKey("recorded-turn"), "turn-content", 17);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            events = new[]
            {
                new
                {
                    schemaVersion = 1, eventVersion = 2, eventId, identity, position = 7,
                    occurredAt = DateTimeOffset.UtcNow, kind = SessionEventKind.Turn,
                    payload = (SessionEventPayload)new TurnSessionPayload("user", content),
                    objectReferences = Array.Empty<StoredSessionObjectReference>()
                }
            },
            nextCursor = (string?)null, hasMore = false
        }, options);

        var page = JsonSerializer.Deserialize<SessionEventPage>(bytes, options);

        Assert.NotNull(page);
        var recorded = Assert.Single(page.Events);
        Assert.Equal(identity, recorded.Identity);
        Assert.Equal(eventId, recorded.EventId);
        Assert.Equal(7, recorded.Position);
        Assert.Equal(SessionEventKind.Turn, recorded.Kind);
        Assert.Equal(new TurnSessionPayload("user", content), recorded.Payload);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task ActualSdkFilesystemCallbacksCaptureOpaqueFilesAndResumeTheSameLogicalSession()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] bytes;
        SdkSessionFacts facts;
        await using (var external = new ControlledCopilotRuntime())
        {
            await using var session = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
                registration, registration.Binding.ModelSelectionReference!,
                RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token);
            var write = await external.InvokeNativeFilesAsync(
                "sessionFs.writeFile", "state/events.jsonl", "opaque native events\n", timeout.Token);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, write.ValueKind);
            facts = session.Facts;
            bytes = await session.CaptureNativeCacheAsync(timeout.Token);
            using var archive = new ZipArchive(new MemoryStream(bytes));
            using var native = new StreamReader(archive.GetEntry("state/events.jsonl")!.Open());
            Assert.Equal("opaque native events\n", await native.ReadToEndAsync(timeout.Token));
        }

        await using var replacement = new ControlledCopilotRuntime();
        await using var resumed = await RuntimeCopilotSessionTests.Factory(replacement).CreateAsync(
            registration with { RuntimeInstanceId = Guid.NewGuid() }, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token,
            Recovery(registration, facts, bytes));
        Assert.Equal(RuntimeSessionRecoveryMode.NativeCache, resumed.RecoveryMode);
        Assert.Equal(facts.SdkSessionId, resumed.Facts.SdkSessionId);
        Assert.Null(resumed.RecoveryReason);
        Assert.Contains(replacement.Requests, request => request.Method == "session.resume");
        Assert.DoesNotContain(replacement.Requests, request => request.Method == "session.create");
        var read = await replacement.InvokeNativeFilesAsync(
            "sessionFs.readFile", "state/events.jsonl", null, timeout.Token);
        Assert.Equal("opaque native events\n", read.GetProperty("content").GetString());
        Assert.Equal(bytes, await resumed.CaptureNativeCacheAsync(timeout.Token));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("sdk")]
    [InlineData("runtime")]
    [InlineData("model")]
    [InlineData("selection")]
    [InlineData("corrupt")]
    public async Task LostOrIncompatibleCacheRebuildsNativeContextWithoutReplayingATurn(string change)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime();
        SdkSessionFacts facts;
        await using (var first = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token))
            facts = first.Facts;
        var recovery = Recovery(registration, facts, NativeArchive("state/events.jsonl", "opaque events"));
        if (change == "missing")
            recovery = recovery with { Cache = null };
        else if (change == "corrupt")
            recovery = recovery with { Cache = Cache(registration, facts, [1, 2, 3]) };
        else
        {
            var material = recovery.Cache!.Material;
            var binding = material.Reference.Material!;
            binding = change switch
            {
                "sdk" => binding with { SdkVersion = "other-sdk" },
                "runtime" => binding with { RuntimeVersion = "other-runtime" },
                "model" => binding with { ModelId = "other-model" },
                "selection" => binding with { AcceptedSelectionHash = new string('b', 64) },
                _ => throw new InvalidOperationException()
            };
            recovery = recovery with
            {
                Cache = recovery.Cache with
                {
                    Material = material with { Reference = material.Reference with { Material = binding } }
                }
            };
        }
        await using var replacement = new ControlledCopilotRuntime();
        await using var rebuilt = await RuntimeCopilotSessionTests.Factory(replacement).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token, recovery);
        Assert.Equal(RuntimeSessionRecoveryMode.JournalRebuild, rebuilt.RecoveryMode);
        Assert.NotNull(rebuilt.RecoveryReason);
        var create = Assert.Single(replacement.Requests, request => request.Method == "session.create");
        Assert.Contains("previous user request", create.Parameters.GetProperty("systemMessage").GetProperty("content").GetString());
        Assert.Contains("previous answer", create.Parameters.GetProperty("systemMessage").GetProperty("content").GetString());
        Assert.DoesNotContain(replacement.Requests, request => request.Method is "session.resume" or "session.send");
    }

    [Fact]
    public async Task NativeCredentialWriteIsRejectedBeforeEnteringTheOpaqueCache()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime();
        await using var session = await RuntimeCopilotSessionTests.Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        var rejection = await external.InvokeNativeFilesAsync(
            "sessionFs.writeFile", "state/config.json", external.SdkCredential, timeout.Token);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, rejection.ValueKind);
        Assert.DoesNotContain(external.SdkCredential, rejection.ToString());
        Assert.Equal("runtime_sdk_cache_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                session.CaptureNativeCacheAsync(timeout.Token))).Code);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("state/../outside")]
    [InlineData("state\\outside")]
    public void NativeArchiveCannotIntroduceNonCanonicalPaths(string path)
    {
        var files = new RuntimeNativeSessionFiles("protected-credential");
        Assert.Equal("runtime_sdk_cache_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                files.Restore(NativeArchive(path, "content"))).Code);
    }

    private static RuntimeSessionRecovery Recovery(
        RuntimeRegistration registration, SdkSessionFacts facts, byte[] bytes) =>
        new(new(registration.Binding.ProjectId, registration.Binding.RunId, registration.Binding.SessionId),
            registration.Binding.TenantId, Cache(registration, facts, bytes),
            [("user", "previous user request"), ("assistant", "previous answer")]);

    private static SessionMaterialReadResult Cache(
        RuntimeRegistration registration, SdkSessionFacts facts, byte[] bytes) =>
        new(new(1, new(registration.Binding.ProjectId, registration.Binding.RunId, registration.Binding.SessionId),
            Guid.NewGuid(), 3,
            new(new ObjectKey("recorded-native-cache"), "runtime-sdk-cache", bytes.Length)
            {
                Material = new(1, SessionMaterialKind.SdkCache, registration.Binding.TenantId,
                    RuntimeContractValidation.Hash(bytes), registration.RuntimeInstanceId,
                    registration.Revision, registration.Binding.ExecutionFence,
                    registration.Binding.AcceptedSelectionHash, facts.SdkVersion, facts.RuntimeVersion,
                    facts.ModelSelectionReference, facts.ModelId)
            }), bytes);

    private static byte[] NativeArchive(string path, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(path, CompressionLevel.NoCompression).Open(),
                   new UTF8Encoding(false)))
            writer.Write(content);
        return buffer.ToArray();
    }
}
