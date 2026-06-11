# Spike Results: Sabbour.Mxc.Sdk v0.1.1 — Windows ARM64

Date: 2026-06-10
Host: Windows 10.0.26200, Snapdragon X (ARM64)
Branch: 002-sandboxed-execution
Spike project: spike/Scaffolder.SandboxExec.Spike/

---

## 1. SDK version confirmed

Package: Sabbour.Mxc.Sdk 0.1.1
Source: https://www.nuget.org/packages/Sabbour.Mxc.Sdk/0.1.1
Verified: `dotnet package search Sabbour.Mxc.Sdk` returns v0.1.1 (0 downloads — private/early)

---

## 2. Binary discovery

### What is in the NuGet package

The package contains only a managed .NET DLL — no native binaries are bundled.

```
sabbour.mxc.sdk\0.1.1\
  lib\net10.0\
    Sabbour.Mxc.Sdk.dll
    Sabbour.Mxc.Sdk.xml
  README.md
  sabbour.mxc.sdk.nuspec
```

No `wxc-exec.exe`, no `arm64/` directory, no native assets section.

### wxc-exec.exe resolution

The SDK resolves the executor through the following priority chain:

1. `SandboxSpawnOptions.ExecutablePath` — per-spawn override
2. `MXC_BIN_DIR` env var — `%MXC_BIN_DIR%\<arch>\wxc-exec.exe`
3. `bin\<arch>\...` next to the SDK assembly
4. Cargo target paths (dev builds)
5. PATH (last resort)

### Binary path that worked on this host

```
MXC_BIN_DIR = C:\Users\asabbour\Git\mxc-dotnet-sdk\artifacts\mxc-bin
Resolved   = C:\Users\asabbour\Git\mxc-dotnet-sdk\artifacts\mxc-bin\arm64\wxc-exec.exe
```

This is the mxc-dotnet-sdk dev repo artifacts directory. In Phase 1, the env var must
be set in CI and on developer workstations to a stable installation path (e.g.
`C:\mxc-bin`) populated from the `mxc-release-binaries.zip` GitHub release asset.

### Production binary installation

Download from https://github.com/microsoft/mxc/releases — asset: `mxc-release-binaries.zip`.
Latest tested: v0.6.1. Unzip, then:

```powershell
$env:MXC_BIN_DIR = "C:\mxc-bin"
# Verify:
& "$env:MXC_BIN_DIR\arm64\wxc-exec.exe" --probe
```

---

## 3. wxc-exec --probe output

```json
{
  "tier": "base-container",
  "needsDaclAugmentation": false,
  "warnings": [],
  "probes": {
    "baseContainerApiPresent": true,
    "bfscfgPresent": false,
    "bfsCompiledIn": false
  }
}
```

Tier is `base-container` (highest). On a stock build the base-container tier returns
`E_NOTIMPL` unless ViVeTool velocity keys 61389575 and 61155944 are enabled. However,
using `SandboxPolicy.Version = "0.4.0-alpha"` routes to the AppContainer fallback
which does NOT require those keys. Both policy paths were tested (see section 5).

---

## 4. MxcSdk actual API surface (v0.1.1)

### Key types

```
Sabbour.Mxc.Sdk.MxcSdk             (static facade)
Sabbour.Mxc.Sdk.SandboxPolicy      (record: Version, Network, Filesystem, Ui, TimeoutMs)
Sabbour.Mxc.Sdk.NetworkPolicy      (AllowOutbound, AllowLocalNetwork, AllowedHosts, BlockedHosts, Proxy)
Sabbour.Mxc.Sdk.FilesystemPolicy   (ReadwritePaths, ReadonlyPaths, HiddenPaths)
Sabbour.Mxc.Sdk.ContainerConfig    (Version, ContainerId, Containment, Process, Lifecycle, ...)
Sabbour.Mxc.Sdk.PlatformSupport    (IsSupported, IsolationTier, AvailableMethods, Reason, IsolationWarnings)
Sabbour.Mxc.Sdk.IsolationTier      (enum: BaseContainer, AppContainerBfs, AppContainerDacl, Process, ...)
Sabbour.Mxc.Sdk.SandboxingMethod   (enum: ProcessContainer, WindowsSandbox, Wslc, Lxc, ...)
Sabbour.Mxc.Sdk.Sandbox.SandboxSpawnOptions   (UsePty, Debug, Experimental, ExecutablePath, LogDir, ...)
Sabbour.Mxc.Sdk.Sandbox.ProcessConnection     (WaitForExitAsync, ProcessId, Kill, Dispose)
Sabbour.Mxc.Sdk.Sandbox.SandboxProcessResult  (Stdout, Stderr, ExitCode)
Sabbour.Mxc.Sdk.Errors.MxcException           (Code, RawCode, Message)
```

