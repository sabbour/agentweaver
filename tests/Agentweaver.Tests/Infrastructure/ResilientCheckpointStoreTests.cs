using FluentAssertions;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Infrastructure;

/// <summary>
/// Guards the startup-durability fix: MAF parses the checkpoint <c>index.jsonl</c> one JSON object
/// per line, so a single blank or partially-written line (e.g. from an interrupted append) otherwise
/// throws in the store constructor and bricks the entire API at startup. <see cref="ResilientCheckpointStore"/>
/// must sanitize the index (or quarantine it) so construction always succeeds.
/// </summary>
public sealed class ResilientCheckpointStoreTests : IDisposable
{
    private readonly string _dir;

    public ResilientCheckpointStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"resilient-cp-{Guid.NewGuid():N}");
    }

    [Fact]
    public void Create_OnFreshDirectory_Succeeds()
    {
        var store = ResilientCheckpointStore.Create(_dir, NullLogger.Instance);
        store.Should().NotBeNull();
    }

    [Fact]
    public void Create_OnBlankAndCorruptIndex_DoesNotThrow_AndLeavesNoBlankLines()
    {
        Directory.CreateDirectory(_dir);
        var indexPath = Path.Combine(_dir, "index.jsonl");
        // The exact corruption that crashed startup: blank/whitespace lines, plus a non-JSON partial-write line.
        File.WriteAllLines(indexPath, new[]
        {
            "",
            "   ",
            "{ this is not valid json",
        });

        object? store = null;
        var act = () => store = ResilientCheckpointStore.Create(_dir, NullLogger.Instance);
        act.Should().NotThrow();
        // The store keeps a handle on the index, so release it before inspecting the file on disk.
        (store as IDisposable)?.Dispose();

        // Recovery happened one way or another: either the index was repaired in place (no blank
        // lines remain) or it was quarantined (moved aside) so the store could start fresh.
        if (File.Exists(indexPath))
        {
            File.ReadAllLines(indexPath).Should().OnlyContain(l => !string.IsNullOrWhiteSpace(l));
        }
        else
        {
            Directory.GetFiles(_dir, "index.jsonl.corrupt.*").Should().NotBeEmpty();
        }
        // The original (pre-repair) index was preserved as a backup before any rewrite.
        Directory.GetFiles(_dir, "index.jsonl.bak.*").Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WhenSharedDirLockedByAnotherProcess_FallsBackToPerPodDir_AndDoesNotThrow()
    {
        // Reproduce the live CrashLoop (exit 139): under API replicas:2 on a shared RWX volume the
        // FileSystemJsonCheckpointStore ctor takes an EXCLUSIVE process lock, so the second pod's
        // ctor throws "already in use by another process". Create must NOT treat this as corruption
        // (no quarantine), must NOT throw, and must hand back a usable per-pod store so the API boots.
        Directory.CreateDirectory(_dir);
        using var lockHolder = new FileSystemJsonCheckpointStore(new DirectoryInfo(_dir));

        var podName = $"api-pod-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable("POD_NAME", podName);
        try
        {
            object? store = null;
            var act = () => store = ResilientCheckpointStore.Create(_dir, NullLogger.Instance);

            act.Should().NotThrow();
            store.Should().NotBeNull();

            // This replica got its own writable per-pod store under the shared volume, keyed by POD_NAME.
            var perPodDir = Path.Combine(_dir, "replicas", podName);
            Directory.Exists(perPodDir).Should().BeTrue();

            // The shared index must NOT have been quarantined — lock contention is not corruption.
            Directory.GetFiles(_dir, "index.jsonl.corrupt.*").Should().BeEmpty();

            (store as IDisposable)?.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", null);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
