# Story: P0 Foundation Probe

**Issue:** [#1784](https://github.com/sabbour/agentweaver/issues/1784).
**Prerequisite:** [#1777 dedicated Azure P0 infrastructure](1777-azure-p0-infrastructure.md).
**Status:** The corrected initial image keeps version `0.0.0`. A separately confirmed one-time replacement of the existing `0.0.0` tag must preserve the previous image by digest. Source and publication do not prove a successful runtime acceptance Job.

## Scope and boundaries

This acceptance-only .NET 10 executable composes the existing workload-identity,
Key Vault, Blob object-store, PostgreSQL outbox/inbox, provider-resolution, and
Azure Monitor libraries in one AKS Job. It records source-bound configuration and
operation evidence for an externally authorized acceptance run. It is not a
product service, does not run schema migrations, and does not provision resources.

The default invocation prints a plan and performs no resource operation:

```powershell
dotnet run --project tools\Agentweaver.FoundationProbe -- --plan
```

Execution requires both `--execute` and an explicit `--target <path>`. There is
no latest-version lookup, credential fallback, default target, or implicit
execution. Errors print stable codes, not connection strings, tokens, or secret
values.

## Target and source contract

The target file is the strict camelCase `ProbeTarget` DTO, not the raw Azure CLI
deployment-output envelope. Unknown properties and missing required fields fail.
Its top-level fields are:

| Field | Contract |
| --- | --- |
| `sourceSha` | Exact 40-lowercase-hex image-build Git commit. |
| `sourceTree` | Exact 40-lowercase-hex image-build Git tree SHA. It is not `sourceHash`. |
| `sourceHash` | Exact 64-lowercase-hex SHA-256 of infrastructure inputs at the image-build commit. |
| `subscriptionId`, `tenantId` | GUIDs for the dedicated deployment. |
| `resourceGroup`, `resourceGroupId` | Exact `aw-v1-p0` name and matching subscription-scoped ID. |
| `deploymentName`, `deploymentId` | The selected successful infrastructure deployment and its exact ARM ID. |
| `infrastructure` | Independent deployment scope, source SHA, Git tree, input hash, and original foundation binding. |
| `aksOidcIssuerUrl` | HTTPS issuer from the deployment output. |
| `foundationProbeIdentity` | The #1777 identity object, including resource ID, distinct client/principal IDs, namespace, and service account. |
| `foundationResources` | Exact cluster, Key Vault, storage account/container, PostgreSQL server, workspace, and Application Insights IDs/endpoints. |
| `runtime` | Explicit database name/role/schema and exact Key Vault fixture name/version. These values are not guessed by the probe. |

`ProbeTargetValidator` binds the values to the dedicated resource names and to
the source metadata embedded in the image. The runtime role is
`foundation_probe_runtime`, the schema is `foundation_probe`, and the fixture is
the exact 32-lowercase-hex `foundation-probe` secret version. Operators or the
acceptance orchestrator must map the supported #1777 output fields and separately
approved runtime configuration into this DTO.

Image-build provenance does not determine the infrastructure deployment name.
For AKS-only deployment, `infrastructure` binds the actual AKS deployment
and the original successful full-foundation deployment separately.
The host verifier checks both against their historical Git sources and native
ARM receipts. It never substitutes the image-build SHA for either deployment.
The runtime receipt retains image metadata at the root and records infrastructure
metadata under `deployment.infrastructure`.
The previous target shape without this binding is rejected.
AKS-only collection requires `--foundation-expected-sha` for the original foundation.

The projected token file, `AZURE_CLIENT_ID`, and `AZURE_TENANT_ID` must match
the target. The JWT claims must have the exact target issuer,
`system:serviceaccount:agentweaver-v1-p0:foundation-probe` subject, and sole
`api://AzureADTokenExchange` audience. The Azure SDK operations then use the
explicit `WorkloadIdentityCredential`; no alternate credential is attempted.

The acceptance egress policy permits ingestion and private Logs queries separately.
The input installer verifies the owned AMPLS endpoint and reciprocal NIC.
Its API member must match the dedicated private DNS A record and subnet.
Only the observed private `/32` receives Logs TCP port 443 access.
DNS permits `api.loganalytics.io`, `api.monitor.azure.com`, and
`api.privatelink.monitor.azure.com` without wildcards.
An endpoint outside the approved private resources blocks installation.
The manifest's private-IP placeholder must use this observed address, not a public fallback.

## Runtime checks and evidence

Before resource operations, the probe validates
`APPLICATIONINSIGHTS_CONNECTION_STRING`. It requires one GUID
`InstrumentationKey` and an HTTPS `IngestionEndpoint` under
`.in.applicationinsights.azure.com`. Duplicate/malformed fields, non-HTTPS or
off-domain endpoints, and missing values block execution. The camelCase receipt
contains only the Application Insights resource ID, normalized ingestion
endpoint, and a boolean indicating the instrumentation key was configured; it
never contains the key or full connection string. The runner rechecks that
evidence against the target before it can return a success receipt.

The provider catalog resolves and pins the fixed Secrets, ObjectStore, and
Telemetry providers to the target Key Vault, Blob container, and Application
Insights resource. The receipt records their seam, provider ID, adapter/options
versions, resource ID, generation, and negotiated capabilities.

The probe then performs these bounded checks:

1. **Key Vault:** redeem only the configured secret name and exact version with
   the existing adapter and workload identity. The returned credential is
   invalidated after redemption. Secret contents never enter the receipt.
   Secrets User is read-only for this fixture; the probe does not create,
   replace, delete, or purge it.
2. **Blob:** create
   `foundation-probe/<nonce>/roundtrip.json` with `If-None-Match: *` and
   metadata binding the nonce, source SHA, and Git tree. Verify metadata and
   upload ETag, read through `AzureBlobObjectStore`, and compare the content
   hash. Cleanup independently re-reads ownership and deletes only with the
   observed ETag in `If-Match`. The bounded cleanup attempt also runs after an
   uncertain upload result. Missing ownership or unconfirmed cleanup prevents
   a receipt.
3. **PostgreSQL:** obtain an Entra token asynchronously for each new physical
   Npgsql connection; synchronous token acquisition throws. TLS uses
   `VerifyFull`. The pre-migrated runtime role inserts one domain effect and
   admits the nonce in the consumer inbox, then enqueues the corresponding
   outbox event in the same transaction. The probe verifies all three rows
   after commit. It never runs migrations or creates roles.
4. **Azure Monitor:** start a `foundation-probe` server Activity tagged with
   `probe.source_sha`, `probe.source_tree`, and a fresh 32-lowercase-hex
   `probe.nonce`, then force-flush the exporter. The receipt records the exact
   trace and span IDs and start time. No raw connection string or key is
   recorded.

The final runtime receipt is camelCase and intentionally has no `kind` or
`schemaVersion`. It is printed only after every evidence check and telemetry
flush succeeds. It does not claim that the Job exited successfully or that
Azure Monitor later stored the trace.

## Image and Job

`scripts/azure/build-foundation-probe-image.mjs` requires a clean Git tree,
derives the commit and Git tree from `HEAD`, recomputes the infrastructure
input hash from tracked `infra/bicep` files, and verifies the image labels with
Docker inspection. The local image config digest is not a registry manifest
digest. The CI build is local-only and never pushes. A registry digest can be
recorded only by the explicit verify-published path after pulling and inspecting
an already-published image; the tool cannot accept a caller-provided digest.

The Kustomize overlay adds a single Job and scoped Cilium egress policy to the
existing base. The Job has `backoffLimit: 0`, a 420-second deadline, a
read-only target ConfigMap mount, a named Monitor Secret reference, a projected
workload identity, and a non-root/read-only-root-filesystem security context.
The image runs as numeric UID/GID 10001, matching the Job's `runAsUser` and
`runAsGroup`; the numeric values let kubelet enforce `runAsNonRoot`.
Image digest, target ConfigMap, Monitor Secret, and egress host values remain
deployment-specific placeholders. Rendering them locally is not deployment
authorization or evidence.

FQDN egress uses both Cilium `rules.dns` inspection for the exact allowed host
names and matching `toFQDNs` destinations. AKS requires ACNS Container Network
Security to enforce FQDN filtering
([Microsoft Learn](https://learn.microsoft.com/en-us/azure/aks/how-to-apply-fqdn-filtering-policies)).
The current v1 AKS template uses API `2024-09-01` and enables ACNS security
without enabling observability; that separately owned infrastructure update
landed in #1799. This probe issue does not modify AKS infrastructure or incur
additional ACNS cost.
The acceptance consumer performs a read-only ARM GET of the exact AKS resource
ID from a successful source-bound deployment receipt (API version `2024-09-01`)
and reports this configuration gate as passed only when the observed resource
has Kubernetes 1.29 or later, `networkDataplane: cilium`,
`advancedNetworking.enabled: true`, and
`advancedNetworking.security.enabled: true`. Template settings or installed
CRDs are not substitutes for this observed-resource check, and passing it does
not replace the mandatory workload, image, Job, Monitor, or cleanup evidence.

The SQL migration
[`001_probe_effects.sql`](../../tools/Agentweaver.FoundationProbe/schema/001_probe_effects.sql)
creates the probe-owned effects table and grants only its required SELECT and
INSERT permissions to the pre-created runtime role. A separate migration
principal must apply it before a real run. The role's existing outbox/inbox
permissions remain governed by the approved PostgreSQL runtime-grants
procedure; no blanket table grant is introduced here.

The guarded operator fixture registers the exact Probe service principal in
`postgres`, where Azure PostgreSQL exposes its Entra principal functions.
It applies the canonical effects and embedded persistence migrations in one
transaction in `agentweaver`.
Principal registration and fixture creation are separate database phases.
A retry after fixture failure verifies the exact existing non-admin principal
and safe role attributes before it creates a missing schema.
Different principal mappings or incompatible fixture state block the operation.

## Acceptance consumer and validation

The in-pod output is only one evidence source. The external acceptance consumer
owns Job/pod identity, completion and exit code, immutable registry digest, and
fresh post-completion Monitor ingestion. Issue #1801 adds that consumer in
[`verify-acceptance.mjs`](../../scripts/azure/verify-acceptance.mjs). It reads
the exact Job, its pod, the ServiceAccount, and an immutable target ConfigMap.
It checks Job ownership by UID, the bounded one-shot deadline, pod namespace,
service account, projected workload-identity token, and termination exit code.
It obtains the expected manifest digest through the existing registry verifier
and requires the Job image, pod image, and observed pulled image ID to match.
The local Docker config digest remains provenance only, not a manifest digest.

The immutable target ConfigMap must match the reviewed source SHA, Git tree,
infrastructure hash, deployment outputs, identity, and exact runtime
configuration. The native receipt preserves issuer/subject/audience correlation,
provider pins, exact read-only Key Vault version redemption, owned Blob
generation and ETag cleanup, and the committed PostgreSQL effect, inbox, and
outbox outcomes. The pod receipt does not attest to its UID, image pull, or exit.
After external Job completion is observed, the consumer queries
`union AppDependencies, AppRequests` for `Name == "foundation-probe"`, exact
`OperationId` trace ID and `Id` span ID, and the `probe.source_sha`,
`probe.source_tree`, and `probe.nonce` properties. The Git tree remains a
40-hex SHA; the nonce is exactly 32 lowercase hex. It does not use `AppTraces`
or a dashed UUID nonce.

Collection requires an explicit read-only CLI option and exact Kubernetes
context. It does not create resources, run commands inside a pod, or perform
live reads during source validation. A source-only consumer change does not
deploy the probe or complete the wider P0 shipping checklist.

Credential-free validation includes:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
dotnet test tests\Agentweaver.FoundationProbe.Tests\Agentweaver.FoundationProbe.Tests.csproj --no-build --no-restore --configuration Release
npm run test:azure
kubectl kustomize deploy\k8s\acceptance\foundation-probe
node scripts\azure\build-foundation-probe-image.mjs
```

The .NET suite includes a disposable local Testcontainers PostgreSQL instance.
These commands do not provision Azure, read a live secret, deploy a Job, or
publish an image. CI performs the local image build and Kustomize render and
uploads the image receipt alongside its existing local-image artifacts.
