# live-preview-provisioning-fig1: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6733 -> 27074 (4.02109x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Observe, reject and probe connectors cross content cards instead of using phase gutters.
- Approved publication combines Service/HTTPRoute creation behind a Pod icon.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs:95-324; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:272-328; tests/Agentweaver.Tests/Preview/PreviewStepTests.cs:194-277,567-597`.
