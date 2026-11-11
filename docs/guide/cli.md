# Agentweaver CLI

The Agentweaver CLI is a native .NET 10 executable named `agentweaver`.
Its source project is version `1.0.0`. Downloadable archives are not yet
available: archive preparation is separate from release publication, so do not
treat a CI candidate as a published download.

The intended archives bundle the .NET runtime. Running an extracted archive
will not require Node.js, a .NET runtime or SDK, or an Agentweaver source
checkout. Supported candidate targets are Windows `win-x64`, glibc Linux
`linux-x64` (not Alpine/musl), and Apple Silicon macOS `osx-arm64`. The
archive format is ZIP on Windows and tar.gz on Unix-like systems. Linux sign-in
also requires `libsecret` and an available, unlocked Secret Service session.

## Build and run from source

Building requires the .NET 10 SDK selected by `global.json`. From the
repository root, publish a self-contained executable for the current host:

```powershell
dotnet restore tools\Agentweaver.Cli\Agentweaver.Cli.csproj --locked-mode
dotnet publish tools\Agentweaver.Cli\Agentweaver.Cli.csproj --configuration Release --no-restore --runtime win-x64 --self-contained true -p:UseAppHost=true -p:PublishTrimmed=false -p:PublishAot=false -o artifacts\cli\win-x64\publish
.\artifacts\cli\win-x64\publish\agentweaver.exe --help
```

For Linux or macOS, use `linux-x64` or `osx-arm64` for the runtime identifier
and output directory, then run `agentweaver` from that publish directory.
Publish and test each target on its native platform. A local publish directory
is a build output, not a released archive.

