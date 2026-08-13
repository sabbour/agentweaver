using System.Diagnostics;
using System.Net.Sockets;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Executes the shipped <c>awx-docker</c> shim itself, rather than asserting against a re-implemented
/// copy of its logic. The shim's exporter refusals are ergonomics, not a security boundary — a run
/// can invoke <c>buildctl</c> directly, and what actually prevents publishing is that the builder
/// holds no registry credential. They still have to behave as documented, so they are proven on the
/// artifact that actually ships in the image.
///
/// The shim reaches the builder over a unix socket, and refuses to do anything when that socket is
/// absent. These tests therefore bind a real listening socket to get past the fail-closed preflight,
/// then assert the exporter policy — no BuildKit daemon is needed, because every refusal happens
/// before the shim ever execs <c>buildctl</c>.
/// </summary>
[Trait("Category", KataRuntimeGate.Category)]
public sealed class AwxDockerShimTests
{
    private const string SocketVariable = "AGENTWEAVER_IMAGE_BUILD_SOCKET";

    /// <summary>
    /// Walks up from the test binary to the shim that ships in the AgentHost image. A missing shim
    /// fails the test instead of returning early: a silent skip here would mean the exporter policy
    /// is never actually proven.
    /// </summary>
    private static string ShimPath()
    {
        const string relative = "apps/Agentweaver.AgentHost/sandbox/awx-docker";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"could not locate '{relative}' above '{AppContext.BaseDirectory}'; the shim must be " +
            "present for the exporter-refusal proofs to mean anything");
    }

    private sealed record ShimResult(int ExitCode, string Stdout, string Stderr);

    private static ShimResult RunShim(string? socketPath, params string[] arguments)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ShimPath());
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        // Passed on the child only, so parallel tests never race over a process-wide variable.
        psi.Environment[SocketVariable] = socketPath ?? "/nonexistent/awx-docker-absent.sock";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000).Should().BeTrue("the shim must not hang");
        return new ShimResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>Binds a real listening unix socket so the fail-closed preflight is satisfied.</summary>
    private static Socket Listen(string socketPath)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        return listener;
    }

    private static string NewContext()
    {
        var directory = Directory.CreateTempSubdirectory("awx-docker-ctx-").FullName;
        File.WriteAllText(Path.Combine(directory, "Dockerfile"), "FROM scratch\n");
        return directory;
    }

    [SidecarLinuxFact]
    public void Shim_FailsClosedWhenNoBuilderSocketIsPresent()
    {
        var context = NewContext();
        try
        {
            var result = RunShim(socketPath: null, "build", context);

            result.ExitCode.Should().Be(3, "no builder must fail closed, never fall back to something weaker");
            result.Stderr.Should().Contain("no build daemon socket");
        }
        finally
        {
            Directory.Delete(context, recursive: true);
        }
    }

    /// <summary>
    /// A path that exists but is not a socket (an empty <c>emptyDir</c> where the sidecar was never
    /// patched in) must be treated exactly like an absent builder.
    /// </summary>
    [SidecarLinuxFact]
    public void Shim_FailsClosedWhenTheSocketPathIsNotASocket()
    {
        var directory = Directory.CreateTempSubdirectory("awx-docker-sock-").FullName;
        var context = NewContext();
        var notASocket = Path.Combine(directory, "buildkitd.sock");
        File.WriteAllText(notASocket, string.Empty);
        try
        {
            var result = RunShim(notASocket, "build", context);

            result.ExitCode.Should().Be(3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(context, recursive: true);
        }
    }

    [SidecarLinuxFact]
    public void Shim_RefusesEveryPublishingExporterEvenWithABuilderPresent()
    {
        var directory = Directory.CreateTempSubdirectory("awx-docker-sock-").FullName;
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        var context = NewContext();
        using var listener = Listen(socketPath);
        try
        {
            // --push is the obvious one; the --output spellings are the ways a caller could reach
            // a registry exporter without typing --push. The last three are CSV-parsing evasions:
            // buildctl reads this value as CSV, so a quoted field or a trailing `type=` in some
            // other key's value defeats a substring scan while buildctl still sees type=image
            // with push=true.
            string[][] publishingInvocations =
            [
                ["build", "--push", "-t", "example.com/app:1", context],
                ["build", "--output", "type=image,push=true", context],
                ["build", "--output", "type=registry,name=example.com/app:1", context],
                ["build", "-o", "type=image,name=example.com/app:1,push=1", context],
                ["build", "--output", "type=image,name=example.com/app:1,\"push=true\"", context],
                ["build", "--output", "type=image,name=example.com/app:1,annotation=type=oci", context],
                ["build", "--output", "type=oci,dest=/tmp/x.tar,PUSH=true", context],
            ];

            foreach (var invocation in publishingInvocations)
            {
                var result = RunShim(socketPath, invocation);
                result.ExitCode.Should().Be(
                    2,
                    "'{0}' publishes from the sandbox and must be refused",
                    string.Join(' ', invocation));
            }
        }
        finally
        {
            listener.Dispose();
            Directory.Delete(directory, recursive: true);
            Directory.Delete(context, recursive: true);
        }
    }

    /// <summary>
    /// The refusal must be a policy on the exporter, not a blanket refusal of <c>--output</c>: the
    /// local exporters a run legitimately needs still have to get through to <c>buildctl</c>.
    /// </summary>
    [SidecarLinuxFact]
    public void Shim_AcceptsLocalExportersAndOnlyThenReachesTheBuilder()
    {
        var directory = Directory.CreateTempSubdirectory("awx-docker-sock-").FullName;
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        var context = NewContext();
        using var listener = Listen(socketPath);
        try
        {
            foreach (var exporter in new[] { "type=oci,dest=out.tar", "type=docker,dest=out.tar", "type=local,dest=out" })
            {
                var result = RunShim(socketPath, "build", "--output", exporter, context);

                // It never completes a build here (there is no daemon behind the socket), but it must
                // get past the policy: exit 2 would mean a permitted exporter was wrongly refused.
                result.ExitCode.Should().NotBe(2, "'{0}' writes into the run's own filesystem", exporter);
            }

            // Docker accepts `--progress plain` as well as `--progress=plain`. The separated form
            // used to fall through to the positional branch and be reported as a second build
            // context, which a live build against a real builder caught.
            foreach (var progress in new[] { "--progress=plain", "--progress" })
            {
                var invocation = progress == "--progress"
                    ? new[] { "build", progress, "plain", "--output", "type=oci,dest=out.tar", context }
                    : ["build", progress, "--output", "type=oci,dest=out.tar", context];

                RunShim(socketPath, invocation).ExitCode.Should().NotBe(
                    2, "'{0}' is a progress selector, not a second build context", progress);
            }
        }
        finally
        {
            listener.Dispose();
            Directory.Delete(directory, recursive: true);
            Directory.Delete(context, recursive: true);
        }
    }
}
