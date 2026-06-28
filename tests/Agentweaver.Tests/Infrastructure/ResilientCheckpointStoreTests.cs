using FluentAssertions;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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

    // =========================================================================
    // (a) Permission-denied / IOException on the SHARED store is the ONE real
    //     recurring exception flooding API + worker logs at startup. It is NOT
    //     corruption: Create must fall back WITHOUT throwing, WITHOUT quarantining,
    //     and emit at most ONE warn line (no fail, no stacktrace).
    // =========================================================================
    [Fact]
    public void Create_WhenSharedStoreAccessDenied_FallsBackQuietly_WithSingleWarn_AndNoFail()
    {
        Directory.CreateDirectory(_dir);
        var logger = new CapturingLogger();
        var sharedFull = new DirectoryInfo(_dir).FullName;

        // Simulate the live failure: the shared-volume store ctor throws permission-denied (as the MAF
        // ctor does), while a per-pod sub-directory under the same volume is openable.
        FileSystemJsonCheckpointStore Opener(DirectoryInfo di) =>
            di.FullName == sharedFull
                ? throw new UnauthorizedAccessException(
                    $"Access to the path '{Path.Combine(_dir, "index.jsonl")}' is denied.",
                    new IOException("Permission denied"))
                : new FileSystemJsonCheckpointStore(di);

        object? store = null;
        var act = () => store = ResilientCheckpointStore.Create(_dir, logger, Opener);

        act.Should().NotThrow();
        store.Should().NotBeNull();
        (store as IDisposable)?.Dispose();

        // Permission denial is not corruption: the shared index must NOT be quarantined.
        Directory.GetFiles(_dir, "index.jsonl.corrupt.*").Should().BeEmpty();

        // No fail-level logging and no stacktrace-bearing warn — exactly ONE concise warn for the fallback.
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(1);
        warnings[0].Message.Should().Contain("permission denied")
            .And.Contain("per-pod directory");
        warnings[0].Exception.Should().BeNull();
    }

    // =========================================================================
    // (b) The QuarantineIndex "already exists" bug: the old fixed
    //     .corrupt.<unixSeconds> name collided on rapid restarts within the same
    //     second and threw IOException. Two back-to-back quarantines must NOT throw
    //     and must produce two distinct quarantine files.
    // =========================================================================
    [Fact]
    public void QuarantineIndex_CalledTwiceInSameSecond_DoesNotThrow_AndKeepsBothCopies()
    {
        Directory.CreateDirectory(_dir);
        var indexPath = Path.Combine(_dir, "index.jsonl");
        var logger = new CapturingLogger();

        File.WriteAllText(indexPath, "first corrupt index");
        var act1 = () => ResilientCheckpointStore.QuarantineIndex(indexPath, logger);
        act1.Should().NotThrow();

        File.WriteAllText(indexPath, "second corrupt index");
        var act2 = () => ResilientCheckpointStore.QuarantineIndex(indexPath, logger);
        act2.Should().NotThrow();

        Directory.GetFiles(_dir, "index.jsonl.corrupt.*").Should().HaveCount(2);
        // No fail log about a failed quarantine.
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    // =========================================================================
    // (c) Genuine index corruption (a real parse failure in the ctor, NOT a
    //     permission/lock error) must STILL quarantine the index and log clearly,
    //     then start fresh on the shared dir.
    // =========================================================================
    [Fact]
    public void Create_WhenIndexGenuinelyCorrupt_QuarantinesAndLogsError_ThenStartsFresh()
    {
        Directory.CreateDirectory(_dir);
        var indexPath = Path.Combine(_dir, "index.jsonl");
        // A line that is valid JSON (survives SanitizeIndex) but the ctor rejects semantically.
        File.WriteAllText(indexPath, "{\"checkpointId\":\"bogus\"}");
        var logger = new CapturingLogger();

        var calls = 0;
        FileSystemJsonCheckpointStore Opener(DirectoryInfo di)
        {
            calls++;
            if (calls == 1)
            {
                // Mirror MAF's "Index corrupted" failure: an InvalidOperationException whose inner cause
                // is a JsonException — i.e. NOT permission/lock, so the corruption path must handle it.
                throw new InvalidOperationException(
                    $"Could not load store {di.FullName}. Index corrupted.",
                    new JsonException("Unexpected token parsing checkpoint index."));
            }
            return new FileSystemJsonCheckpointStore(di);
        }

        object? store = null;
        var act = () => store = ResilientCheckpointStore.Create(_dir, logger, Opener);

        act.Should().NotThrow();
        store.Should().NotBeNull();
        (store as IDisposable)?.Dispose();

        // Genuine corruption IS quarantined and IS logged loudly (error), unlike the quiet perms path.
        Directory.GetFiles(_dir, "index.jsonl.corrupt.*").Should().ContainSingle();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("unrecoverable"));
    }

    /// <summary>Captures emitted log entries (level, formatted message, exception) for assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