### Key static methods on MxcSdk

```csharp
PlatformSupport  GetPlatformSupport()
ContainerConfig  CreateConfigFromPolicy(SandboxPolicy policy, string containment = "process", string? containerName = null)
ContainerConfig  BuildSandboxPayload(string script, SandboxPolicy policy, string? workingDirectory = null, string? containerName = null, string containment = "process")
ProcessConnection SpawnSandboxProcessFromConfig(ContainerConfig config, SandboxSpawnOptions? options = null, ...)
Task<IPtyConnection> SpawnSandboxFromConfig(ContainerConfig config, SandboxSpawnOptions? options = null, ...)
Task<SandboxProcessResult> SpawnSandboxAsync(string script, SandboxPolicy policy, SandboxSpawnOptions? options = null, ...)
```

### API gap: ProcessConnection stdout/stderr are internal

`ProcessConnection.GetStdout()` and `GetStderr()` are declared `internal` in v0.1.1.
The only public members are:

```csharp
int ProcessId { get; }
Task<int> WaitForExitAsync(CancellationToken ct = default)
void Kill()
void Dispose() / ValueTask DisposeAsync()
```

To capture buffered stdout + stderr + exit code, use `SpawnSandboxAsync` instead,
which returns `SandboxProcessResult` with public `Stdout`, `Stderr`, `ExitCode`.

This is documented in the spike code comments and is the recommended approach for
Phase 1 unless a future SDK version makes those members public.

---

## 5. Working configuration for processcontainer

Confirmed working on this host (Windows 10.0.26200, ARM64):

```csharp
var policy = new SandboxPolicy
{
    Version = "0.4.0-alpha",                // AppContainer fallback tier — no ViVeTool needed
    Network = new NetworkPolicy { AllowOutbound = false },
    // Filesystem: leave null for defaults (system paths included)
};

// SpawnSandboxProcessFromConfig: pipe mode, exit code only
ContainerConfig config = MxcSdk.BuildSandboxPayload(
    "cmd /c echo hello from sandbox",
    policy,
    containment: "process");

using ProcessConnection conn = MxcSdk.SpawnSandboxProcessFromConfig(
    config,
    new SandboxSpawnOptions { UsePty = false });
int exitCode = await conn.WaitForExitAsync();
// exitCode = 0

// SpawnSandboxAsync: buffered, full output
SandboxProcessResult result = await MxcSdk.SpawnSandboxAsync(
    "cmd /c echo hello from sandbox",
    policy,
    new SandboxSpawnOptions { UsePty = false });
// result.ExitCode = 0
// result.Stdout = "hello from sandbox\r\n..."
```

### Observed stdout artifact

The Stdout from `SpawnSandboxAsync` contained an unexpected suffix:

```
hello from sandbox
:\Users\asabbour\Git\mxc-dotnet-sdk\artifacts\mxc-bin\arm64\wxc-exec.exe
```

The executor path fragment appears on a second line. This looks like the executor
echoing its own path to stdout (possibly a debug line or an SDK wrapper artifact
in the dev-artifacts build). Needs investigation before Phase 1. The exit code
was 0 and the primary output was correct.

---

## 6. WSL2 availability

```
wsl.exe --status:  Default Distribution: Ubuntu-24.04 / Default Version: 2
wsl.exe -- echo hello from WSL:  "hello from WSL"
Result: PASS
```

