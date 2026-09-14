# infra-deployment-fig2: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6631 -> 26716 (4.028955x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Vault-to-CSI routing crosses authorization cards and their labels.
- ServiceAccounts, CSI and workloads all use the same Pod icon despite distinct resource types.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `scripts/azure/steps/15-setup-identity.mjs:245-438; k8s/base/secret-provider-class.yaml:16-61; apps/Agentweaver.Api/Program.cs:231-250; k8s/base/mcp-deployment.yaml:45-83`.
