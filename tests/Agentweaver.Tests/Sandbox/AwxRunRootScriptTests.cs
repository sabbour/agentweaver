using System.Diagnostics;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Executes the shipped <c>awx-run-root</c> script itself, rather than asserting against a
/// re-implemented copy of its logic — the same convention <see cref="AwxDockerShimTests"/> uses for
/// <c>awx-docker</c>.
///
/// A real Kata run's <c>/etc</c> cannot be an overlay lowerdir (the container runtime bind-mounts
/// resolv.conf/hosts/hostname into it and the kernel then refuses the overlay), so the script copies
/// it onto the writable tmpfs instead. A copy that fails partway through must never be treated as
/// success: binding a half-populated <c>/etc</c> over the real one would leave a run silently
/// executing against a broken system config while the executor believes the writable root came up
/// cleanly. These tests force that copy to fail and assert the script refuses to reach its
/// <c>READY</c> handshake, matching every other mount step in the script.
/// </summary>
[Trait("Category", KataRuntimeGate.Category)]
public sealed class AwxRunRootScriptTests : IDisposable
{
    private readonly string _binDir =
        Path.Combine(AppContext.BaseDirectory, $"awx-run-root-fakebin-{Guid.NewGuid():N}");

    /// <summary>
    /// Walks up from the test binary to the script that ships in the AgentHost image. A missing
    /// script fails the test instead of returning early: a silent skip here would mean the fail-loud
    /// contract is never actually proven.
    /// </summary>
    private static string ScriptPath()
    {
        const string relative = "apps/Agentweaver.AgentHost/sandbox/awx-run-root";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"could not locate '{relative}' above '{AppContext.BaseDirectory}'; the script must be " +
            "present for the fail-loud proof to mean anything");
    }

    /// <summary>
    /// A <c>cp</c> that always fails, placed ahead of the real one on <c>PATH</c>. The script invokes
    /// <c>cp</c> unqualified, so this deterministically fails exactly the <c>/etc</c> copy step
    /// without depending on tmpfs sizing or on the size of whatever <c>/etc</c> happens to contain on
    /// the machine running the test — both of which would make a size-based reproduction flaky.
    /// </summary>
    private string CreateFailingCpOnPath()
    {
        Directory.CreateDirectory(_binDir);
        var fakeCp = Path.Combine(_binDir, "cp");
        File.WriteAllText(fakeCp, "#!/bin/sh\necho \"fake cp: forced failure\" >&2\nexit 1\n");
        RunShell($"chmod +x '{fakeCp}'");
        return _binDir;
    }

    private static void RunShell(string command)
    {
        using var chmod = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", command },
            UseShellExecute = false,
        })!;
        chmod.WaitForExit();
    }

    [Fact]
    public async Task AFailedEtcCopy_NeverReachesReadyAndFailsLoudly()
    {
        if (!KataRuntimeGate.Available())
            return;
        // The environment must actually support the writable-root feature (unprivileged user
        // namespaces + tmpfs-backed overlay mounts) before this test can mean anything: without
        // that, the script would already fail earlier, at one of the overlay mounts, and this test
        // would be asserting on the wrong failure message rather than proving the /etc-copy fix.
        if (!Agentweaver.SandboxExec.SandboxCapabilityProbe.ProbeWritableSystemRoot(out _))
            return;

        var fakeBinDir = CreateFailingCpOnPath();
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ScriptPath());
        psi.ArgumentList.Add("hold");
        psi.ArgumentList.Add("64m");
        // Prepend the fake `cp` so the script's unqualified `cp -a` resolves to it instead of the
        // real coreutils binary; keep the rest of PATH so `unshare`/`mount`/`mkdir` still resolve.
        psi.Environment["PATH"] = fakeBinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "awx-run-root did not exit after its /etc copy was forced to fail; it should fail " +
                "loudly rather than block on the READY handshake.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0, "a failed /etc copy must not look like a successful start");
        stdout.Should().NotContain(
            "READY", "the handshake must never fire once /etc could not be fully populated");
        stderr.Should().Contain(
            "awx-run-root: /etc copy failed",
            "the failure must be attributed to the copy step specifically, not swallowed silently");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_binDir))
                Directory.Delete(_binDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
