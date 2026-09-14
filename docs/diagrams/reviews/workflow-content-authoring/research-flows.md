# Completed independent flow thread — relayed findings

Provenance: parent reports a separate completed GPT-6 Astra direction/flow thread. This handoff preserves the reported conclusions and reconciles every relationship against current code. Default is merge → create/reuse PR → Scribe, not a proved git push. Explicit/conversational choices precede singleton handling; malformed/unknown choices permit two model attempts, exceptions fall back immediately. Runtime fallback prefers default/standard, then a non-code-review candidate, finally the first candidate. Outer coordinator fallback is the project default.

- `research → draft`: unconditional — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:82–83`.
- `draft → edit`: unconditional — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:85–86`.
- `edit → rai-check`: unconditional — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:89–90`.
- `rai-check → draft`: revise — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:93–95`.
- `rai-check → terminal-safety-failed`: safety-failed — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:97–99`.
- `rai-check → done`: no-changes — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:101–103`.
- `rai-check → human-review`: review — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:105–107`.
- `human-review → publish`: approved — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:109–111`.
- `human-review → draft`: request-changes — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:113–115`.
- `human-review → terminal-declined`: declined — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:117–119`.
- `publish → done`: unconditional — `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:121–122`.
