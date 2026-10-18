# Component releases

`releases/foundation.json` is a draft component manifest, not a platform release.

| Component | Kind | Manifest version |
| --- | --- | --- |
| `Agentweaver.Abstractions` | Contract | `1.0.0` |
| `Agentweaver.Providers` | Library | `1.0.0` |
| `Agentweaver.SourceControl` | Library | `1.0.0` |
| `Agentweaver.Orchestrator.Core` | Library | `1.0.0` |
| `Agentweaver.Persistence.Postgres` | Library | `1.0.0` |
| `Agentweaver.Secrets.AzureKeyVault` | Library | `1.0.0` |
| `Agentweaver.Identity` | Library | `1.0.0` |
| `Agentweaver.AgentRuntime` | Library | `1.0.0` |
| `Agentweaver.AgentHost` | Service | `1.0.0` |
| `Agentweaver.Telemetry` | Library | `1.0.0` |
| `Agentweaver.Telemetry.AzureMonitor` | Library | `1.0.0` |
| `Agentweaver.ObjectStore.AzureBlob` | Library | `1.0.0` |
| `Agentweaver.Identity.Broker` | Service | `1.0.0` |
| `Agentweaver.EventsAndSessions` | Service | `1.0.0` |
| `Agentweaver.Gateway` | Service | `1.0.0` |
| `Agentweaver.Mcp` | Service | `1.0.0` |
| `Agentweaver.Web` | Service | `1.0.0` |
| `Agentweaver.Orchestrator` | Service | `1.0.0` |
| `Agentweaver.Environment` | Service | `1.0.0` |
| `Agentweaver.Providers.Storage.AzureFiles` | Library | `1.0.0` |
| `Agentweaver.Providers.Sandbox.AgentSandbox` | Library | `1.0.0` |
| `Agentweaver.Knowledge` | Service | `1.0.0` |
| `Agentweaver.Projects.Config` | Service | `1.0.0` |
| `Agentweaver.FoundationProbe` | Acceptance-only service | `0.0.1` |

The manifest pins exact project-reference compatibility but does not pin container
image digests. The existing Identity Broker image `f989` remains separate from
these prepared source versions. The historical P0 package baseline was `0.0.0`.
Its initial notes remain in the archived P0 changesets referenced by the prior receipt.
The Gateway is a source-only service candidate at `1.0.0`; its manifest entry and
documentation do not claim deployment or live acceptance.
The first-party MCP host is a source-only service candidate at `1.0.0`; it validates
Broker access tokens and delegates to the Gateway, without claiming deployment or
live acceptance.
Environment uses its existing executable host and SDK container configuration.
Its release artifact is a service image, not a NuGet library.
The Orchestrator Core compatibility entries include both Abstractions and Providers,
matching its direct project references.
The runtime library pins its native SDK dependency and references Abstractions
and Identity. Environment now references Identity for its separate bootstrap
profile contract. The separate AgentHost service entry describes the unpublished executable and pinned image definition.
AgentHost also references Source Control for the trusted version 2 BuildTest repository and full-output helpers.
The manifest records that direct compatibility dependency.
CI builds an exact-source amd64 image, measures its compressed OCI bytes, and checks native SDK startup without model execution.
These components do not publish credentials or prove deployment or paid-model acceptance.

Each product component change needs a fresh `.changeset` record. The record names the manifest component ID and semver intent. Documentation-only changes do not need a changeset.

## Validate and prepare

Run the validator:

```powershell
npm run release:validate
npm run release:validate -- --base <full-base-commit-sha>
```

The base-aware form checks changeset coverage for changed components.
Both CI workflows use `npm run release:validate -- --ci` on the integrated checkout.
For a pull request, the tested commit's ordered parents must match the event base and authored head.
For an ordinary merge push, those parents must match the previous target and authored head.
Validation compares their common ancestor with the integrated tested tree, not only with the authored tree.
A linear push retains its previous-commit comparison.
Missing or mismatched history fails explicitly.
The comparison base never comes from a receipt's own claim.
Preparation receipts and archive-byte checks remain unchanged.

```mermaid
flowchart LR
  Event["Verified event and ordered commit parents"] --> SourceBase["Authored source common ancestor"]
  SourceBase --> Diff["Compare with the integrated tested tree"]
  Diff --> Guards["Unchanged receipt, archive, and product coverage checks"]
  Tested["Integrated checkout and exact coverage identity"] --> Diff
```

`npm run release:plan` writes a source-bound plan under ignored `artifacts/release/`. It does not change component versions.

