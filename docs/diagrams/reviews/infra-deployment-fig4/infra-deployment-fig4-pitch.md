# infra-deployment-fig4: pitch inspection

Takeaway: Gateway TLS termination and HTTPRoute selection keep public backends explicit.

Audience: operators and contributors reading the execution deep dives.
One editable A5 landscape page. Exported with draw.io Desktop 31.4.5.
Opened enlarged and in the saved A5-scale contact sheets.
Sparse generic cards are a sketch, not an approved native-notation composition.
Missing hierarchy, relationship detail and process boundaries require redesign.

Evidence: `k8s/base/httproute-api.yaml:25-63; k8s/base/mcp-httproute.yaml:14-46; k8s/base/httproute-frontend.yaml:27-34; k8s/base/worker-deployment.yaml:111-117`.

Fluent palette/template/library: repository-owned docs/diagrams/drawio assets.
Native symbol proposals and source-backed content: content-models.json and the
three research reports in the canonical-pod-process-boundaries review directory.
