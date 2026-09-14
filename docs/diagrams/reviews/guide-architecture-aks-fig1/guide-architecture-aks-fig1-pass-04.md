# guide-architecture-aks-fig1: pass 04

Mode: correction-only. Reviewer/model: GPT-6 Astra (`gpt-6-astra`).
Actual inspected evidence: `guide-architecture-aks-fig1-pass-04.png` enlarged and
`guide-architecture-aks-fig1-pass-04-print.png` at approximate A5 print scale.
The images were opened, not inferred solely from XML.

No semantic or layout expansion. Re-exported unchanged pass-3 XML and traced all nine arrows. The marigold next-turn loop is distinct from the gray failure/end cleanup rail; every head and label is visible.

Observed remaining issue clusters: orientation/in-page 0,
overlap/fitting 0, arrows 0.
These are human inspection findings, not an automated overlap-score claim.
Passes 2-4 preserve semantic node/edge identity; only visibility, fit, labels and
endpoint/routing defects are corrected. No expansion to meet a later growth target.

Draw.io SHA256: `66b61eae72b9d38beb233d68083e83861e5ecd6d80c1c849a308cfba821e03a2`.
PNG SHA256: `5b283ad10407890f4d9078ad5ebaa72c5151bec1cbef1b945bfec1fa5a1a6ef7`.

## Complete arrow trace

- **l1** `claim -> reachable`: Rightward bound claim to reachable listener; head clear of bound label. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303
- **l2** `reachable -> configure`: Rightward reachability to configure; HTTP success is not setup readiness. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303
- **l3** `configure -> workspace`: Downward accepted configure to workspace; one-shot gate stays consumed. Evidence: apps/Agentweaver.AgentHost/Program.cs:258-350,377-407; AgentHostRuntimeState.cs:160-173; AgentHostStartupService.cs:124-245
- **l4** `workspace -> setup`: Leftward workspace preparation to setup; label below the rail. Evidence: apps/Agentweaver.AgentHost/Program.cs:258-350,377-407; AgentHostRuntimeState.cs:160-173; AgentHostStartupService.cs:124-245
- **l5** `setup -> ready`: Leftward finished setup to ready; head points to ready, not the reverse. Evidence: apps/Agentweaver.AgentHost/Program.cs:258-350,377-407; AgentHostRuntimeState.cs:160-173; AgentHostStartupService.cs:124-245
- **l6** `ready -> turn`: Downward ready to turn; dispatch label is in the gutter, title moved aside. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303; apps/Agentweaver.AgentHost/Program.cs:258-350,377-407; AgentHostRuntimeState.cs:160-173; AgentHostStartupService.cs:124-245
- **l7** `turn -> retain`: Rightward successful Assistant turn to retention; not every run retains. Evidence: apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:185-208
- **l8** `retain -> turn`: Dashed marigold bottom return from retention to turn; enters at 85%, separate from cleanup. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303
- **l9** `turn -> release`: Gray lower rail from turn bottom to release bottom; upward final head; no shared junction with l8. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303

Every actual XML edge is covered exactly once. PNG inspection checked source, destination, direction, head visibility, label association, crossings and false junctions. Final identified defects: zero.
