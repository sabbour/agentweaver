# sandbox-fig3: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6683 -> 25820 (3.863534x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Cleanup alternatives are detached from the execution flow.
- Production preview lookup is an isolated card outside the retained command-path takeaway; keep that distinction inline.
- The sandbox controller is not a Pod and needs an appropriate native classification.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:287-361; k8s/base/sandbox-template-agenthost.yaml:104-122,252-280`.
