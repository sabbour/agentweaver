---
"agentweaver": patch
---

Enforce the per-run filesystem policy at the Kata command boundary (security, #476):

- **Cross-run workspace escape**: every Kata AgentHost pod mounts the *shared* RWX
  `/workspace` PVC, and the Kata-mode `PassthroughExecutor` previously ignored the per-run
  filesystem policy entirely. A prompt-injected command could keep its declared working
  directory inside its own tree yet read/write a sibling project via an absolute path
  (`cat /workspace/<other-project>/secrets`, `git -C /workspace/<other-project> …`).
- **New guard**: `SharedWorkspacePathGuard` scans a command's *text* for absolute paths
  that resolve under a protected shared-mount root (default `/workspace`, override via
  `AGENTWEAVER_PROTECTED_SHARED_ROOTS`) but outside the run's own allowed roots, and rejects
  them before the shell starts. It is wired into both `ShellCommandValidator` (the
  `run_command` tool) and `PassthroughExecutor` (the executor boundary, consuming
  `SandboxCommand.FilesystemPolicy`), collapsing `.`/`..` traversal and handling quoting,
  `--flag=` assignment, and colon path-lists.

This is defense-in-depth, not a substitute for true per-run volume isolation (the shared
RWX PVC follow-up remains tracked architectural work); a command-text filter cannot catch
every obfuscation, but it closes the direct cross-project read/write path described in #476.
