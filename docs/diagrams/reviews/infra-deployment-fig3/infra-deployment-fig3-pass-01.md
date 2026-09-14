# infra-deployment-fig3: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 7069 -> 27063 (3.828406x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Cross-row apply and CRDs connectors pass through preceding cards.
- Namespace, storage, Service and rollout objects need correct native resource classifications rather than generic Pod icons.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `scripts/azure/image-spec.mjs:80-141; scripts/azure/steps/20-build-push-images.mjs:352-381; scripts/azure/steps/30-deploy.mjs:52-113,427-561`.
