# canonical-agent-communication-a2a: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 5421 -> 18152 (3.34846x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- The serpentine return path is readable, but worker/AgentHost ownership needs explicit grouped boundaries.
- The bridge is represented by a Pod icon although it is a logical runtime component.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:220-344; apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:202-265; tests/Agentweaver.Tests/AgentHost/A2ATurnBridgeAgentTests.cs:341-366`.
