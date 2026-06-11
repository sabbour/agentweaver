// Phase 0 spike: validate Sabbour.Mxc.Sdk v0.1.1 on Windows ARM64.
//
// Goals:
//   T001: Confirm NuGet package contents (managed DLL only — no bundled binary)
//   T002: Exercise MxcSdk.GetPlatformSupport(), CreateConfigFromPolicy(),
//         SpawnSandboxProcessFromConfig() (exit code), SpawnSandboxAsync() (full output)
//   T003: WSL2 smoke test via wsl.exe
//   T004: Results documented in specs/002-sandboxed-execution/spike-results.md
//
// Required env var:
//   MXC_BIN_DIR  — root dir containing arm64\wxc-exec.exe (or x64\wxc-exec.exe)
//   Example: $env:MXC_BIN_DIR = "C:\mxc-bin"
//
// Run:
//   dotnet run --project spike\Scaffolder.SandboxExec.Spike -c Release

using System.Diagnostics;
using Sabbour.Mxc.Sdk;
using Sabbour.Mxc.Sdk.Sandbox;

Console.WriteLine("=== Scaffolder.SandboxExec.Spike — Sabbour.Mxc.Sdk v0.1.1 ===");
Console.WriteLine($"DateTime: {DateTime.UtcNow:O}");
Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"MXC_BIN_DIR: {Environment.GetEnvironmentVariable("MXC_BIN_DIR") ?? "(not set)"}");
Console.WriteLine();

// -----------------------------------------------------------------------
// T001 / T002 — Platform probe
// -----------------------------------------------------------------------

Console.WriteLine("--- 1. Platform support probe ---");

// GetPlatformSupport() invokes wxc-exec.exe --probe under the hood.
// Returns PlatformSupport: IsSupported, IsolationTier, AvailableMethods,
//         IsolationWarnings, Reason, UiCapabilities.
PlatformSupport support;
try
{
    support = MxcSdk.GetPlatformSupport();
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL  GetPlatformSupport threw: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("      Ensure MXC_BIN_DIR points to the dir containing arm64\\wxc-exec.exe");
    PrintSummary(platformOk: false, processContainerOk: false, wsl2Ok: false);
    return;
}

Console.WriteLine($"  IsSupported     : {support.IsSupported}");
Console.WriteLine($"  IsolationTier   : {support.IsolationTier}");
Console.WriteLine($"  AvailableMethods: {string.Join(", ", support.AvailableMethods ?? [])}");
Console.WriteLine($"  Reason          : {support.Reason ?? "(none)"}");
if (support.IsolationWarnings is { Count: > 0 } warnings)
    Console.WriteLine($"  Warnings        : {string.Join("; ", warnings)}");
Console.WriteLine();

if (!support.IsSupported)
{
    Console.WriteLine("Platform is not supported on this host. Skipping spawn tests.");
    PrintSummary(platformOk: false, processContainerOk: false, wsl2Ok: false);
    return;
}

// -----------------------------------------------------------------------
// T002 — Sandbox spawn: processcontainer / 0.4.0-alpha
// -----------------------------------------------------------------------
// Policy pinned to 0.4.0-alpha: this schema routes to the AppContainer tier
// (appcontainer-dacl or appcontainer-bfs) which runs without the ViVeTool
// velocity keys required by base-container (schema >= 0.5.0-alpha).
// Network outbound disabled; no filesystem overrides (system paths included
// in the default AppContainer policy).

Console.WriteLine("--- 2a. SpawnSandboxProcessFromConfig (pipe mode, exit-code only) ---");

// NOTE: In Sabbour.Mxc.Sdk v0.1.1 ProcessConnection.GetStdout() and
// GetStderr() are declared internal, so stdout/stderr are not accessible
// via this API path. Use SpawnSandboxAsync (2b) for buffered output.
// This call demonstrates the pipe-mode API and captures the exit code.

bool processContainerOk = false;
var policy = new SandboxPolicy
{
    Version = "0.4.0-alpha",
    Network = new NetworkPolicy { AllowOutbound = false },
};

// BuildSandboxPayload: sets commandLine + containment on the ContainerConfig.
ContainerConfig config = MxcSdk.BuildSandboxPayload(
    script: "cmd /c echo hello from sandbox",
    policy: policy,
    containment: "process");

