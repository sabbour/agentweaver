# Validate the Azure environment

Use this article to distinguish local source checks from checks against an
approved Azure deployment. Bicep compilation and Kubernetes rendering validate
the checked-in configuration; neither proves that Azure resources or services
are deployed. Deployment and smoke acceptance are separate, approved operations.

## Prerequisites

Before you run a live check, obtain approval for the target subscription,
tenant, source revision, and any resource changes. Install Azure CLI, Bicep,
`kubectl`, and Node.js 24. Use the guarded
exact-source procedure in the
[Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md);
do not replace its receipt and target checks with an ad hoc deployment command.
The separately approved
[session-material container repair](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#session-material-container-repair)
is limited to that container and its Events identity grant; it does not replace
the guarded foundation workflow or establish acceptance evidence.

The examples in this article use the requested environment identifiers.
`agentweaver-aks` is the Azure Kubernetes Service (AKS) cluster name:

| Resource | Example identifier |
| --- | --- |
| Resource group | `agentweaver` |
| AKS cluster | `agentweaver-aks` |
| Product namespace | `agentweaver` |
| Gateway namespace | `agentweaver-gateway` |
| Knowledge Memory resource ID | `agentweaver.knowledge` |
| Public portal host | `portal.<managed-domain>` |

These names identify the intended target for the examples. Confirm the
subscription and source-bound deployment receipt before using them.

## Run the offline source checks

Run these commands from the repository root:

1. Test the Azure deployment tooling:

   ```powershell
   npm run test:azure
   ```

2. Compile the Bicep entry point:

   ```powershell
   az bicep build --file infra\bicep\main.bicep --stdout
   ```

3. Render the product and acceptance Kubernetes configurations:

   ```powershell
   kubectl kustomize deploy\k8s\base
   kubectl kustomize deploy\k8s\migrations\identity-broker
   kubectl kustomize deploy\k8s\migrations\p1-postgres
   kubectl kustomize deploy\k8s\acceptance\foundation-probe
   ```

The commands must complete without validation errors. They do not contact
Azure, apply SQL, create identities, execute an operator bootstrap, or prove a
running service. Confirm the migration egress policy renders the approved
PostgreSQL FQDN and TCP 5432 rule without a `CHANGEME` host.

The PostgreSQL migration overlay contains the Orchestrator and Events & Sessions Jobs.
The Events & Sessions Job repeats its runtime Sessions and Messaging settings
because the host validates them before it enters `--migrate`.

The Identity runtime-table compatibility tests use disposable PostgreSQL
principals. They check the source-defined grants for compare-and-swap updates
and append-only audit writes. They do not create Azure identities or apply
those grants to a deployed database. Passing these tests is not Foundation
Probe evidence or live SDK accounting acceptance.

## Deploy or update the approved target

Use the guarded exact-source command from the operator guide. The installer
binds the approved subscription, tenant, resource group, source SHA, source
tree, and input hash. The AKS API must be public:
`apiServerAccessProfile.enablePrivateCluster` must be `false`. A public DNS
name does not make a private AKS API public.

For an existing environment, use the guarded AKS-only path to refresh the three
existing workload-identity federation records. It reads the cluster issuer and
managed routing-domain state but does not PUT AKS or change its configuration.
It also does not redeploy PostgreSQL, Key Vault, Blob Storage, the VNet, private
DNS, or Monitor. Supply the approved infrastructure inputs only; the deployment
does not take an operator object ID for Kubernetes access.

The tooling never creates, requires, or verifies a human Azure Kubernetes
Service RBAC Cluster Admin assignment. Provision cluster connectivity and
Kubernetes API permissions outside the tooling. The cluster-dependent bootstrap
steps check the access they need and stop before writing resources when it is
missing. An AKS-only receipt is scoped to the federation refresh; it does not replace the
verified full-foundation resource receipt required for Foundation Probe
acceptance. The Probe image and infrastructure deployment have separate source
receipts.

The full-foundation template configures the system node pool to scale
automatically between two and three `Standard_D2s_v5` nodes, starting with two.
The AKS-only installer validates these bounds against the approved foundation
parameters but does not read or configure the pool. Its receipt has no submitted
node count and does not prove that the pool scaled. Available vCPU quota also
does not prove that the region has VM capacity.

The AKS API uses managed Microsoft Entra ID authentication and Azure RBAC,
with local accounts disabled. The deployment keeps PostgreSQL, Key Vault,
Blob Storage, VNet/private endpoints, and Monitor in their approved target
and region.

The foundation creates the private `platform-artifacts` and `session-material`
Blob containers. The Foundation Probe keeps its account-scoped Blob data role;
Events & Sessions receives Storage Blob Data Contributor on
`session-material` only. These definitions do not confirm live resource or
role-assignment state.

## Check the deployed environment

Run live checks only after confirming the current Azure subscription and
Kubernetes context:

1. Confirm the Azure subscription and cluster context. Read the actual AKS
   properties and verify that the API is public, managed Entra and Azure RBAC
   are enabled, and local accounts are disabled.

   ```powershell
   az account show --query "{subscription:name, tenant:tenantId}" --output table
   kubectl config current-context
   az aks show --resource-group agentweaver --name agentweaver-aks
   ```

   Expected result: the active subscription and Kubernetes context identify
   the approved target. The AKS properties show a public API, managed Entra
   authentication, Azure RBAC, and disabled local accounts.
2. Check workload rollout and health in both namespaces:

   ```powershell
   kubectl get deployments,services,pods -n agentweaver
   kubectl get deployments,services,pods -n agentweaver-gateway
   ```

   Confirm that the expected workloads are ready and that their image
   references match the approved deployment receipt. These observations
   confirm only the resources you queried.
   For public routing, verify that the managed Gateway has one programmed
   wildcard-domain HTTPS listener and that the Broker, Web, and Gateway API
   HTTPRoutes retain their exact hostnames, paths, and backends on it. The
   checked-in release/Kustomize composition also includes MCP compatibility
   resources, but their presence does not make MCP a supported product or
   acceptance surface. Outbound remote MCP connections are separate. A Portal
   root response alone does not prove that the Broker authorization route
   works.
3. Exercise one authorized Identity secret-redemption request and one denied
   request. Confirm that the authorized request follows the owner's success
   contract and the denied request is rejected. Do not log credentials or
   returned secret values.
4. Use a small generated fixture to test an exact-version Key Vault read, a
   Blob Storage round trip, and PostgreSQL behavior. Confirm that cleanup
   removes only data created by that fixture, and compare each response with
   the fixture's expected value.
5. Confirm the relevant telemetry when the deployed service integration
   requires it.

Record concise, sanitized results and the observed source and image versions.
Preserve the external data services and all resources outside the approved
fixture. Mark checks that you did not run as not run; do not infer a pass from
source tests or rendered manifests.

## Collect optional Foundation Probe evidence

The `foundation-probe` Job and
`verify-acceptance.mjs --collect-runtime-evidence` command provide supplemental
evidence. The verifier observes the Job, pod, image, workload identity,
native deployment receipt, and correlated Azure Monitor records. It does not
perform the Identity Broker grant-redemption smoke check and is not a separate
shipping gate.

The verifier compares the Probe image with its exact build source. It also
compares each native deployment receipt with its source, input hash, resource
IDs, and deployment scope. An AKS-only receipt cannot stand in for the
full-foundation receipt, and neither image evidence nor infrastructure
evidence replaces the other. The existing P0 deployment preflight does not enumerate Azure resources or verify their
post-deployment properties.

See [Test Agentweaver](./testing) for the limits of source and integration
tests.

## Run the native Workspace repository-read check

This check uses the native .NET CLI to read one file from a configured
repository. It is separate from the Foundation Probe and Azure deployment
checks. It does not create an Agentweaver project, run, role, or test fixture.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-native-workspace-acceptance.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-native-workspace-acceptance.png'" alt="The native .NET CLI opens an exact Workspace review in the browser. The Broker checks its current cookie and antiforgery token, creates an owner-bound Repo App selection, and returns it only after HTTPS code exchange with an S256 verifier. The CLI then reads refs, a tree and a file through typed Gateway operations with current owner checks. The selection is not part of the callback, OAuth tokens, logs or evidence." />
  </a>
  <figcaption>This native source check is separate from the Foundation Probe and Azure deployment checks. It does not prove a live browser, deployment, or the full acceptance journey.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-native-workspace-acceptance.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-native-workspace-acceptance.drawio'">Open editable draw.io source</a></p>

Run it only with an approved non-production target, a signed-in owner who can
read the project, and the exact configured Repo App connection and repository.
The check performs normal OAuth sign-in when needed. It also creates a
short-lived Broker selection record for each repository read. It does not
change or delete GitHub repository data. Do not run it against production.

Prepare a valid `workspace-repository-read` manifest from the current target.
Use the source, deployment, and proof inputs required by the standard API
acceptance manifest. Set `fixturePlan.workspace` to the exact project revision,
Repo App connection and revision, installation, repository, branch or tag,
immutable commit, file path, UTF-8 byte length, and SHA-256 digest. The file
must be a regular text file no larger than 1 MiB. The digest must cover its
original UTF-8 bytes without newline or Unicode normalization. Do not add a
selection code or credential to the manifest.

From PowerShell at the repository root, set these values:

```powershell
$cli = "<path to the exact self-contained Agentweaver apphost>"
$manifest = "<path to the approved Workspace manifest>"
$evidence = "<new path for Workspace evidence>"
```

Replace each placeholder with its full path. Use the apphost published from
this source, or the exact extracted apphost when checking a distribution
candidate. The evidence path must not exist and must not replace the manifest
or any proof file.

First validate the input without network access:

```powershell
& $cli test --input $manifest --output $evidence --dry-run --interactive
```

A successful dry run reports that the inputs are valid and does not create the
evidence file. It does not open a browser, start a callback listener, sign in,
or contact the target. Fix validation errors locally before proceeding.

After confirming the target and effect scope, run the approved check:

```powershell
& $cli test --input $manifest --output $evidence --interactive
```

When the Broker asks, sign in as the expected owner and review the exact
repository and read operation. The CLI requests a fresh review for each ref
page, tree read, and file read. The check uses one immutable commit for the
tree and file. It does not create API fixtures or call model, run, or Sandbox
operations.

The command reports `featureStatus` as `passed`, `failed`, or `blocked`.
`fullP1Status` remains `not-evaluated`. Check the new evidence file and the
reported result. The evidence contains bounded file metadata and digests, not
the file text or Repo App selection. This result proves only the selected
Workspace read on the target used. It does not prove denied-actor parity,
Azure deployment, or the full acceptance journey. Record any check you did not
run as not run.

## Expected result

You have recorded each check as passed, failed, or not run against the
approved source and deployment receipt. Source checks and Foundation Probe
evidence remain distinct from live user-workflow acceptance.
