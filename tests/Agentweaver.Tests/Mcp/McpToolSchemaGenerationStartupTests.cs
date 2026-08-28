using System.Diagnostics;
using System.Text;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Regression guard for the MCP startup crash that has now landed TWICE on the same landmine:
/// <list type="bullet">
/// <item>Commit 7605b692 reverted a <c>JsonElement?</c> parameter default on <c>project_create</c>
/// after it crashed the server at boot.</item>
/// <item>PR #419 re-added the <c>= null</c> default (correctly, to keep the parameter optional per
/// #418/#347) but that re-introduced the exact same startup crash, because
/// <c>Microsoft.Extensions.AI</c>'s reflection-based schema exporter
/// (<c>AIJsonUtilities.CreateFunctionJsonSchema</c>, invoked from
/// <c>AIFunctionFactory.ReflectionAIFunctionDescriptor</c>'s constructor) cannot serialize the
/// default/uninitialized state of a <c>Nullable&lt;JsonElement&gt;</c> parameter
/// (<c>JsonValueKind.Undefined</c>) into the tool's JSON input schema.
/// </list>
///
/// WHY THIS TEST SPAWNS A REAL PROCESS INSTEAD OF HOSTING IN-PROCESS: this bug is a
/// <c>Microsoft.Extensions.AI</c> VERSION-specific behavior. <c>apps/Agentweaver.Mcp.csproj</c>
/// resolves <c>Microsoft.Extensions.AI.Abstractions 9.6.0</c> (transitively via
/// <c>ModelContextProtocol.Core 0.3.0-preview.2</c>), which has the bug. But
/// <c>Agentweaver.Tests.csproj</c> references several OTHER projects that pull in
/// <c>Microsoft.Extensions.AI 10.6.0</c>, and NuGet's version-unification picks the single highest
/// version for the whole test binary's dependency graph - so any test that hosts the MCP server
/// in-process inside <c>Agentweaver.Tests</c> silently runs against 10.6.0, not 9.6.0, and can NEVER
/// reproduce this crash no matter how faithfully it replicates <c>Program.cs</c>'s DI registrations
/// (verified empirically: an in-process <c>WebApplication</c> host built with the exact same
/// <c>AddMcpServer().WithToolsFromAssembly()</c>/<c>MapMcp</c> calls as production did NOT crash for
/// the buggy <c>JsonElement? blueprint = null</c> parameter, while
/// <c>dotnet run --project apps/Agentweaver.Mcp</c> crashed reliably every time).
///
/// So this test instead launches the ACTUAL compiled <c>Agentweaver.Mcp.dll</c> (found via its own
/// build output directory, not the test project's shadow copy) as a child process - the exact same
/// <c>Program.Main</c> entry point, with its own <c>deps.json</c>-resolved 9.6.0 dependency - bound to
/// an OS-assigned free port, and asserts it does not print an unhandled exception during startup.
/// Production runs Agentweaver.Mcp in HTTP mode (no <c>--stdio</c>), and <c>MapMcp</c> requires
/// <c>WithHttpTransport()</c> to have been registered, so this test must run the same way (not
/// <c>--stdio</c>) to faithfully exercise the exact crashing call.
/// </summary>
public sealed class McpToolSchemaGenerationStartupTests
{
    [Fact]
    public async Task RealMcpProcess_Startup_DoesNotThrowUnhandledException()
    {
        var mcpDllPath = FindMcpAssemblyPath();
        File.Exists(mcpDllPath).Should().BeTrue($"expected the built Agentweaver.Mcp.dll at {mcpDllPath}");

        var psi = new ProcessStartInfo("dotnet", $"\"{mcpDllPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Production runs Agentweaver.Mcp in HTTP mode (not --stdio) - MapMcp requires
        // WithHttpTransport() to have been registered, which Program.cs only does when --stdio is
        // absent. Development environment skips the Auth:Mcp:Issuer/Audience Production-only check
        // in Program.cs, and port 0 lets the OS pick a free port so this test never collides with a
        // real running instance.
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputLock = new object();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaptureOutput(StringBuilder destination, string? line)
        {
            if (line is null)
                return;

            lock (outputLock)
            {
                destination.AppendLine(line);
            }

            if (line.Contains("Now listening", StringComparison.OrdinalIgnoreCase))
                listening.TrySetResult();
        }

        process.OutputDataReceived += (_, e) => CaptureOutput(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => CaptureOutput(stderr, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // The schema exporter completes before Kestrel reports readiness. Waiting for that
            // concrete signal verifies real startup without charging every successful run six seconds.
            var exited = process.WaitForExitAsync();
            var completed = await Task.WhenAny(listening.Task, exited, Task.Delay(TimeSpan.FromSeconds(6)));
            string combinedOutput;
            lock (outputLock)
            {
                combinedOutput = stdout.ToString() + stderr.ToString();
            }

            combinedOutput.Should().NotContain("Unhandled exception",
                because: $"the MCP server must not crash at startup (regression of 7605b692 / #419). Output:\n{combinedOutput}");

            completed.Should().BeSameAs(listening.Task,
                because: $"the MCP server must report readiness within six seconds. Output:\n{combinedOutput}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Locates the real (non-test-shadow-copied) Agentweaver.Mcp.dll build output, so this test runs
    /// against the SAME dependency resolution (deps.json) that production uses - not the version-
    /// unified copy that ends up in Agentweaver.Tests' own bin folder.
    /// </summary>
    private static string FindMcpAssemblyPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // AppContext.BaseDirectory is .../tests/Agentweaver.Tests/bin/<Config>/<Tfm>/ - walk up to
        // find the repo root, identified by the presence of apps/Agentweaver.Mcp/Agentweaver.Mcp.csproj.
        var tfmName = dir.Name; // e.g. net10.0
        var buildConfig = dir.Parent?.Name ?? "Debug"; // e.g. Debug/Release

        DirectoryInfo? repoRoot = dir;
        while (repoRoot is not null &&
               !File.Exists(Path.Combine(repoRoot.FullName, "apps", "Agentweaver.Mcp", "Agentweaver.Mcp.csproj")))
        {
            repoRoot = repoRoot.Parent;
        }

        repoRoot.Should().NotBeNull("could not locate repo root containing apps/Agentweaver.Mcp/Agentweaver.Mcp.csproj by walking up from the test output directory");

        var mcpBinRoot = Path.Combine(repoRoot!.FullName, "apps", "Agentweaver.Mcp", "bin");
        var candidate = Path.Combine(mcpBinRoot, buildConfig, tfmName, "Agentweaver.Mcp.dll");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Fall back to searching for any built Agentweaver.Mcp.dll under the bin folder (covers
        // Configuration/TFM mismatches between the two projects' most recent local builds).
        var found = Directory.Exists(mcpBinRoot)
            ? Directory.GetFiles(mcpBinRoot, "Agentweaver.Mcp.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        found.Should().NotBeNull(
            $"expected a built Agentweaver.Mcp.dll under {mcpBinRoot} (run `dotnet build apps/Agentweaver.Mcp` first)");
        return found!;
    }
}
