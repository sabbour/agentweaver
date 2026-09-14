# canonical-provider-admission — pass 04

Separate no-layout-change export and complete arrow trace. PNG inspection caught Windows default-decoding corruption in Unicode annotation glyphs during source copying. This pass is retained as historical evidence, NOT the final promoted source. Diagrams containing only ASCII had no glyph defect.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 2; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | signed context | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:205-207,256-290; apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-358 | clean |
| e1 | n1 → n2 | match | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-358; apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:121-183; apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs | clean |
| e2 | n2 → n3 | capture | Exit (0.65,1); entry (1,0.3); waypoints [('709.6', '288'), ('282', '288'), ('282', '351.0')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:121-183; apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs; apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-77,83-155; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:796-825 | clean |
| e3 | n3 → n4 | load boundary | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-77,83-155; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:796-825; apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:826-855 | clean |
| e4 | n4 → n5 | Copilot capability | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:826-855; apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:35-123; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
