# auth-security-fig1 — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `auth-security-fig1-pass-04.png` (2× official draw.io export) and `auth-security-fig1-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Protected request | n1: Scheme selector | classify | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:20-57; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57 | request right → selector left; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: Scheme selector | n2: Entra handler | otherwise | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:131-153 | selector right → Entra left; otherwise/default branch; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n1: Scheme selector | n3: Scoped handlers | eligible | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:32-55; 159-250 | selector bottom → scoped handlers right via the left gutter; eligible alternative branch; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n2: Entra handler | n4: Resource authorizer | authenticated | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:131-153; apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23 | Entra bottom → authorizer top; bridge separates this path from the selector route; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n3: Scoped handlers | n4: Resource authorizer | authenticated | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:32-55; 159-250; apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23 | scoped handlers right → authorizer left; target block arrowhead, no false junction dots | clean |
| e5 / 6 | n4: Resource authorizer | n5: Protected operation | authorized | apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23; docs/deep-dive/auth-security.md:5-23; apps/Agentweaver.Api/Security | authorizer right → protected operation left; target block arrowhead, no false junction dots | clean |