The user-selected P1 plan applied `1.0.0` source versions for all 23 product components:
13 packages and 10 service images.
The canonical application archived its major-intent changeset and the consumed earlier records.
The acceptance-only Probe has its separate prepared `0.0.1` source version.
The draft accepts `1.0.0` for all 53 direct dependency relationships, including
the Probe's product dependencies. It retains each earlier accepted version.
These declarations retain the reviewed consumer and dependency source.
They change no runtime code, API contract, or external dependency pin.
This compatibility decision does not apply to later source changes.
The manifest, project version fields and Web version mirrors now contain these versions.
The preparation receipt is `releases/receipts/62a0dcf5307dc7ef58c805d4c66b45baecd98e43.json`.
Its source SHA identifies the reviewed plan inputs, not a built or published artifact.
The manifest remains a draft. No package or image was built or published by this application.

`npm run release:apply` updates manifest versions, project version mirrors, changelog, archive records, and a receipt. It requires a clean tree and a valid source-bound plan.

`npm run release:pack` builds and packs the manifest components. It writes local package and image artifacts. It does not push them to a registry or package feed.

Use `npm run release:pack -- --platform-only` to prepare every product component
without the acceptance-only Foundation Probe.
Use `--platform-only` for the matching publication command.
The full manifest hash remains in the preparation receipt.
The publisher rejects a receipt prepared with another component selection.
This selection cannot combine with `--packages-only` or `--foundation-probe-only`.

For a product build, the automated v1 release workflow first prepares its source-pinned build inputs.
It pulls only the three source-pinned Microsoft base images.
A standard locked npm install fills the cache from the source-committed Web lockfile.
It retains the committed download destinations and npm's existing integrity checks.
This preparation runs no dependency install scripts and adds no dependency.
The image job starts Docker Engine `v29.9.0` with the official `docker/setup-docker-action` `v5.5.0`, pinned to commit `2bf61fb9464cc67f0cbdeabed6aa0380accd1c70`.
The action enables `containerd-snapshotter` in its temporary Docker daemon.
The action exports that daemon's Unix socket through `DOCKER_HOST`, so Docker commands use the temporary engine without changing context metadata.
The default Buildx builder then uses the same local Docker Engine as the image pulls.
Preparation reads the standard `docker buildx inspect default` output and requires exactly one `Driver: docker` and one `Endpoint: default` entry.
It also checks the shared store, image digests, and clean source before packing.
The action stops its daemon at the end of the job and does not change the runner's preinstalled daemon.
The runner still requires Buildx `0.13` or later. The workflow does not install Buildx or fetch a builder image.
The existing offline Web install and image checks remain unchanged.
These inputs are prepared on the image runner, not on a developer's machine.
The package and image jobs upload separate artifacts, bound to the same source SHA and manifest hash.

```mermaid
flowchart LR
  Receipt["Merged version-application receipt on v1"] --> Validate["Wait for CI; validate manifest and changesets"]
  Validate --> Packages["Pack NuGet packages"]
  Validate --> Images["Build product Docker images"]
  Packages --> Artifacts["Upload exact-source artifacts"]
  Images --> Artifacts
  Artifacts --> Publish["Publish changed packages and images; save receipt"]
```

## Publication boundary

The v1 CI workflows run tests, build, lint, coverage, changeset, release-composition,
and documentation checks on pull requests and pushes. A committed
`releases/receipts/**` version-application receipt on `v1` triggers automatic
publication. Before building packages or images, the release workflow waits for
both CI workflows to pass on the exact source commit. `workflow_dispatch` runs
the same release path manually and still requires a fresh receipt, so it must
target the version-application commit that added the receipt.
There is no publish toggle or environment approval. The Foundation Probe is
excluded from product packages and images.

P1 NuGet publication uses the existing Actions token only with
`https://nuget.pkg.github.com/sabbour/index.json`.
The `publish` job fixes this feed and the `ghcr.io/sabbour` destination.
Only that job receives `contents: write` and `packages: write`; the package and
image jobs remain read-only.
No stored NuGet key or external-feed credential fallback is added.
The publisher writes a temporary `NuGet.Config` in the operating system's temp
folder, binds the approved feed to the repository owner's username and the same
Actions token, and passes that file explicitly to `dotnet nuget push`. The
directory and file are restricted to the runner user, then removed whether the
push succeeds or fails. This does not read or change a developer's global NuGet
settings, and the config is not included in release artifacts or receipts.
Before any push, the publisher verifies source HEAD, manifest, selected components,
and artifact hashes. It records immutable claim and result records and uploads the
publication receipt for the local deployment tooling. It publishes only NuGet
packages whose versions changed in the verified receipt, while publishing all
ten product service images. Each service image tag contains its component
version and the full source SHA, so a later receipt cannot move an existing
version tag. The workflow queues receipt-triggered runs in FIFO order, so later
runs cannot replace an earlier pending release.

