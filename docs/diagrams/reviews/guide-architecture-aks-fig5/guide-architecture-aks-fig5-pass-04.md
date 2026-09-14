# guide-architecture-aks-fig5: pass 04

Mode: correction-only. Reviewer/model: GPT-6 Astra (`gpt-6-astra`).
Actual inspected evidence: `guide-architecture-aks-fig5-pass-04.png` enlarged and
`guide-architecture-aks-fig5-pass-04-print.png` at approximate A5 print scale.
The images were opened, not inferred solely from XML.

No new content. Re-exported and traced all eight arrows. Vault-to-broker is credential delivery, not a request. AgentHost identity has no vault edge. Configure and certificate/CSI crossings are bridges, never junctions.

Observed remaining issue clusters: orientation/in-page 0,
overlap/fitting 0, arrows 0.
These are human inspection findings, not an automated overlap-score claim.
Passes 2-4 preserve semantic node/edge identity; only visibility, fit, labels and
endpoint/routing defects are corrected. No expansion to meet a later growth target.

Draw.io SHA256: `f80727a45f334d9a367c3fc65348e7ee830ac4de7e28f0b02235cda91caa128d`.
PNG SHA256: `be012a6d30bfcdf0fb924eb54a75caf7fba40364efe475cadb6945f3dd36883d`.

## Complete arrow trace

- **s1** `sa -> mi`: Rightward SA federation to API identity; API and Worker both included. Evidence: scripts/azure/steps/15-setup-identity.mjs:245-260,314-423
- **s2** `mi -> vault`: Downward identity authority to vault, not an AgentHost grant. Evidence: scripts/azure/steps/15-setup-identity.mjs:245-260,314-423
- **s3** `vault -> broker`: Leftward credential response from vault to broker; label corrected from redeem and placed below rail. Evidence: apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:78-119; apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:81-117
- **s4** `broker -> configured`: Broker bottom to configured-runtime bottom via y=386; upward head; CSI/certificate crossings have bridge arcs. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303
- **s5** `hmi -> configured`: Downward separate AgentHost pod identity to runtime; no authority edge to vault. Evidence: scripts/azure/steps/15-setup-identity.mjs:245-260,314-423
- **s6** `vault -> csi`: Vault right around y=408 to CSI top; downward head, bridges at crossings, app-secrets label on rail. Evidence: k8s/base/secret-provider-class.yaml:1-62
- **s7** `vault -> oauth`: Vault bottom to OAuth certificates top; downward head; two bridge crossings are not joins. Evidence: apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:319-377
- **s8** `configured -> mcp`: Runtime right around exterior gutter to MCP right; leftward head, Assistant JWT label stays inside page. Evidence: apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs:23-37,102-113

Every actual XML edge is covered exactly once. PNG inspection checked source, destination, direction, head visibility, label association, crossings and false junctions. Final identified defects: zero.
