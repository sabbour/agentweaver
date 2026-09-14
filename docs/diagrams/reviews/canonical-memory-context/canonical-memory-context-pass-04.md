# canonical-memory-context — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `canonical-memory-context-pass-04.png` (2× official draw.io export) and `canonical-memory-context-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n1: Core + learnings | n3: Joint rank + budget | combine / sort | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:65-89; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:107-140 | combined candidates bottom → joint rank right through left gutter; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n0: Active decisions | n4: Context compiler | approved | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:54-63,154-176; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213 | decisions bottom → compiler top-left; bridge keeps memory-ranking path independent; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n2: Open session | n4: Context compiler | latest open | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:94-104; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213 | open session bottom → compiler top-right on a separate lower lane; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n3: Joint rank + budget | n4: Context compiler | selected | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:107-140; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213 | joint rank right → compiler left; selected memories only; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n4: Context compiler | n5: Untrusted JSON | serialize | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:185-225 | compiler right → untrusted JSON left; target block arrowhead, no false junction dots | clean |
