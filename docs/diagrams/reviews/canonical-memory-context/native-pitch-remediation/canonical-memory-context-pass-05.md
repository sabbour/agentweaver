# canonical-memory-context — pass 05

Corrected the source-copy encoding defect without changing the intended labels, content, geometry or arrows. Explicit UTF-8 restores the previously inspected pass-3 glyphs. Every connector was traced again on the newly exported PNG.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 0; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n1 → n3 | combine / sort | Exit (0.65,1); entry (1,0.3); waypoints [('447.6', '270'), ('282', '270'), ('282', '351.0')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:65-89; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:107-140 | clean |
| e1 | n0 → n4 | approved | Exit (0.35,1); entry (0.35,0); waypoints [('118.39999999999999', '282'), ('380.4', '282')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:54-63,154-176; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213 | clean |
| e2 | n2 → n4 | latest open | Exit (0.65,1); entry (0.65,0); waypoints [('709.6', '288'), ('447.6', '288')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:94-104; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213 | clean |
| e3 | n3 → n4 | selected | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:107-140; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213 | clean |
| e4 | n4 → n5 | serialize | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213; apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:185-225 | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
