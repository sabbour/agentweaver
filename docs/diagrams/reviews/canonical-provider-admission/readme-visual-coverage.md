# README visual coverage

> **Visual acceptance rejected by the user; authoring/promotion paused.**
> The factual coverage and mechanical results below remain evidence, not visual
> approval. The README's promoted architecture PNG remains frozen pending an
> approved shared calibration/template; see `visual-pause.md`.

Scope: the entire root `README.md`, explicitly owned by `plan-shared.json`.
This is an asset/diagram audit, not incidental prose review. Both actual PNGs were
opened and inspected again for this scope confirmation. No new diagram was needed.

| Embedded visual | Syntax | Disposition | Result |
| --- | --- | --- | --- |
| `docs/public/agentweaver.png` | HTML `img`, width 128 | Retain | Existing interlocking Agentweaver brand mark. Keep its stable path and accurate `Agentweaver logo` alt text. It asserts no runtime architecture and is not converted into a draw.io diagram. |
| `docs/diagrams/email-architecture.png` | Markdown image | Reuse | Use the reviewed shared canonical, its editable source and existing inspected lineage. Alt text correctly separates Entra identity, GitHub capabilities, API/worker control, MCP forwarding and PostgreSQL. |
| Former `docs/public/pitch-architecture.png` | Former Markdown image | Merge/remove duplicate | Already replaced by the shared canonical and retired after the repository consumer scan. No remaining README embed points to it. It is not used as factual evidence. |

There are exactly **two current embedded visuals**, one of which is an architecture
diagram. No additional image references, SVG, diagram code blocks, canvas, media,
iframe or CSS image constructs occur. The capability table remains a written table;
the quick-start shell block remains instructions, not an invented diagram.

## Independently checked repository truth

- **Brand identity:** `docs/.vitepress/config.ts:39` uses `/agentweaver.png` as the
  documentation logo. The README retains that same repository asset rather than
  inventing a new brand or inferring architecture from the logo.
- **Entra identity versus GitHub capability:** current
  `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:20-59` selects named handlers
  and defaults to Entra, not GitHub user-token validation.
  `docs/guide/authentication.md:7-15` independently documents the same separation.
  `tests/Agentweaver.Tests/Auth/LegacyOAuthRetirementTests.cs:12-25` asserts legacy
  GitHub OAuth endpoints are not mapped. This test source was inspected, not
  represented as a newly executed .NET test.
- **Control and persistence:** `k8s/base/api-deployment.yaml` and
  `k8s/base/worker-deployment.yaml` configure the Postgres database provider,
  connection strings and CSI secret volumes. The diagram separates durable
  PostgreSQL state from mounted files rather than treating CSI as a database.
- **MCP forwarding:** `apps/Agentweaver.Mcp/Program.cs:31-63,74-100` wires the API
  HTTP client and broker-authenticated HTTP tool endpoint;
  `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:353-374` selects the validated
  broker token for downstream bearer authentication. MCP is not drawn as a
  separate database owner.
- **Execution credentials are constrained, not absent:**
  `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:697-708,1240-1290`
  mints repository material and places `repositoryAccessToken`, separate Copilot
  material and the turn token in the trusted configure payload. The diagram and
  README do not claim GitHub is product identity or that the sandbox is
  credential-free. Normative claims are not substituted for this implementation.

The image is a logical deployment/trust view, not a live-cluster capture, exhaustive
resource inventory or precise credential protocol sequence. Its alt text no longer
claims that unpictured client, Key Vault or model-provider nodes are shown.

## Inspected canonical and arrow coverage

The active `docs/diagrams/reviews/email-architecture/iteration-manifest.json`
selects the accepted `fluent-pitch-remediation` lineage. Its initial pitch and four
post-pitch passes remain separate; pass-1 meaningful XML growth is
3265 to 36756 (**11.25758x**). The final canonical is uncompressed A5 landscape,
with native Kubernetes, Azure Entra, cloud and database symbols in the Fluent style,
exported by pinned draw.io Desktop 31.4.5.

The README reuses that lineage rather than claiming this coverage check constitutes
a new pitch or extra iteration pass. The full canonical PNG was opened again;
all five depicted relationships were traced against the implementation evidence:

| Source | Target | Relationship |
| --- | --- | --- |
| MCP | API + worker control-plane group | Authenticated HTTP API calls |
| API + worker | AgentHost | Configure and A2A control |
| Entra | API + worker control-plane group | User identity |
| GitHub | API + worker control-plane group | Purpose-bound repository capability |
| API + worker | PostgreSQL | Read/write persistence |

The combined API/worker box is a grouping: its edges do not assert that the worker
hosts the public HTTP endpoint. Arrows express logical relations, not all response
packets. No direction, endpoint or overlap defect requiring source reauthoring was
found. Existing print-size and enlarged inspections remain in the accepted lineage.

## Repeatable validation and scope

`validate-shared.py` now explicitly extracts **both HTML and Markdown README images**,
requires exactly the two audited targets with nonempty alt text, opens/verifies
both PNGs, records dimensions and SHA-256, and rejects unreviewed visual constructs.
Its existing canonical checks cover source/final-pixel identity, renderer stamps,
manifest schema, growth, A5 and all final arrows. It also verifies the retired
duplicate is absent. Results are in `shared-validation.json` under
`readme_visual_coverage`; README now links this evidence in its provenance comment.

Executed follow-up results: **2/2 README images passed**, all 27 canonical drift
checks passed, and the standard documentation build passed in **78.21 seconds**
(`readme-docs-build.log`). Scoped diff whitespace checks passed. The three existing
contributor document-link findings are unrelated to README and remain reported.

Only README and shared-owned report/helper entries change in this follow-up.
The logo, canonical source/PNG/hash and accepted review lineage are unchanged;
therefore no synthetic extra passes or new research agents are claimed.
The global inventory, pipeline/skill code, product code and coordinator pilot
remain outside this write scope.