GitHub Actions never deploys to Azure or AKS. Use the local `release:deploy`
command for deployment and live acceptance.
For the v1.0.0 P0 bootstrap, `release:deploy-bootstrap` accepts the source-bound
published receipt, its immutable continuation proof, and the tracked draft.
For v1.0.1, it accepts only the exact tag-source normal publication receipt and
tracked draft, rejecting continuation receipts and completion proofs. Both
bootstrap results are pre-acceptance only; neither replaces or weakens
`release:deploy`, which remains the strict gate for the v1.0.0 product-version
composition. Its current version constraints do not accept v1.0.1's
mixed-version composition, even after persona evidence exists. The complete
P0 product installation is expressed by the root Kustomization at
`deploy/k8s`. After workload-required runtime inputs and Gateway placement are approved, the
operator can apply it with `kubectl apply -k deploy\k8s`. The bundle contains
all nine shared-service Deployments and Services, ServiceAccounts, runtime
ConfigMaps, the Environment Cilium settings, Projects provider catalog,
NetworkPolicies, and required Cilium policies. Product images are pinned with
Kustomize `images:` entries. The service-owner policies allow only configured
Broker issuer HTTPS, workload-scoped Entra/PostgreSQL access, Environment's
Kubernetes API path, and configured owner-service calls. Secrets, private keys,
certificates, public CA data, database grants and migrations, and Gateway
placement remain operator-supplied.

`release:deploy` and `release:deploy-bootstrap` use the same Kustomize source.
They verify the source-bound publication receipt, all ten product image
digests, the Azure and Kubernetes target, runtime prerequisites, TLS/CA
requirements, and existing Broker routing before applying. Dry-run is
read-only; `--execute` applies the rendered installation with `kubectl apply -k`
and then performs live rollout and image checks. The helper no longer generates
Deployments or Services from a JavaScript List. The bootstrap is pre-acceptance
only and does not record release acceptance.

Gateway, Web, and Knowledge now have source-backed runtime values. Gateway uses
`https://portal.6ac31fa7dd86cf00014876f0.eastus2euap.aksapp.io`; the root
Kustomization includes its Web HTTPRoute, while the external Gateway source adds
the matching `portal` listener under the existing App Routing managed domain.
Web uses the Identity Broker public client `agentweaver-web` with
`openid profile email api.read offline_access projects.admin
projects.orchestrator`; its `/auth/callback` is not added to the upstream Entra
application, whose `/signin-oidc` callback remains unchanged. The immutable
Broker ConfigMap is versioned as `identity-broker-runtime-config-v2`, and the
Deployment reference change rolls the Broker Pod. Knowledge uses PostgreSQL
role `aw-v1-p0-id-knowledge`, resource ID `agentweaver.knowledge`, and
generation `1`.

The existing cluster Gateway currently lacks the `portal` listener, so update
the reviewed Gateway source and verify managed DNS/TLS before claiming Portal
readiness. PostgreSQL role existence and grants for Knowledge also remain to be
verified before deployment. The three Gateway ingress policies remain in the
bundle without changing Gateway placement; they require an approved Gateway
namespace and namespace policy mode (`restricted` or `baseline`). The source
also includes an incomplete `agenthost-runtime-v1` ConfigMap, but it is not a
deployment prerequisite while Environment's optional AgentHost profile remains
unset. Enabling that profile requires the exact per-Sandbox configure endpoint
and matching audience, an approved `MaximumPendingTurns`, and the name of an
existing operator-supplied TLS Secret.
The
[P0 Kubernetes input inventory](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md#p0-runtime-configmap-snapshots)
records value provenance, remaining inputs, and the install command. Pass the
catalog PR merge commit as `--provider-catalog-source-commit`; the catalog
snapshot separately records the published product source. Applying the
Kustomize bundle does not prove Azure acceptance.

## One-use v1.0.0 completion

The one-use continuation for `v1.0.0` is consumed. Run `38997112717` published
the 11 missing outputs and verified the full 23-output release. It is not a
retry path. Deployment uses the product source at tag `v1.0.0` (commit
`c24d0000fe5fc6163e4f38594c39dff782d3a46d`) and a separate helper checkout
pinned to `5cb088d`. Comments added to AgentHost inputs on the current `v1`
branch do not change that release checkout.

The continuation did not use the ordinary publication environment approval.
Its immutable admission, receipt, registry, package-read, and image-validation
checks remain documented in the [release operations guide](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md).