The source-bound Windows archive verifier runs controlled checks against the
exact extracted apphost. See [Windows archive checks](./testing.md#windows-archive-checks);
these checks require an explicit attended Windows trust action. This is a
verifier option, not a product TLS option, API acceptance, or release
publication evidence.

When release archives are available, choose the archive matching the host,
extract it, and place the executable directory on `PATH` or invoke the
executable by path. No download URL is listed until a release is published.

## Configure a target

`api operations` and `api describe` use only `AGENTWEAVER_GATEWAY_URL`;
authentication settings are ignored by these discovery commands.
`api invoke`, `rest`, and all `auth` commands require the Gateway URL, Broker
issuer, API audience, and tenant. The optional project and run selectors for
those commands must be supplied together. These settings identify the target;
never pass access or refresh tokens as command-line arguments.

For `auth` and API commands, the nonsecret target options override their
matching environment variables: `--gateway-url`, `--broker-issuer`,
`--client-id`, `--api-audience`, `--tenant-id`, `--project-id`, and `--run-id`.
Discovery still uses only the Gateway URL, whether supplied by environment or
`--gateway-url`. Keep project/run overrides paired. `test` accepts none of
these target flags and does not fall back to target environment variables; its
target comes from the manifest.

```powershell
$env:AGENTWEAVER_GATEWAY_URL = "https://gateway.example"
$env:AGENTWEAVER_BROKER_ISSUER = "https://identity.example"
$env:AGENTWEAVER_API_AUDIENCE = "https://gateway.example"
$env:AGENTWEAVER_TENANT_ID = "TENANT_ID"
# Optional; defaults to agentweaver-cli.
$env:AGENTWEAVER_CLIENT_ID = "agentweaver-cli"
# Optional for auth/API calls; set both when selecting a project and run.
$env:AGENTWEAVER_PROJECT_ID = "PROJECT_ID"
$env:AGENTWEAVER_RUN_ID = "RUN_ID"
```

These environment selectors do not set the acceptance runner's project or run.
The runner creates and binds its own fixture within each `test` invocation.

For one-off discovery, only the Gateway URL is needed:

```powershell
agentweaver api operations --gateway-url "https://gateway.example"
```

For sign-in, command-line options override matching environment values:

```powershell
agentweaver auth login --gateway-url "https://gateway.example" --broker-issuer "https://identity.example" --client-id "agentweaver-cli" --api-audience "https://gateway.example" --tenant-id "TENANT_ID"
```

The statically registered public client uses authorization code with S256
PKCE and the exact loopback callback
`http://127.0.0.1:49173/oauth/callback`. The Broker must have this client
registration and be configured for the selected Gateway audience before
sign-in can work. The checked-in Broker configuration is source only. Updating
its immutable ConfigMap and rolling out the Broker are separate, explicitly
authorized operations; building the CLI does neither.

## Sign in and manage local credentials

```powershell
agentweaver auth login
agentweaver auth status
agentweaver auth logout
```

`login` opens the Broker's browser sign-in. `status` reports whether the
selected identity is signed in and safe metadata: actor, issuer, client,
audience, Gateway, tenant, scopes, and project/run selectors. It never prints
tokens. The CLI keeps access tokens in memory and stores only refresh tokens
in the host's native credential store: Windows Credential Manager, macOS
Keychain, or Linux Secret Service. If the secure store is unavailable,
sign-in fails closed; there is no plaintext-file fallback.

The native consent page keeps a restrictive content security policy and allows
form redirects only to the exact registered CLI callback, in addition to the
same-origin consent submission.

The default requested scopes are `openid api.read offline_access`.
`projects.admin` and `projects.orchestrator` are requested only when explicitly
needed for the selected operation or journey. A scope does not grant a project
role; the Gateway checks current actor, tenant, project, run, and role
authority. For the consent meanings, see [Identity Broker permissions](./identity-scopes.md).
Run selection uses unbound Orchestrator scope before a run exists; the
acceptance runner then reads the selection back and obtains authorization
bound to that exact project and run before run operations.

`logout` confirms removal of only the selected local credential entry. It does
not revoke the Broker's server-side refresh-token family or clear access tokens
already held by another running process. If a refresh outcome is uncertain,
the CLI requires sign-in again rather than retrying a possibly consumed refresh
token.

## Manage a Copilot connection

The Copilot commands use an unbound `projects.admin` identity. The Broker checks
current `WriteProjects` authority for a project connection or
`WritePlatformRuntimeDefaults` authority for a platform connection. These
permissions also require `api.read`. Sign in with `projects.admin` before you
use the commands:

```powershell
agentweaver auth login --scope projects.admin
agentweaver copilot status --scope project --scope-id PROJECT_ID
agentweaver copilot connect --scope project --scope-id PROJECT_ID
agentweaver copilot refresh --connection-id CONNECTION_ID
agentweaver copilot disconnect --connection-id CONNECTION_ID
```

Use `--scope platform` for a platform connection. Its scope ID is `default`,
so you do not need `--scope-id`. For a project connection, pass the exact
project ID with `--scope-id`.

The `status` command returns at most 50 connections for the selected scope.
If the response has a `nextAfterConnectionId` value, pass it with
`--after-connection-id` to read the next page:

```powershell
agentweaver copilot status --scope project --scope-id PROJECT_ID --after-connection-id CONNECTION_ID
```

The CLI checks the current authorization context before it sends a Copilot
request. It ignores `AGENTWEAVER_PROJECT_ID` and `AGENTWEAVER_RUN_ID` because
Copilot commands require an unbound identity. Do not pass `--project-id` or
`--run-id`. The `--scope` and `--scope-id` values select the Copilot connection;
they do not bind the bearer.

`connect` opens GitHub in your browser and waits for the callback on
`http://127.0.0.1:49173/oauth/callback`. The browser returns through the
callback page on the Broker's configured portal origin. This origin can differ
from the Gateway URL. That page sends the protected state to the Gateway and
redirects only the authorization code or denial result to the loopback
listener. The CLI keeps the callback state, code, and Broker cookie in memory.
It sends the cookie only with the completion request. GitHub access and refresh
tokens stay in the Broker's secret store and are not returned to the CLI.

`refresh` and `disconnect` first read the current connection revision. The
Broker rejects the change if another request changes that revision first.
`disconnect` revokes the connection. `status` and these changes return safe
connection details; they do not return GitHub credentials.

## Discover and invoke API operations

The CLI reads the currently served Gateway `GET /openapi/v1.json` document for
operation discovery. It does not maintain a second route table or an SDK:

```powershell
agentweaver api operations
agentweaver api describe getProject
agentweaver api invoke getProject --path projectId=PROJECT_ID
```

Invocation accepts only parameters described by that operation:

```text
agentweaver api invoke <operationId> [--path name=value] [--query name=value] [--scope <scope>] [--body <JSON|@file>] [--header name=value] [--output <new-file>]
```

`--output` is for an advertised binary response and must name a new file.
Server-sent events stream as they arrive and stop on cancellation. The CLI
preflights the current authorization context and applies same-origin,
redirect, and reserved-header guards. It does not accept caller-supplied
bearer tokens or project-authorization headers. When an operation advertises
`X-Agentweaver-Project-Authorization`, the CLI obtains the separate,
unbound `projects.admin` authorization for the same actor and tenant.

The invocation setup receipt operations are `createP1InvocationSetup` (POST) and
`readP1InvocationSetup` (GET), both on
`/api/v1/p1/invocations/{invocationId}/setup`. They use the separate unbound
`api.read` plus `projects.admin` identity and still require current TenantAdmin
authority. For the setup receipt flow:

```powershell
agentweaver auth login --scope projects.admin
agentweaver api invoke createP1InvocationSetup --path invocationId=<INVOCATION_ID> --scope projects.admin
agentweaver api invoke readP1InvocationSetup --path invocationId=<INVOCATION_ID> --scope projects.admin
```

The create operation mutates the owned project fixture; use it only in an approved
setup context. These individual calls do not replace the native
`agentweaver test --input <manifest>` acceptance journey. The operations remain
excluded from MCP.

The operation list includes supported public operations in the served Gateway
catalog even when they are not admitted to MCP. Identity Connection browser
BFF routes such as `/api/connections/...` are not in this catalog; this is not
full browser or BFF parity. See the [HTTP API reference](../reference/api.md#cli-operation-discovery).

## Make a direct Gateway request

`rest` sends explicit HTTP methods to root-relative URLs or URLs on the
configured Gateway origin, including paths not listed in OpenAPI. It does not
fetch OpenAPI and is not proof of operation-catalog, browser, or BFF parity.

```text
agentweaver rest --method GET|HEAD|POST|PUT|PATCH|DELETE|OPTIONS --url <root-relative-or-same-Gateway-URL> [target options] [--scope <scope>] [--body <JSON|@file>] [--header name=value]... [--output <new-file>]
```

The target options are `--gateway-url`, `--broker-issuer`, `--client-id`,
`--api-audience`, `--tenant-id`, `--project-id`, and `--run-id`; each overrides
its matching environment variable, and project/run selectors stay paired.
Authentication uses the selected primary bearer and validates current issuer,
tenant, project, and run binding with the fixed
`/api/v1/authorization/context` preflight before sending the request. The
Gateway remains authoritative for access; raw requests do not use dual-
credential escalation or retry. `--scope` selects an existing scope; it does
not grant roles or bypass Gateway authorization.

An absolute URL must match the configured Gateway origin; a root-relative URL
resolves under that Gateway. Userinfo, fragments, backslashes, off-origin
URLs, and credential-like query keys are rejected before authentication or
network access. Repeated `--header` options accept custom headers but reject
sensitive or reserved headers, including authorization, cookies, tenant and
correlation selectors, Host, proxy, framing, forwarded, method-override, and
Content-Type headers. Invalid CR/LF header values are rejected too. The
URL and header rejections happen before authentication or network access. The
command does not follow redirects, forward bearer tokens to another host, or
retain or send cookies.

`--body` accepts valid JSON up to 1 MiB. Inline JSON and `@file` bytes are sent
without JSON reserialization. Omitting `--body` sends no content; `--body null`
or a file containing `null` sends the four UTF-8 bytes `null` with JSON
Content-Type.

The command decodes `application/json`, `application/problem+json`,
`text/plain`, and `text/event-stream`; SSE streams through redaction and
cancellation. If `--output` is supplied for one of these recognized media
types, the command fails as soon as response headers identify the type; it
does not buffer an SSE stream before refusing the output option.
`application/octet-stream` and other or missing content types are opaque
bytes, never decoded to the terminal. Opaque responses are read under the
response-size bound; nonempty opaque bytes require `--output`, which creates
a new file without overwriting and is valid only for nonempty opaque bytes.
An empty opaque response reports an empty-output error. Supplying `--output`
for JSON, text, SSE, or an empty response is an error. Empty HEAD and 204
responses succeed without an output file. HEAD and 204 ignore advertised
`Content-Length` for size checks; actual reads remain bounded to 8 MiB.

The CLI returns its JSON status/evidence wrapper; decoded text and JSON bodies
are redacted. Opaque responses report the output path and byte count. Raw
response headers are not surfaced (`headers` is empty). Exit codes are `0`
for 2xx, `1` for other statuses or failures, and `130` for cancellation.

## Configure a BYOK model binding

`models byok` plans an Azure OpenAI BYOK model binding. It does not need a
target or a sign-in.

```text
agentweaver models byok prices --region <azure-region> --search <meter-text>
agentweaver models byok plan --input <byok.json> --source <deploy/k8s> --output <new-plan-file> [--overlay-output <new-model-bindings-file>]
agentweaver models byok apply --plan <plan-file>
```

`prices` lists Azure Retail Prices meters in USD per million tokens. `plan`
reads the input file and the runtime maps, and writes a new plan file. With
`--overlay-output`, `plan` also writes a model bindings file. Give this file
to `npm run deploy:kustomize -- --model-bindings <file>`. Keep it outside the
repository. `apply` writes the planned maps to the deployment source only
when every file still has its planned digest. Do not use `apply` for an
environment binding. The
input contains a Key Vault secret name and version, never an API key. For the
full procedure, see
[Configure an Azure OpenAI BYOK model](./configure-byok-azure-openai.md).

## Run API acceptance

The acceptance project launches the exact apphost for `--version`,
`api operations` discovery against locally emitted Gateway OpenAPI, and
`test --dry-run` with a valid manifest. The dry-run uses the production
`Program` composition, but performs no authentication or network request and
does not create evidence, even with `--interactive`. Journey tests also call
`CliApplication.RunAsync` in process. A supplemental spawned
`Agentweaver.Cli.Acceptance.TestHost` exercises the application with
controlled providers and local HTTP; it is not the shipped apphost or proof
of production authentication. None of these checks is an authenticated
mutation journey through the production apphost.

The production evidence provider verifies the committed source manifest, the
selected registry images, and Kubernetes service readbacks. GHCR is the
default registry. For an approved Azure Container Registry (ACR), set its
lowercase login server in `deploymentEvidence.registry`. The CLI uses the
signed-in Azure CLI identity to request a repository-scoped pull token; the
token stays in memory and is not written to output or evidence. ACR images
must match the pinned source commit, Git tree, and component version. A
non-dry-run needs the exact external source manifest and image manifest, and
may change API state during fixture setup and cleanup; do not use it against
production.
The `native-material-recovery` journey supplies positive session-material
evidence only when its turn, material, replacement, replay, and cleanup
receipts all validate. Full API acceptance can still be blocked when other
required probes or authorized target evidence are unavailable. The manifest
and evidence are external files; relative proof paths resolve from the
manifest directory.

`deploymentEvidence` contains `manifestPath` and `imageManifestPath`. It can
also contain `registry`. If you omit `registry`, the CLI uses
`ghcr.io/sabbour`. Set `registry` to an approved ACR login server, such as
`<name>.azurecr.io`, to verify ACR images. For a live ACR test, sign in with
Azure CLI and use an identity that already has pull access. The CLI does not
create roles or credentials.

The separate `board-delete` journey is a narrow typed API acceptance feature,
not a complete API acceptance run. Its manifest uses `executionIntent: "execute"`,
`journey: "board-delete"`, and `fixturePlan: {"kind":"owned-disposable-board"}`.
The reviewed environment is fixed: `baseUrl` is
`https://api.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io`,
`issuer` and `audience` are both
`https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/`, and
`tenantId` is `72f988bf-86f1-41af-91ab-2d7cd011db47`. The Gateway and Broker
origins are deliberately distinct. `actorId` is optional: when omitted, the
runner derives it from the live authorization context before writing anything;
when supplied, it must match that context.

Pins use Kubernetes `context: "agentweaver-aks"` and
`namespace: "agentweaver"`. `sourceReceipt` must contain the Gateway source
SHA `fa4c10d40b10b5f06c6333ecfb6e6d86a6a7a8fe` and tree
`e235a0f576a26924920130c0c4bb4dbd01d4d567`. `orchestratorSourceReceipt`
contains the exact Orchestrator commit and tree selected for the rollout.
`authoritySourceReceipt` must contain source SHA
`1471e0b6ee4ab2a859231582ed1efa8e86693002` and tree
`5846e8524d8d684f78d24518453a4e1348fa7bf4`. The image manifest must contain
exactly these four immutable digests:

| Image key | Digest |
| --- | --- |
| `agentweaver.gateway` | `sha256:0fa6e9f718b06a030172deda8ee0e509871b8bd10e9e8a74c13a37cbd8780dcd` |
| `agentweaver.orchestrator` | The selected immutable digest, verified against `orchestratorSourceReceipt`. |
| `agentweaver.identity.broker` | `sha256:d76177945cf05121120f8fbc6b4d7fe708f0c4e6adbbafbcafe449f52ce7e0b4` |
| `agentweaver.projects.config` | `sha256:33f1af287e19ac8854952dc379e685397cda2e5068b7734dc35c83f4a32b3378` |

The provider keeps Gateway pinned to `sourceReceipt`, checks Orchestrator
against `orchestratorSourceReceipt` and its OCI source labels, and checks
Identity Broker and Projects Config against `authoritySourceReceipt`. The
three retained owner image digests cannot change. Projects Config returns
project IDs as 32-character GUID strings; the runner retains the exact receipt
string for every owned operation and cleanup request. The current CLI apphost
must also carry `AgentweaverSourceSha` and
`AgentweaverSourceTree` metadata. The provider verifies these owners; it does
not rebuild or redeploy the services.

The runner creates a uniquely named project, grants only its owner role to the
resolved actor, exercises fixed backlog operations and expected owner conflicts,
then verifies persisted deletion readbacks. Cleanup deletes only tasks recorded
as owned by that invocation, archives the project, and revokes only its owner
assignment. An uncertain mutation or cleanup result fails closed and must be
reconciled before retry. The runner reserves the evidence path exclusively
before deployment-provider reads or authentication. An existing unfinished
Board reservation is not resumable because the in-memory fixture receipt ledger
is not persisted; the runner leaves that evidence untouched and rejects the
retry before any owner setup. Reconcile the original invocation and use a new
evidence path for a new attempt. The existing full-journey reservation behavior is
unchanged. The result uses `board-acceptance-result` with
`fullP1Status: "not-evaluated"`; it does not claim complete API acceptance.

The `owner-denial` step passes only when the response is HTTP `403` and its
JSON `error` field is `backlog_project_permission_denied`. The result preserves
that exact value as `ownerIssueCode`. A generic Gateway problem with
`code: "forbidden"` or a bodyless `403` is not an owner denial. The runner
rereads the board and requires the authoritative graph to remain unchanged
after the rejected delete.

The separate `board-archived-denial` journey uses the same fixed Board
environment and source/image pins. It archives one invocation-owned task, then
requires deleting it to return HTTP `409` with `taskNotEditable` and an
unchanged persisted graph. Cleanup retains that archived task in the
invocation-owned project, archives the project, revokes its owner assignment,
and independently reads back the archived project and empty active assignment
list. Automation-held and claimed-task guards remain blocked without safe
no-model fixture and cleanup paths. Distinct-actor and revoked-role checks
remain blocked without a separate authenticated non-admin actor; TenantAdmin
authority cannot establish those denials.

The `journal` API journey checks the Events journal without dispatching work.
Omit `selectionContext` from `fixturePlan` to let Projects create a pending run
from current project configuration. The CLI uses the server-returned run and
root-session IDs, then reads back the selection and checks the expected platform
runtime revision. It obtains authorization bound to that exact project and
run before it admits the root. The server creates the raw selection context;
the CLI does not copy an effective selection or guess provider requirements.
Supplying `selectionContext` keeps the explicit raw-context path.

After it reads back the accepted root, the CLI sends one run-bound
`sendProjectRunMessage` request with an invocation-scoped idempotency key.
`replayRunEvents` must return nonempty, ordered pages with exact project and run
identity and advancing cursors. The message is a journal seed; it does not
start a workflow or dispatch a model. Before removing project authority, the
CLI cancels the owned run and independently reads the cancelled run, root
session, and session tree. It then revokes Orchestrator, archives the project,
revokes the creator's Owner assignment last, and reads back the archived
project and empty assignment list. A missing receipt or failed readback remains
indeterminate; the CLI does not retry an uncertain write or report cleanup as
complete.

The `session-history-metric` API journey creates two child history records
without starting a model turn. It completes and archives one child, detaches
the other, then reads the current session tree and `readProjectMetrics`. The
fixture expects exactly two history records (root plus archived child), one
accepted run, and zero runs in a date range that excludes that run.

For this journey, and for `journal`, omit `selectionContext` from `fixturePlan`
to create a real pending interactive run. The CLI uses the server-returned run
and root-session IDs and reads back the effective selection. Its benign
`firstMessage` does not start the run or dispatch work. Root acceptance still
uses normal runtime admission and budget checks; it is not a zero-cost or
admission-free path. Supplying `selectionContext` keeps the explicit
raw-context selection path. Both metric paths cancel the accepted run before
removing authority, then independently verify the terminal run, root, retained
session tree, and detached session at the advanced fence. Cleanup archives the
project before the final Owner revocation and reads back the archived project
and empty assignment list. An uncertain create, selection, cancellation, or
readback remains indeterminate; the CLI does not claim that the fixture was
never created or cleaned. The API journeys use the manifest actor and do not
run a cross-actor denial probe. `fixturePlan` rejects unknown fields, so older
manifests that include a second actor are invalid.

No native Workspace refs/tree/file journey is currently defined. The Gateway
read operations require the short-lived Repo App selection code in
`X-Agentweaver-Team-Sync-Selection`; do not put that code in the manifest or
bypass the authorized Repo App selection flow. Without an approved project,
repository, connection, and fresh selection code, Workspace acceptance is
blocked. See the [Workspace read contract](../deep-dive/source-control.md).

The image-manifest file uses the same sparse JSON contract as
`npm run deploy:kustomize`:

```json
{
  "images": {
    "agentweaver.gateway": "sha-<40-character-source-commit>",
    "agentweaver-agenthost": "sha256:<64-hex-digest>"
  }
}
```

Repository names are the canonical component image names in the Kustomize
deployment guide. The registry comes from `deploymentEvidence.registry`, not
from this image manifest. Each value is either a source-commit tag or an
immutable digest. Selected tags are resolved to digests, and every selected
reference must match the live workload. Omitted entries are read from
Kubernetes and keep their current registry. That registry must be
`ghcr.io/sabbour` or an approved ACR login server. Omitted references must
remain immutable and match the exact pinned source. All observed images are
verified against their registry manifest,
platform, config digest, and exact source revision; a present source-tree
label must match the pinned Git tree. ACR also requires the pinned component
version label. For AgentHost, the CLI checks the `linux/amd64` platform
manifest and compares the compressed config and layer sizes with the
Environment Deployment and Pod settings. Missing or inconsistent size
evidence blocks the provider; it is never replaced with zero or a guessed
value. Caller-supplied image receipts and local OCI archives are not accepted.

```powershell
agentweaver test --input manifest.json --output evidence.jsonl
```

The command accepts the runner's pinned manifest contract and optional
envelopes:

```text
agentweaver test --input <manifest> --output <evidence> [--native-probe-envelope <file>] [--dispatch-recovery-envelope <file>] [--dry-run] [--interactive]
```

For API acceptance journeys, the manifest's `environment` object must provide `baseUrl`
(Gateway origin), `issuer` (Broker/OIDC issuer), `audience`, `tenantId`, and
`actorId`. The runner builds its target from those fields and uses the
`agentweaver-cli` client; it does not substitute target flags or environment
variables. For each API acceptance invocation, the runner creates its owned project and
takes its ID from the `createProject` receipt. Journeys with an explicit
`selectionContext` derive run and root-session IDs from the invocation. The
`journal`, `session-history-metric`, and `native-material-recovery` journeys
may omit that context and then use the run and root-session IDs returned by
`createProjectRun`. The `full-flow-delivery` journey must omit it. The
`board-delete` target and input contract are separate and described above.

Interaction is disabled by default (`InteractionPolicy.Never`). Only
`--interactive` allows browser authorization. For a test run, the CLI first
sets up its owned fixture and observes the selected run, then obtains
authorization bound to both that exact project and run. Setup and run
authorization must identify the same actor and tenant. An unbound token or
refresh cannot stand in for exact-run authority.

When a journey accepts a coordinator root and its first message, Orchestrator
can report `runtimeStartup.state` as `pending` while it starts AgentHost in the
background. Read the run's session status to check whether startup is still
pending, ready, blocked, or cleaning up. Starting AgentHost does not send a
model turn. A controlled CLI journey or a `pending` result does not prove that
the Host is ready.

The `native-material-recovery` journey uses the Projects P1 setup receipt and
the server-owned run selection. It sends one read-only casting request to the
approved Bring Your Own Key (BYOK) model. It reads the stored user and
assistant turn text, checks their hashes against the runtime completion
receipt, replaces the Host through the ordinary replacement operation, and
checks that the new Host restores the same saved SDK cache without model calls
or tool effects. It then sends the same request again and checks the
Orchestrator replay receipt and unchanged run state.

This journey does not test authority revocation, stale-binding denial,
changed-payload retry, optional-cache absence or expiry, or missing or expired
required turn material. A passed session-material row does not imply that
these separate checks or full API acceptance passed.

This journey performs a model request and a Host replacement. Use it only on
an approved non-production target, with an approved model and spend limit and
explicit approval for Host replacement. Do not use `--dry-run` as live
acceptance: it validates the manifest only and does not create evidence or
contact the target.

The `native-material-recovery` fixture plan must omit `selectionContext` and
contain exactly `expectedPlatformRuntimeRevision`, `nativeMaterialRecovery`,
`gate`, and `message`. Set `gate` and `message` to `null`. Use the current
platform runtime revision from the approved platform receipt.

The `nativeMaterialRecovery` object must contain exactly these fields:

| Field | Required value |
| --- | --- |
| `approvedModelSelectionReference` | The approved model selection reference |
| `approvedModelSourceMode` | `byok` |
| `approvedReasoningEffort` | The approved reasoning effort |
| `approvedMaxPromptTokens` | The approved positive prompt-token limit |
| `approvedNativeRequestCount` | `1` |
| `allowOrdinaryHostReplacement` | `true` |

The runner rejects a model source other than `byok`, a request count other than
`1`, or a false replacement approval. A passed session-material result also
requires the ordinary project and run cleanup receipts. Other required probes
or deployment evidence can still keep full P1 acceptance blocked.

For API journeys that accept a root run, the CLI cancels the run during
cleanup. It then waits up to two minutes for the root session status to report
`runtimeStartup.state: "cleanup_complete"`. The CLI waits before it revokes
project roles or archives the fixture project. If cleanup does not complete,
or the CLI cannot read its status, the evidence records the failure and the
CLI stops before it removes access or archives the project. After all cleanup
receipts and readbacks pass, the cleanup state is `cleaned` and the result does
not include the provisional failure reason. Reconcile an incomplete or
indeterminate cleanup before you retry.

`--dry-run` validates the manifest without network calls, authentication,
browser launch, or evidence creation, even when `--interactive` is present.
The intended distributed runner does not need Node.js, a .NET SDK/runtime,
Git, `gh`, or a repository checkout. Source and image checks use read-only,
pinned GitHub and OCI evidence; deployed evidence also requires `kubectl`
configured for the operator-selected context. The CLI does not download or
execute repository code or forward Gateway credentials to GitHub, a registry,
or a cluster.

Source verification is bound to the committed `releases/foundation.json` in
`sabbour/agentweaver` at the exact pinned commit and tree. The supplied
manifest's local name or path cannot select a different repository or source
blob.

The expected result contract is `0` for passed, `1` for failed, and `2` for
blocked. A missing or denied exact-run authorization is blocked only if
owned-fixture cleanup succeeds. Failed or uncertain cleanup takes precedence
and returns failed; reconcile that invocation before retrying. Do not start a new
invocation or reuse the cleaned project/run while cleanup is unresolved. After
successful cleanup or reconciliation, a new attempt uses
`--interactive`, a new invocation identity, and a new evidence path. Native
probe evidence is not fabricated or dispatched by this CLI, so the overall
acceptance can remain blocked even when its API surface passes. Imported
`numeric-copilot-dispatch-boundaries` and
`checkpoint-restart-duplicate-fence` pass claims remain blocked until the
runner can verify source-owned proof for those probes. The CLI does not create
or require a second Broker account. Missing independent native probes can also
keep the overall acceptance blocked.

### Run the full-flow delivery journey

The `full-flow-delivery` journey runs casting, casting confirmation, a backlog
task, and a run-usage check with a real BYOK model. It does not replace the
Host. The [testing guide](./testing.md#run-the-full-flow-delivery-journey)
gives the prerequisites and the steps.

The fixture plan must omit `selectionContext` and contain exactly
`expectedPlatformRuntimeRevision`, `fullFlow`, `gate`, and `message`. Set
`gate` and `message` to `null`. The `fullFlow` object must contain exactly
these fields:

| Field | Required value |
| --- | --- |
| `approvedModelSelectionReference` | The approved BYOK model selection reference |
| `approvedModelSourceMode` | `byok` |
| `approvedReasoningEffort` | The approved reasoning effort |
| `approvedMaxPromptTokens` | The approved positive prompt-token limit |
| `maxRunUsd` | The largest run cost in USD. Use a number more than 0 and not more than `5`. |
| `maxDurationMinutes` | The time limit for the journey, from `10` to `30` minutes |

For example:

```json
"fixturePlan": {
  "expectedPlatformRuntimeRevision": 3,
  "fullFlow": {
    "approvedModelSelectionReference": "<byok-model-reference>",
    "approvedModelSourceMode": "byok",
    "approvedReasoningEffort": "medium",
    "approvedMaxPromptTokens": 20000,
    "maxRunUsd": 5,
    "maxDurationMinutes": 30
  },
  "gate": null,
  "message": null
}
```

Replace `<byok-model-reference>` with the binding reference from your BYOK
plan. Set `expectedPlatformRuntimeRevision` to the current platform runtime
revision.

The journey does not accept `--native-probe-envelope` or
`--dispatch-recovery-envelope`. Before it starts the run, it requires the
approved BYOK model and a hard run budget on `byok.tokens` in USD that is not
more than `maxRunUsd`. If either is missing, the result is `blocked` and no
model request is sent. The result uses `full-flow-acceptance-result` with
`fullP1Status: "not-evaluated"`; it does not claim full P1 acceptance.
