using System;
using System.Diagnostics;
using System.IO;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using FluentAssertions;
using Xunit;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// A run that has to install a system package needs somewhere to install it. The executor gives it a
/// per-run writable system root — /usr and /var overlaid onto a size-bounded tmpfs inside the run's
/// own user namespace — instead of the alternatives, all of which are unacceptable: writing to the
/// shared image, adding a capability to the pod, or running the command as real root.
///
/// These tests pin the two properties that make that safe: it is per-run, and every failure mode
/// leaves the command *more* restricted (read-only system root) rather than less.
/// </summary>
[Trait("Category", "ProcessEnvironment")]
public sealed class SandboxWritableSystemRootTests : IDisposable
{
    private readonly string _root =
        Path.Combine(AppContext.BaseDirectory, $"kata-rootfs-{Guid.NewGuid():N}");
    private readonly string _workspace;
    private readonly string _runA;

    public SandboxWritableSystemRootTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _runA = Path.Combine(_workspace, "worktrees", "run-a");
        Directory.CreateDirectory(_runA);

        // RegisterRuntimeHome refuses a HOME that is missing any authoritative XDG directory, so
        // the fixture has to create the same shape AgentHost provisions for a real run.
        CreateHome(Path.Combine(_root, "home", "run-a"));
    }

    /// <summary>Creates the same HOME shape AgentHost provisions for a real run.</summary>
    private static void CreateHome(string home)
    {
        Directory.CreateDirectory(Path.Combine(home, ".cache"));
        Directory.CreateDirectory(Path.Combine(home, ".local", "share"));
        Directory.CreateDirectory(Path.Combine(home, ".config"));
    }

    /// <summary>
    /// The helper is the only thing that can build the writable root, so when the image does not
    /// ship it the probe must say so by name — an operator reading the log needs to know exactly
    /// what is missing, not merely that a capability is off.
    /// </summary>
    [Fact]
    public void Probe_NamesTheMissingHelperInsteadOfFailingSilently()
    {
        if (File.Exists(SandboxCapabilityProbe.RunRootHelperPath))
            return;

        SandboxCapabilityProbe.ProbeWritableSystemRoot(out var detail).Should().BeFalse();
        detail.Should().NotBeNullOrWhiteSpace();
        detail.Should().Contain(
            OperatingSystem.IsLinux() ? SandboxCapabilityProbe.RunRootHelperPath : "Linux");
    }

    /// <summary>
    /// Without a writable root the command must still run — against the image's read-only system
    /// root, exactly as it did before this feature existed. A missing writable root is a reduced
    /// capability, never a reason to relax the sandbox or to fail the run.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void WithoutAWritableRoot_TheCommandStillRunsUnderBwrapWithAReadOnlySystemRoot()
    {
        if (!KataRuntimeGate.Available())
            return;
        if (SandboxCapabilityProbe.ProbeWritableSystemRoot(out _))
            return;

        using var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);

        var psi = executor.BuildProcessStartInfo(Command());

        psi.FileName.Should().Be("bwrap");
        psi.ArgumentList.Should().ContainInOrder("--ro-bind", "/usr", "/usr");
        psi.ArgumentList.Should().NotContain("--target");
    }

    /// <summary>
    /// The writable root must never silently widen what the sandbox can reach: the command still
    /// runs under bubblewrap with the same namespace flags and the same dropped capabilities, it
    /// only sees a private overlay where the read-only image used to be.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void WithAWritableRoot_TheCommandStillRunsUnderBwrapWithTheSameHardening()
    {
        if (!KataRuntimeGate.Available())
            return;
        if (!SandboxCapabilityProbe.ProbeWritableSystemRoot(out _))
            return;

        using var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);

        var psi = executor.BuildProcessStartInfo(Command());

        psi.FileName.Should().Be("nsenter");
        psi.ArgumentList.Should().ContainInConsecutiveOrder("--preserve-credentials", "--", "bwrap");
        psi.ArgumentList.Should().ContainInOrder("--cap-drop", "ALL");
        psi.ArgumentList.Should().Contain("--unshare-user");
        psi.ArgumentList.Should().Contain("--die-with-parent");
        psi.ArgumentList.Should().ContainInOrder("--bind", "/usr", "/usr");
        psi.ArgumentList.Should().NotContainInOrder("--ro-bind", "/usr", "/usr");
    }

    /// <summary>
    /// Nothing ever tells the executor a run has ended, and one sidecar process can outlive many
    /// runs' workspaces, so a holder for a workspace the platform already deleted must not go on
    /// occupying a slot forever — that would eventually starve a still-active run of a writable root
    /// while the capability contract keeps claiming apt-get is <c>Supported</c>. This pins the fix:
    /// once the cap is reached, a holder whose workspace directory no longer exists is reclaimed to
    /// make room, while a holder for a workspace that still exists is left alone.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void AHolderForADeletedWorkspace_IsReclaimedWhenTheCapIsReached()
    {
        if (!KataRuntimeGate.Available())
            return;
        if (!SandboxCapabilityProbe.ProbeWritableSystemRoot(out _))
            return;

        var runB = Path.Combine(_workspace, "worktrees", "run-b");
        var runC = Path.Combine(_workspace, "worktrees", "run-c");
        Directory.CreateDirectory(runB);
        Directory.CreateDirectory(runC);
        var homeB = Path.Combine(_root, "home", "run-b");
        var homeC = Path.Combine(_root, "home", "run-c");
        CreateHome(homeB);
        CreateHome(homeC);

        // Cap of 2 so this test only ever has to hold two real writable roots at once, rather than
        // needing to fill the production default (8) to prove the same reclaim path.
        using var executor = new KataBwrapExecutor(protectedRoots: [_workspace], maxWritableSystemRoots: 2);
        executor.RegisterTrustedWorkspace(_runA);
        executor.RegisterRuntimeHome(_runA, Path.Combine(_root, "home", "run-a"));
        executor.RegisterTrustedWorkspace(runB);
        executor.RegisterRuntimeHome(runB, homeB);
        executor.RegisterTrustedWorkspace(runC);
        executor.RegisterRuntimeHome(runC, homeC);

        // Fill both slots: run-a and run-b each get a real holder.
        executor.BuildProcessStartInfo(Command(_runA)).FileName.Should().Be("nsenter");
        executor.BuildProcessStartInfo(Command(runB)).FileName.Should().Be("nsenter");

        // The cap is full and every workspace still exists, so run-c must fall back to read-only
        // rather than evicting an active run's holder.
        var stillFull = executor.BuildProcessStartInfo(Command(runC));
        stillFull.FileName.Should().Be("bwrap");
        stillFull.ArgumentList.Should().ContainInOrder("--ro-bind", "/usr", "/usr");

        // The platform deletes run-a's workspace once that run is over.
        Directory.Delete(_runA, recursive: true);

        // run-c can now claim a slot: the stale run-a holder is reclaimed, and run-b's holder (whose
        // workspace still exists) is left untouched.
        var afterReclaim = executor.BuildProcessStartInfo(Command(runC));
        afterReclaim.FileName.Should().Be("nsenter");
        afterReclaim.ArgumentList.Should().ContainInOrder("--bind", "/usr", "/usr");

        var runBStillWritable = executor.BuildProcessStartInfo(Command(runB));
        runBStillWritable.FileName.Should().Be("nsenter");
    }

    /// <summary>
    /// An operator must be able to turn the writable root off without redeploying a different
    /// image; turning it off is always safe because it only removes a capability.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void TheWritableRootCanBeDisabledByConfiguration()
    {
        if (!KataRuntimeGate.Available())
            return;

        var previous = Environment.GetEnvironmentVariable("AGENTWEAVER_EXEC_WRITABLE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_EXEC_WRITABLE_ROOT", "0");
            using var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
            RegisterRun(executor);

            var psi = executor.BuildProcessStartInfo(Command());

            psi.FileName.Should().Be("bwrap");
            psi.ArgumentList.Should().ContainInOrder("--ro-bind", "/usr", "/usr");

            executor.DescribeCapabilities()
                .Should().ContainSingle(capability => capability.Id == SandboxCapabilityIds.AptInstall)
                .Which.State.Should().Be(SandboxCapabilityState.Unavailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_EXEC_WRITABLE_ROOT", previous);
        }
    }

    /// <summary>
    /// The contract the executor publishes must describe the executor as it is actually configured;
    /// a contract assembled from defaults would tell callers a capability exists when it does not.
    /// </summary>
    [Fact]
    public void TheExecutorPublishesItsOwnMeasuredCapabilities()
    {
        using var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);

        var capabilities = executor.DescribeCapabilities();

        capabilities.Should().Contain(capability => capability.Id == SandboxCapabilityIds.WingetInstall);
        capabilities.Should().Contain(capability => capability.Id == SandboxCapabilityIds.AptInstall);
        capabilities.Should().Contain(capability => capability.Id == SandboxCapabilityIds.ImageBuild);
    }

    private void RegisterRun(KataBwrapExecutor executor)
    {
        executor.RegisterTrustedWorkspace(_runA);
        executor.RegisterRuntimeHome(_runA, Path.Combine(_root, "home", "run-a"));
    }

    private SandboxCommand Command() => Command(_runA);

    private static SandboxCommand Command(string workingDirectory) =>
        new(
            "true",
            workingDirectory,
            null,
            new SandboxFsPolicy([workingDirectory], [], []),
            5000);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
