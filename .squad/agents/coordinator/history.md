

## 2026-07-06 v0.9.0 staging wave
- Fixed local dev port/startup probe behavior for the frontend and validated the staging workflow.
📌 Team update (2026-07-10T05:55:00-07:00): Keep #196/#207/#208 issue work bounded; preserve the baseline API checkpoint and resume immediately toward three consecutive clean public-API application journeys. Tank may not revise #207; Morpheus owns the independent revision. — recorded by Scribe

## 2026-07-14T15:15:00Z — v0.9.50-rc1 staging ship
Release batch shipped to staging with live verification and infra checks passing. Process lesson reinforced: local diffs and peer review alone are not enough for closure when the fix has not been tagged and deployed yet.


## 2026-07-15T13:55:00Z — v0.9.58/v0.9.59 staging wave
- Shipped **v0.9.58** with the provenance-verifier `accumulated-prov-tag` ambiguity fix (`9089174f`).
- Live Harness verification on v0.9.58 marked **#272, #335, #337, #338, #339** PASS and **#336** conditional-PASS, then discovered **#341**.
- `fenster-341` fixed #341 in `apps/Agentweaver.Mcp/Tools/RunTools.cs`, merged it to `main`, and reported **65 MCP tests passing**.
- Shipped **v0.9.59** (`561ddc19`) to staging; post-ship validation filed **#342** after the provenance verifier false-failed on a hardcoded `agent-host` pod-count expectation even though manual `kubectl` checks confirmed the real pods were healthy.
- Follow-up live MCP stress verification remains in progress under `harness-v0959-mcp-stress`.


## 2026-07-20T12-01-24-07-00 — CI/docs ship coordination
- Added the local/Azure Quick Start hero block to `docs/index.md` + `custom.css`, waited for rubber-duck approval, verified the final batch (220/220), then committed and pushed `95a855a0` to `origin/main`.
