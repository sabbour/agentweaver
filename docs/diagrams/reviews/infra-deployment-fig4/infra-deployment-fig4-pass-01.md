# infra-deployment-fig4: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 4661 -> 18589 (3.9882x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- API/MCP branch rails overlap the HTTPS/select route and cross Gateway/client cards.
- Backend Services use Pod icons, conflating service selection with pod execution.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `k8s/base/httproute-api.yaml:25-63; k8s/base/mcp-httproute.yaml:14-46; k8s/base/httproute-frontend.yaml:27-34; k8s/base/worker-deployment.yaml:111-117`.
