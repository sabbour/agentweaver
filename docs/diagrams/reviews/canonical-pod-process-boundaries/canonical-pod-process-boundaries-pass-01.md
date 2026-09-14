# canonical-pod-process-boundaries: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6736 -> 25852 (3.837886x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Flat cards do not depict two container/PID boundaries and the narrower child mount view.
- A mount-view card has a spawn arrow, incorrectly turning a boundary into the spawning process.
- Pod symbols are used for containers and an IPC volume; the Kata boundary is an isolated cloud card.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `k8s/base/sandbox-template-agenthost.yaml:58-61,151-162,269-292,341-399; packages/Agentweaver.SandboxExec/PodExec/PodExecServer.cs:212-242; packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs:609-655,892-931; tests/Agentweaver.Tests/KataBwrapExecutorTests.cs:29-47,110-164`.