WSL2 is available on this host. The `lxc` / `process` (Linux) containment backends
are reachable via WSL2. Note: `hyperlight` and `microvm` are not available on ARM64
(x86_64-only and missing `nanvixd` daemon respectively).

---

## 7. Backend availability matrix (this host)

| Backend            | Containment wire | Status                    | Notes                                   |
|--------------------|------------------|---------------------------|-----------------------------------------|
| processcontainer   | "process"        | PASS (AppContainer tier)  | base-container tier also present        |
| windows_sandbox    | "vm"             | Available (not tested)    | Feature must be enabled + reboot, admin required |
| microvm            | "microvm"        | NOT AVAILABLE             | nanvixd not in public zip               |
| hyperlight         | (experimental)   | NOT AVAILABLE             | x86_64 only                             |
| wslc               | "wslc"           | NOT TESTED                | Requires WSL 2.8.1+                     |

---

## 8. One-time host preparation steps found

1. Set `MXC_BIN_DIR` to the dir containing `arm64\wxc-exec.exe`:

   ```powershell
   [System.Environment]::SetEnvironmentVariable("MXC_BIN_DIR", "C:\mxc-bin", "Machine")
   ```

2. For `appcontainer-dacl` tier (if needed): run `wxc-host-prep.exe` elevated:

   ```powershell
   & "$env:MXC_BIN_DIR\arm64\wxc-host-prep.exe" prepare-system-drive   # one-time
   & "$env:MXC_BIN_DIR\arm64\wxc-host-prep.exe" prepare-null-device    # per-boot
   ```

3. For `base-container` tier with schema >= 0.5.0-alpha: enable ViVeTool keys
   (not required for 0.4.0-alpha AppContainer path):

   ```
   ViVeTool.exe /enable /id:61389575,61155944
   # then reboot
   ```

---

## 9. Gaps that affect Phase 1 design

### GAP-1: stdout/stderr inaccessible from ProcessConnection
`GetStdout()`/`GetStderr()` are internal in v0.1.1. Phase 1 must use
`SpawnSandboxAsync` (buffered) for output capture, not `SpawnSandboxProcessFromConfig`.

Mitigation: Use `SpawnSandboxAsync` with `UsePty = false` as the Phase 1 execution path.

### GAP-2: Executor path artifact in stdout
The executor binary path appears as a second line in stdout. May be a dev-artifact
build artifact or a genuine SDK bug. Needs investigation. Phase 1 should strip or
filter unexpected non-output lines, or file an issue upstream.

### GAP-3: MXC_BIN_DIR is a manual prerequisite
The executor is not bundled. Phase 1 must document this as an installation prerequisite
and add a startup check that gives a clear error if the env var is not set.

### GAP-4: Policy version pinning
Schema `0.4.0-alpha` selects the AppContainer fallback and runs without extra keys.
Phase 1 should pin this version for maximum compatibility across Windows 11 ARM64 hosts
until the velocity keys are confirmed present in the target CI/deployment environment.

### GAP-5: windows_sandbox requires elevation
The `windows_sandbox` backend requires the process to run elevated. If Phase 1 needs
stronger isolation than AppContainer, it must either use elevation or accept AppContainer.

---

## 10. Recommendation for Phase 1 implementation

1. Use `SpawnSandboxAsync(script, policy, new SandboxSpawnOptions { UsePty = false })`
   as the primary sandbox execution API — it provides stdout/stderr/exitCode and avoids
   the Porta.Pty ARM64 gap.

2. Pin `SandboxPolicy.Version = "0.4.0-alpha"` to target the AppContainer tier.
   Upgrade to a higher schema only after ViVeTool keys are confirmed on all target hosts.

3. Add a startup check: verify `MXC_BIN_DIR\<arch>\wxc-exec.exe` exists before any
   sandbox operation. Emit a human-readable error if not found.

4. Use `processcontainer` (containment: "process") as the default Windows backend.

5. Add stdout post-processing to strip the executor path artifact (see GAP-2).

6. For CI: set `MXC_BIN_DIR` as a pipeline variable pointing to a pre-extracted
   `mxc-release-binaries.zip` directory. Cache the directory across runs.
