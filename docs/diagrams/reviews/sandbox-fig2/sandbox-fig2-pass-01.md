# sandbox-fig2: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6694 -> 27413 (4.09516x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Windows/Linux selection and unavailable branches share a rail and cross the Windows card.
- This routing suggests a Windows-to-Linux chain despite the footer correctly saying they are alternatives.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs:89-109,162-172; packages/Agentweaver.SandboxExec/SandboxExecutorFactory.cs:20-71; packages/Agentweaver.SandboxExec/WslMxcSandboxExecutor.cs:28-34`.