try
{
    // UsePty = false: avoids the Porta.Pty win-arm64 gap (no PTY allocation).
    using ProcessConnection conn = MxcSdk.SpawnSandboxProcessFromConfig(
        config,
        new SandboxSpawnOptions { UsePty = false });

    int exitCode = await conn.WaitForExitAsync();
    Console.WriteLine($"  ProcessId : {conn.ProcessId}");
    Console.WriteLine($"  ExitCode  : {exitCode}");
    Console.WriteLine($"  Note      : stdout/stderr not accessible — GetStdout()/GetStderr() are internal in v0.1.1");
    processContainerOk = exitCode == 0;
    Console.WriteLine($"  Result    : {(processContainerOk ? "PASS" : "FAIL (non-zero exit)")}");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL  {ex.GetType().Name}: {ex.Message}");
}
Console.WriteLine();

Console.WriteLine("--- 2b. SpawnSandboxAsync (buffered one-shot, full output) ---");

// SpawnSandboxAsync returns SandboxProcessResult with public Stdout, Stderr, ExitCode.
// UsePty = false ensures separate stdout/stderr streams on ARM64.
bool spawnAsyncOk = false;
try
{
    var opts = new SandboxSpawnOptions { UsePty = false };
    SandboxProcessResult result = await MxcSdk.SpawnSandboxAsync(
        script: "cmd /c echo hello from sandbox",
        policy: policy,
        options: opts);

    Console.WriteLine($"  ExitCode  : {result.ExitCode}");
    Console.WriteLine($"  Stdout    : {result.Stdout?.Trim()}");
    Console.WriteLine($"  Stderr    : {result.Stderr?.Trim()}");
    spawnAsyncOk = result.ExitCode == 0 && result.Stdout?.Contains("hello from sandbox") == true;
    Console.WriteLine($"  Result    : {(spawnAsyncOk ? "PASS" : "FAIL")}");

    // Combine the two results for the overall processcontainer verdict
    processContainerOk = processContainerOk || spawnAsyncOk;
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL  {ex.GetType().Name}: {ex.Message}");
}
Console.WriteLine();

// -----------------------------------------------------------------------
// T003 — WSL2 smoke test (best-effort, does not fail the spike)
// -----------------------------------------------------------------------

Console.WriteLine("--- 3. WSL2 smoke test ---");
bool wsl2Ok = false;

try
{
    // Step 1: Check wsl.exe availability and status
    string wslStatus = RunProcess("wsl.exe", "--status", timeoutMs: 10_000);
    Console.WriteLine("  wsl.exe --status:");
    foreach (string line in wslStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        Console.WriteLine($"    {line}");

    // Step 2: Invoke a simple command inside WSL
    string wslEcho = RunProcess("wsl.exe", "-- echo hello from WSL", timeoutMs: 15_000);
    Console.WriteLine($"  wsl.exe -- echo: {wslEcho.Trim()}");
    wsl2Ok = wslEcho.Contains("hello from WSL");
    Console.WriteLine($"  Result: {(wsl2Ok ? "PASS" : "FAIL (unexpected output)")}");
}
catch (Exception ex)
{
    Console.WriteLine($"  WSL2 unavailable or timed out: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("  WSL2 result: SKIP (best-effort, spike continues)");
}
Console.WriteLine();

// -----------------------------------------------------------------------
// Summary
// -----------------------------------------------------------------------

PrintSummary(
    platformOk: support.IsSupported,
    processContainerOk: processContainerOk,
    wsl2Ok: wsl2Ok);

// -----------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------

static string RunProcess(string fileName, string arguments, int timeoutMs = 10_000)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(timeoutMs);
    return stdout + stderr;
}

static void PrintSummary(bool platformOk, bool processContainerOk, bool wsl2Ok)
{
    Console.WriteLine("=== Spike summary ===");
    Console.WriteLine($"  SDK version          : Sabbour.Mxc.Sdk 0.1.1");
    Console.WriteLine($"  Binary bundled       : NO — external wxc-exec.exe required");
    Console.WriteLine($"  Binary path (used)   : %MXC_BIN_DIR%\\<arch>\\wxc-exec.exe");
    Console.WriteLine($"  GetPlatformSupport   : {(platformOk ? "PASS" : "FAIL")}");
    Console.WriteLine($"  processcontainer run : {(processContainerOk ? "PASS" : "FAIL")}");
    Console.WriteLine($"  WSL2 available       : {(wsl2Ok ? "YES" : "NO/SKIP")}");
    Console.WriteLine($"  Overall              : {(platformOk && processContainerOk ? "PASS" : "PARTIAL/FAIL")}");
}
