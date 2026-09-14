# canonical-provider-admission — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `canonical-provider-admission-pass-04.png` (2× official draw.io export) and `canonical-provider-admission-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Prepare context | n1: Accept request | signed context | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:205-207,256-290; apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-358 | prepare right → accept left; signed context, not provider invocation; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: Accept request | n2: Accepted plan | match | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-358; apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:121-183; apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs | accept right → accepted plan left; only matching context; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n2: Accepted plan | n3: Run snapshot | capture | apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:121-183; apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs; apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-77,83-155; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:796-825 | accepted plan bottom → snapshot right via left gutter; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n3: Run snapshot | n4: Invocation guard | load boundary | apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-77,83-155; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:796-825; apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:826-855 | snapshot right → invocation guard left; durable boundary loaded; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n4: Invocation guard | n5: Capability fences | Copilot capability | apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:826-855; apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:35-123; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs | invocation guard right → live fences left; Copilot capability path, not a BYOK credential requirement; target block arrowhead, no false junction dots | clean |
