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
issuer, API audience, and tenant. A run selector requires a project selector.
A project selector without a run is valid for project administration with
`projects.admin`; run-bound calls use `projects.orchestrator`. These settings
identify the target; never pass access or refresh tokens as command-line
arguments.

For `auth` and API commands, the nonsecret target options override their
matching environment variables: `--gateway-url`, `--broker-issuer`,
`--client-id`, `--api-audience`, `--tenant-id`, `--project-id`, and `--run-id`.
Discovery still uses only the Gateway URL, whether supplied by environment or
`--gateway-url`. Set both project and run for a run-bound request. Set only
project for a project-admin request. The read-only API case script supplies
its target as CLI options from its case input; it does not use inherited target
environment values.

```powershell
$env:AGENTWEAVER_GATEWAY_URL = "https://gateway.example"
$env:AGENTWEAVER_BROKER_ISSUER = "https://identity.example"
$env:AGENTWEAVER_API_AUDIENCE = "https://gateway.example"
$env:AGENTWEAVER_TENANT_ID = "TENANT_ID"
# Optional; defaults to agentweaver-cli.
$env:AGENTWEAVER_CLIENT_ID = "agentweaver-cli"
# Optional. Run-bound requests need both values; project administration needs only the project.
$env:AGENTWEAVER_PROJECT_ID = "PROJECT_ID"
$env:AGENTWEAVER_RUN_ID = "RUN_ID"
```

These environment selectors configure individual CLI commands. The read-only
API case passes the target selectors from its input and does not create a
project or run.

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
The read-only API case uses the `api.read` scope. It does not request
project/run authority or run selection operations.

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

## Read stored run egress

Use `run egress` to read one page of stored outbound traffic for a run:

```powershell
agentweaver run egress RUN_ID
agentweaver run egress RUN_ID --limit 25
agentweaver run egress RUN_ID --limit 25 --cursor CURSOR
```

The command uses the cached Broker sign-in and the base `openid api.read
offline_access` scopes. The Gateway checks current tenant, project, and role
authority for every request. To read through the Platform/default
administrator path, sign in with `projects.admin` and select that scope for the
command:

```powershell
agentweaver auth login --scope projects.admin
agentweaver run egress RUN_ID --scope projects.admin
```

The command uses an unbound credential. Do not pass `--project-id` or
`--run-id`. `--limit` accepts 1 through 100. If you omit it, the Gateway uses
50. Pass `nextCursor` unchanged to read the next page.

Standard output contains one JSON object with `runId`, `coverage`,
`delayedUntil`, `items`, and `nextCursor`. `coverage: "Unavailable"` is a valid
page result. It means the source contains only stored attributed flows; the CLI
writes a notice to standard error and exits with code 0. Errors leave standard
output empty and exit with code 1. Cancellation exits with code 130.

See [Manage AgentHost egress reads](./manage-agenthost-egress.md) for the
authorization and paging workflow.

## Read, validate, and apply Platform Policy

The policy commands read, validate, export, and apply the canonical Platform
Policy. Read and validation commands require active membership and current
PlatformAdmin authority. Apply also requires the `projects.admin` OAuth scope
and current PlatformAdmin authority. The server checks authority for every
request; the scope does not grant a role.

All policy commands use an unbound identity. They reject `--project-id` and
`--run-id` and ignore `AGENTWEAVER_PROJECT_ID` and `AGENTWEAVER_RUN_ID`.

```powershell
$env:AGENTWEAVER_PROJECT_ID = $null
$env:AGENTWEAVER_RUN_ID = $null
agentweaver auth login
```

For apply, sign in with the required scope:

```powershell
agentweaver auth login --scope projects.admin
```

```powershell
agentweaver policy show --scope platform
agentweaver policy history --scope platform --page-size 20
agentweaver policy validate --scope platform --input .\platform-policy.json
```

`show` returns JSON with `scopeType`, `scopeId`, `policyId`, `revision`,
`contentSha256` and `updatedAt`. To read an exact retained revision, use its
positive decimal revision number. The exact read returns `createdAt` instead
of `updatedAt`.

```powershell
agentweaver policy show --scope platform --revision 1
```

To export the complete current artifact, give `--output` a new file path:

```powershell
agentweaver policy export --scope platform --output .\platform-policy.json
agentweaver policy export --scope platform --revision 1 --output .\platform-policy-revision-1.json
```

The JSON file contains the manifest bytes, every module and data file, the
complete content hash, and the exact revision. The artifact bytes remain in
the API's `manifestBytes` and `files[].bytes` arrays. The CLI recomputes the
hash before it writes the file. The command does not overwrite an existing
file. Standard output contains the scope, policy ID, revision, hash, timestamp,
and output path; it does not print the policy artifact.

`validate` reads an export file of up to 64 MiB. It checks the export metadata
and artifact hash, then sends the `artifact` object to the Platform Policy
validation route. The serialized request must fit the Gateway's 5 MiB limit.
The server checks current Platform Policy reader authority and runs validation
without changing policy state.

```powershell
agentweaver policy validate --scope platform --input .\platform-policy.json
```

The command prints JSON with `isValid` and `diagnostics`. It exits with code 0
when the artifact is valid and code 1 when it is invalid or the request fails.
Invalid artifacts include the server's bounded diagnostics. The command does
not print the artifact bytes.

`apply` sends a complete Platform export file to the existing policy write
route. It requires the expected current revision and a reason:

```powershell
agentweaver policy apply --scope platform --input .\platform-policy.json --expected-revision 4 --reason "Approve reviewed policy update"
```

Give `--expected-revision` a canonical positive decimal Int64. Use `none` to
require an empty Platform Policy head. This value is the current head to
compare, not the revision recorded in the input file. A stale value returns
HTTP 409. `--reason` must contain non-whitespace text and no more than 1,024
characters.

The CLI checks that the input has Platform scope and a valid artifact hash
before it sends the request. The request must fit the Gateway's 5 MiB limit.
The server checks current PlatformAdmin write authority and the expected
revision. A successful preparation prints JSON with `scopeType`, `scopeId`,
`policyId`, `preparationId`, `state` (`pending`), expected head fields,
`proposedContentSha256`, `checkpoint`, `createdAt`, `replayed`, and optional
`governanceDecisionId` and `governanceDecisionHash` without claiming an active
policy revision. A replay of a legacy committed write prints `scopeType`,
`scopeId`, `policyId`, `revision`, `contentSha256`, and `replayed`. Neither
response prints the artifact.

The CLI saves an idempotency key and correlation ID in per-user state before it
sends the write. It does not save the artifact or reason there. If the result
is uncertain, run the same command again with the same input, expected
revision, and reason. The CLI reuses the saved IDs. A different artifact,
revision, or reason uses a different key.

The server write gate (`NativeAcsPolicy:HeadWritesEnabled`) stays disabled by
default. When the gate is disabled, apply reports
`native_acs_policy_writes_unavailable` and keeps the recovery record. Run the
same command again when writes are enabled. Apply prepares a pending change; it
does not activate or enforce a policy revision.

History returns the newest revision first. Each item contains `revision`,
`contentSha256` and `createdAt`. The response also contains
`snapshotRevision` and `nextCursor`. To continue, pass `nextCursor` unchanged
and use the same page size. Page size defaults to 20 and ranges from 1 to 100.

```powershell
agentweaver policy history --scope platform --page-size 20 --cursor CURSOR
```

Replace `CURSOR` with the returned value. An empty scope returns an empty
history; a show request returns `native_acs_policy_not_found`. The head and
exact revision reads verify the stored artifact hash. History shows stored
hashes without verifying each artifact. `show` and `history` return metadata,
not artifact files or enforcement status. `export` writes source data; it does
not activate or enforce policy.

To read Platform Policy governance decision history, use `decisions`:

```powershell
agentweaver policy decisions --scope platform --page-size 20
agentweaver policy decisions --scope platform --policy-id canonical --page-size 20 --before-sequence SEQUENCE
agentweaver policy decisions --scope platform --output .\platform-decisions.json
```

`--policy-id` defaults to `canonical`. `--page-size` defaults to 20 and ranges
from 1 to 100. Pass the returned `nextBeforeSequence` integer as
`--before-sequence` to read older records. The output projects the stream
identity, `nextBeforeSequence`, and allowlisted decision fields, including
`pending` outcomes and `null` `policyRevision` values, while omitting identity
and role-assignment metadata. Decision history records past governance events;
it does not show that a policy revision is active or enforced.

Pass `--output <new-file>` to write the same JSON page to a new file encoded as
UTF-8 without a byte order mark. When `--output` succeeds, the command exits
with code 0 and prints nothing to standard output or standard error. To export
an older page, pass the returned `nextBeforeSequence` digits unchanged as
`--before-sequence` with a different `--output` file path. The CLI does not
overwrite an existing file or follow a final symbolic link. On Unix, new files
start with owner-only read and write permissions (`0600`). On Windows, new
files inherit the destination directory access control list (ACL); choose a
directory with a private ACL for reports you want to keep private. If a write,
flush, close, or cancellation occurs after file creation, the CLI leaves the
file in place; treat that file as incomplete and retry with a new file name.

### Read Project Policy sources

Project Policy reads use your current server-side project access. Sign in with
`agentweaver auth login` and set the tenant target. The CLI uses an unbound
identity with its base scopes. `--project-id` selects the Project route; it
does not bind the token. These reads do not need `projects.admin`.

List returns one page of visible Projects that have a saved Policy head. It
does not show an inherited view. Tenant admins can read Projects in their
tenant. Project owners, contributors, and viewers can read Projects where they
have a current role. Platform admin status alone does not grant Project access.

```powershell
agentweaver policy list --scope project --page-size 20
agentweaver policy list --scope project --page-size 20 --cursor CURSOR
agentweaver policy show --scope project --project-id PROJECT_ID
agentweaver policy show --scope project --project-id PROJECT_ID --revision 2
agentweaver policy history --scope project --project-id PROJECT_ID --page-size 20
agentweaver policy export --scope project --project-id PROJECT_ID --output .\project-policy.json
agentweaver policy export --scope project --project-id PROJECT_ID --revision 2 --output .\project-policy-revision-2.json
```

Replace `PROJECT_ID` with the 32-character ID from the list output. Replace
`CURSOR` with the returned `nextCursor`. List and history page sizes default to
20 and range from 1 to 100. Pass each cursor unchanged with the same page size.
List cursors can contain up to 4,096 characters. If a list cursor is rejected
after a membership or role change, start again without a cursor.

`show` returns Project scope, Project ID, Policy ID, revision, content hash,
and timestamp. Exact revisions return `createdAt`; the current head returns
`updatedAt`. `history` returns one page of revisions. An empty history has no
items and null lineage fields.

`export` writes the complete artifact to a new file and verifies its canonical
hash. The file contains the manifest, all relative-path files, the Project ID,
revision, hash, and timestamp. Standard output contains metadata and the output
path, not artifact bytes. The command does not overwrite an existing file.

To read recorded governance decisions for one Skill in a Project, use `decisions`:

```powershell
agentweaver policy decisions --scope project --project-id PROJECT_ID --resource-type skill --resource-id SKILL_ID --page-size 20
agentweaver policy decisions --scope project --project-id PROJECT_ID --resource-type skill --resource-id SKILL_ID --page-size 20 --before-sequence SEQUENCE
agentweaver policy decisions --scope project --project-id PROJECT_ID --resource-type skill --resource-id SKILL_ID --output .\skill-decisions.json
```

Replace `PROJECT_ID` with the 32-character Project ID. Replace `SKILL_ID` with
the exact Skill ID (1 to 256 characters, case-sensitive, with no leading or
trailing whitespace). `--page-size` defaults to 20 and accepts 1 to 100. Pass
the returned `nextBeforeSequence` integer digits unchanged as
`--before-sequence` to read older records; do not round integers larger than
53 bits through floating-point JSON parsers. Pass `--output <new-file>` to save
the JSON page to a new file with silent success, owner-only Unix permissions,
and the same Windows ACL and incomplete-file recovery rules as Platform
decision export.

The output includes the stream identity, `items`, and `nextBeforeSequence`.
An empty stream or a read with `--before-sequence 1` returns an empty `items`
array and `nextBeforeSequence: null` with exit code 0. Recorded Skill trust
evidence entries keep `outcome: "pending"`, `approvalState: "pending"`, and
`null` for `policyRevision` and `policyArtifactSha256`. Missing and
inaccessible Projects return the same HTTP 404 status message. Decision history
shows recorded events; it does not show active policy or Skill admission state.

### Validate a Project Policy artifact

Project validation requires current Project Owner or TenantAdmin authority and
an unbound identity with `api.read` and `projects.admin`. Clear any Project
and run defaults before you sign in:

```powershell
Remove-Item Env:AGENTWEAVER_PROJECT_ID, Env:AGENTWEAVER_RUN_ID -ErrorAction SilentlyContinue
agentweaver auth login --scope projects.admin
```

Replace `PROJECT_ID` with the 32-character ID from the Project list. Pass a
current or exact-revision file from `policy export`:

```powershell
agentweaver policy validate --scope project --project-id PROJECT_ID --input .\project-policy.json
```

The CLI checks the export scope, Project ID, and artifact hash before Gateway
discovery. It rejects files larger than 64 MiB and compact JSON request bodies
larger than 5 MiB. It sends no query or secondary Project bearer. The command
uses an unbound identity and rejects `--run-id`.

The command prints only `isValid` and bounded `diagnostics`. It exits with code
0 for a valid artifact and code 1 for an invalid artifact or failed request.
Validation checks the submitted artifact without reading, saving, or activating
Policy. Unsafe diagnostic paths are returned as null.

#### Validate a new Project artifact without an export

Use the Gateway API for an artifact that has no export. In a .NET application
that references `packages\Agentweaver.Policy.Acs\Agentweaver.Policy.Acs.csproj`,
create the request file. The empty action list denies all actions. The
constructor retains the pinned manifest and modules, omits Platform network
data, and computes the hash over the submitted bytes.

```csharp
using System.Text.Json;
using Agentweaver.Policy.Acs;

var artifact = NativeAcsComposableDomainsProfile.CreateProjectArtifact(
    """{"allowed_action_ids":[]}""");
await using var file = File.Open("project-policy-validation.json", FileMode.CreateNew);
await JsonSerializer.SerializeAsync(
    file, new { artifact }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
```

Use the same unbound sign-in and Project ID as above:

```powershell
agentweaver api invoke validateProjectNativeAcsPolicy --path projectId=PROJECT_ID --scope projects.admin --body '@.\project-policy-validation.json'
```

`api invoke` accepts a JSON body up to 1 MiB. The Gateway HTTP limit is 5 MiB.
For the v2 profile, use `NativeAcsUnifiedPolicyProfile.CreateProjectArtifact`
with actions, model-use, and Guardrails data.

## Discover and invoke API operations

The CLI reads the currently served Gateway `GET /openapi/v1.json` document for
operation discovery. It does not maintain a second route table or an SDK:

```powershell
agentweaver api operations
agentweaver api describe getProject
agentweaver api invoke getProject --path projectId=PROJECT_ID
```

Environment discovery and recovery use the same commands; the CLI adds no
Environment-specific command set:

```powershell
agentweaver api describe listRunEnvironments
agentweaver api describe reconcileEnvironmentSandbox
```

The descriptions come from the Gateway's current OpenAPI document. Check the
[Environment API reference](../reference/api.md#environment-discovery-and-recovery)
for owner permissions and recovery limits before you invoke a write operation.

For run recovery, use `api describe recoverRun` to read the current request
schema. The optional `checkpointRecovery` field supports Host recovery at a
pending gate and admission of a saved child join. A `202` response means the
operation is still pending. Retry it with the same idempotency key. For a
pending gate, a `ready` response means the Host restored the saved boundary.
The gate remains pending, and the run remains incomplete. Read
`checkpointRecovery.operationId` from normal run or root-session status to
identify the selected durable recovery operation. The owner selects it; the
caller cannot select a different operation through a status request. For a child join,
the owner keeps `dispatchHeld` set while it restores saved, unsent children.
It registers the root and all required children before it starts any bootstrap.
The final owner transaction imports a completed-but-unjoined result only when
the saved material, child session and fence, and native accounting match the
frozen manifest. It does not send another task message for that child. A
missing or mismatched result keeps the hold active. After recovery is `ready`,
ordinary dispatch can send saved messages for children that were pending and
unsent. `ready` does not mean that all child tasks or the run are complete.
Use `api describe readCheckpointRecoveryEvents` to inspect the read contract
for one operation's retained progress. Follow its cursor to read each page.
Start a new read for later events. The read does not restart recovery.
For a prepared native child, the owner preserves the original dispatch row and
adds a verified `native-child-rebind` receipt before it releases the hold.
This receipt changes the effective runtime binding, not the saved message,
prompt, or model selection. Recovery itself does not send the child message.
Read the
[recovery API reference](../reference/api.md#recover-a-host-at-a-pending-gate)
before you use this field.

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

Use `--turn-content-sha256` with `readSessionTurnContent` to verify a stored
reply without printing the text. Sign in with a run-bound identity that has
permission to read the turn. The `projectId` and `runId` path parameters must
match that identity.

```powershell
agentweaver api invoke readSessionTurnContent --turn-content-sha256 --scope projects.orchestrator --project-id PROJECT_ID --run-id RUN_ID --path projectId=PROJECT_ID --path runId=RUN_ID --path sessionId=SESSION_ID --path eventId=EVENT_ID
```

The command returns `projectId`, `runId`, `sessionId`, `eventId`, and
`contentSha256`. The digest is a SHA-256 hash of the decoded text as UTF-8,
including its original spaces and line endings. This mode does not accept a
request body, query parameters, custom headers, or an output file.

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
setup context. These individual calls do not replace the read-only Gateway
case, which checks discovery and authorization context through normal CLI
commands. The operations remain excluded from MCP.

The operation list includes supported public operations in the served Gateway
catalog even when they are not admitted to MCP. Identity Connection browser
BFF routes such as `/api/connections/...` are not in this catalog; this is not
full browser or BFF parity. See the [HTTP API reference](../reference/api.md#cli-operation-discovery).

## Read runtime limit evidence

The `readRuntimeObservationV2` operation reads the Orchestrator's current,
owner-authorized evidence for one run and session. The caller needs current
Coordination authority for that project and run. Discover the operation and
read the first page with:

```powershell
agentweaver api describe readRuntimeObservationV2
agentweaver api invoke readRuntimeObservationV2 --path projectId=PROJECT_ID --path runId=RUN_ID --path sessionId=SESSION_ID --query pageSize=50
```

Use the returned `nextCursor` value to read the next page. A cursor keeps the
same snapshot and binds to the caller, run, session, accepted selection, and
page size. It expires after 24 hours. Do not combine a cursor with `attemptId`.
To find all recorded facts for one attempt, use an exact lookup:

```powershell
agentweaver api invoke readRuntimeObservationV2 --path projectId=PROJECT_ID --path runId=RUN_ID --path sessionId=SESSION_ID --query attemptId=ATTEMPT_GUID
```

The response marks legacy or uninstrumented history as incomplete. Missing
native-send, tool-execution, or consumption proof stays unavailable; a
reservation does not prove that native work ran. For a tool-call limit denial,
`toolPermission` can show that the runtime recorded a rejection decision.
It does not prove that the SDK received the decision or that the tool did not
run. If the owner retains more than 100 facts for one attempt, the exact
response includes an unavailable reason and a history cursor. Continue with
the cursor and omit `attemptId`. An empty exact result means the owner did not
retain matching evidence; it does not prove that the attempt did not occur.
The route is a read, not a live acceptance test.
See [runtime observation contracts](../reference/contracts.md#runtime-observation-v2)
for the response fields and limits.
The [numeric runtime observation case](./numeric-runtime-observation-acceptance.md)
checks retained child-session tool and hard-credit denials through both
`api invoke` and `rest`. The child hard-credit case preserves exact decimal
number tokens and does not claim a native no-send witness. Its root model-turn
and hard-credit cases check a retained root-session denial and its native
no-send witness in the same way.
The [admitted root-completion case](./numeric-runtime-observation-acceptance.md#check-an-admitted-root-completion)
sends one model request and rejects the generated proposal. It requires
separate non-production, model-spend, and cleanup approval.

## Make a direct Gateway request

`rest` sends explicit HTTP methods to root-relative URLs or URLs on the
configured Gateway origin, including paths not listed in OpenAPI. It does not
fetch OpenAPI and is not proof of operation-catalog, browser, or BFF parity.

```text
agentweaver rest --method GET|HEAD|POST|PUT|PATCH|DELETE|OPTIONS --url <root-relative-or-same-Gateway-URL> [target options] [--scope <scope>] [--body <JSON|@file>] [--header name=value]... [--output <new-file>]
```

The target options are `--gateway-url`, `--broker-issuer`, `--client-id`,
`--api-audience`, `--tenant-id`, `--project-id`, and `--run-id`; each overrides
its matching environment variable. Run-bound calls need both project and run.
Project administration uses `--project-id` without `--run-id` and requests
`projects.admin`.
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

## Review a project repository

The `workspace` commands read the Repo App repository of a project. Each
command reads the project repository from the project configuration. Then it
opens one browser approval for one read.

```text
agentweaver workspace refs --project <project-id> --interactive [--cursor <2-10000>] [target options]
agentweaver workspace tree --project <project-id> --ref <commit-sha> --interactive [target options]
agentweaver workspace file --project <project-id> --ref <commit-sha> --path <repository-path> --interactive [--output <new-file>] [target options]
```

- `refs` lists branches and tags. To get the next page, use the `nextCursor`
  value from the result with `--cursor`.
- `tree` lists the files and directories at one commit.
- `file` reads one UTF-8 text file at one commit. The result shows the size,
  the SHA-256 hash, and the Git blob SHA. It does not show the file content.
  To keep the content, use `--output` with a new file path. The command does
  not replace a file that exists.

Use these rules:

- `--interactive` is necessary. Without it, the command stops before it sends a
  request.
- `--project` is the project GUID. Do not use `--project-id` or `--run-id`. The
  command uses an identity that is not bound to a project or run, with the
  `projects.admin` scope.
- `--ref` must be the full 40-character commit SHA. Use `refs` to find it.
- `--path` must be a relative repository path with `/` between the parts. The
  command stops before the approval if the path has `.` or `..` parts, a `\`,
  or an encoded `/` or `\`.
- The target options are `--gateway-url`, `--broker-issuer`, `--client-id`,
  `--api-audience`, and `--tenant-id`, or their environment variables.

The command first signs you in, if necessary. It then shows the repository and
the action on stderr and opens the browser. Approve the review in the browser.
A selection is good for one read only. Each new command opens a new approval.

| Message | What to do |
| --- | --- |
| A fresh Workspace repository selection requires --interactive. | Run the command again with `--interactive`. |
| The project does not use a GitHub App repository connection. | Configure the source control of the project. |
| Sign-in was canceled or access was denied. | Run the command again and approve the review. |
| The Identity Broker could not complete sign-in. | Make sure that the GitHub App installation can see the repository with the exact name and case, and that the connection is current. Then run the command again. |
| The Identity Broker returned an unexpected Workspace selection. | Run the command again. If the error occurs again, report it. |
| The Gateway request failed with HTTP status N (code). | Check your project role and the input values. Then run the command again. |
| The Gateway returned an unexpected Workspace response. | Do not use the result. Report the error. |
| The file is binary and cannot be read. | The command reads only text files. |

Exit codes are `0` for success, `1` for a failure, and `130` for cancellation.

## List pending provider host requests

Use `platform egress provider-requests list` to read one page of pending
model-provider host requests that Environment received across tenants. Clear
`AGENTWEAVER_PROJECT_ID` and `AGENTWEAVER_RUN_ID`, then sign in with
`agentweaver auth login --scope projects.admin` using an account with current
Platform Administrator read authority so the saved sign-in is unbound. The
command uses an unbound identity: it ignores `AGENTWEAVER_PROJECT_ID` and
`AGENTWEAVER_RUN_ID`, and it rejects `--project-id` and `--run-id`.

```text
agentweaver platform egress provider-requests list [--limit <1-100>] [--cursor <cursor>] [target options]
```

`--limit` defaults to `50` and accepts `1` through `100`. Pass `--cursor` with
the `nextCursor` value from the previous page to read the next page in server
order. An empty `requests` array can still return a non-null `nextCursor`; keep
paging until `nextCursor` is `null`. If the server rejects the cursor, rerun
without `--cursor` to start from the first page.

The command prints one JSON object with `requests` and `nextCursor`. Each item
in `requests` contains `host`, `port`, `scope`, `tenantId`, `projectId`,
`providerId`, `providerRevision`, and `requestedAt`. It lists only pending
provider host requests; it does not list non-provider requests, approvals, or
enforcement status.

## Manage platform BYOK provider drafts

Use `platform byok` to list and edit platform model-provider metadata through
the public Draft API. Sign in with `projects.admin` and use an account with
current PlatformAdmin authority. The API checks that authority on each request.
These commands use an unbound identity. They ignore
`AGENTWEAVER_PROJECT_ID` and `AGENTWEAVER_RUN_ID` and reject `--project-id` and
`--run-id`.

```text
agentweaver platform byok list [target options]
agentweaver platform byok show <providerId> [--credential-status | --operation-id <uuid>] [target options]
agentweaver platform byok add --name <name> --type <openai|azure|anthropic>
    --base-url <https-url> --model <deployment> [metadata options] [--operation-id <guid>]
    [--rate-card-id <id> --rate-card-version <version>]
agentweaver platform byok update <providerId> --expected-revision <revision>
    --name <name> --type <openai|azure|anthropic> --base-url <https-url> --model <deployment>
    [metadata options] [--operation-id <guid>] [--rate-card-id <id> --rate-card-version <version>]
agentweaver platform byok save-prices <id> <version> --source manual|azure-retail --input <file.json>
agentweaver platform byok set-key <providerId> --from-stdin
    --expected-revision <providerRevision> --operation-id <guid>
agentweaver platform byok recheck-key <providerId> --expected-revision <revision>
    --expected-credential-revision <credentialRevision> --operation-id <guid>
agentweaver platform byok disable-key <providerId> --expected-revision <revision>
    --expected-credential-revision <credentialRevision> --operation-id <guid>
agentweaver platform byok disable-key <providerId> --operation-id <guid> --cancel
agentweaver platform byok activate <providerId> --expected-revision <providerRevision>
    --expected-settings-revision <settingsRevision>
agentweaver platform byok deactivate --expected-settings-revision <settingsRevision>
```

The optional metadata options are `--wire-api`, `--azure-api-version`,
`--reasoning-effort` (repeat it to set more than one value), and
`--prompt-capacity-tokens`. Rate-card attachment options are
`--rate-card-id` and `--rate-card-version`; specify both or neither. On
`update`, omitting both clears an existing attachment. The API accepts
`completions` or `responses` for
`--wire-api`, and `none`, `minimal`, `low`, `medium`, `high`, or `xhigh` for
`--reasoning-effort`. The provider type must be `openai`, `azure`, or
`anthropic`. The base URL must use HTTPS. An Azure base URL must name the
resource host without an API path.

`list` reads every cursor page. Its JSON output contains `switchMode`,
`activeProviderId`, `settingsRevision`, and a `providers` array.
Each provider has a stable `providerId`, a mutable display `name`, provider
metadata, and a `revision`. Use the `providerId` from `list` or from the `add`
response for `show` and `update`. A display name does not identify a provider.
Pass `--credential-status` to `platform byok show` to read the saved credential
state (`not_set`, `enabled`, `disabled`, `pending`, or `quarantined`) and both
revision numbers (`providerRevision` and `credentialRevision`) without
returning key bytes. For platform responses, `projectId` is `null`.
Pass `--operation-id <uuid>` to `platform byok show` instead of
`--credential-status` to read the status of one credential write or disable
operation (`pending`, `publication_pending`, `published`, `quarantined`,
`cancelled`, `disable_pending`, or `disabled`) without resending key bytes.
All valid operation statuses exit `0`. Do not combine `--credential-status` and
`--operation-id`.

`add` creates a Draft at revision 1. `update` writes a complete metadata
revision and requires the current `--expected-revision`. Omitted optional
metadata values clear those values. Read the current metadata with `show`
before you update it. A stale revision returns an error; the CLI does not retry
the update. Read the provider again and review its current metadata before
starting a new update.

`save-prices` writes a version-1 runtime rate card from a JSON file. Use the
`manual` source for a complete manual rate-card envelope, or `azure-retail`
for a verified meter selection. The file must fit 64 KiB. A verified receipt
returns the rate-card ID and version, so attach that exact pair to a provider
draft.

Each write uses an operation ID. The CLI creates one when you omit
`--operation-id`. Give an explicit GUID when you need to replay a write. If the
write result is uncertain, repeat the same command with the same operation ID
and the same fields. The API replays that write. Use a new operation ID for a
new write. If you cancel a write, the CLI reports that its result is unknown
and prints the operation ID for replay.

If a `show` request returns
`model_provider_draft_metadata_requires_update`, the CLI prints
`BYOK_METADATA_REPAIR_REQUIRED` with the validated provider ID and revision.
Use that pair with `update` and supply complete corrected metadata. The API
does not return the rejected metadata.

`set-key` reads one non-empty, non-whitespace UTF-8 key (up to 8,192 bytes after
stripping one final LF or CRLF) from redirected stdin and uploads it as
`application/octet-stream` with `If-Match` and `Idempotency-Key`. Before
reading stdin, the CLI verifies the target provider revision and checks the
operation status endpoint (`404` required). On an uncertain or lost `PUT`
response, the CLI checks the operation status endpoint with the same
`operationId` and never resends the key.

`recheck-key` verifies the current enabled credential version without uploading
a key. `disable-key` disables the current credential and requires exact
provider and credential revisions and an explicit operation ID. Pass `--cancel`
with `--operation-id <uuid>` and no revision options to cancel a pending
credential operation without resending key material. `activate`
switches new runs to an `Available` provider. `deactivate` restores hosted
Copilot defaults for new runs. Activation and deactivation require the exact
settings revision; neither changes accepted runs. For the task procedure, see
[Manage platform BYOK provider metadata](./manage-model-providers.md).

Credential errors use fixed CLI messages. A quarantined HTTP 409 prints
`BYOK_QUARANTINED`; another HTTP 409 prints `BYOK_CONFLICT`. A pending
publication HTTP 503 prints `BYOK_PENDING_PUBLICATION`; a Vault-unavailable
HTTP 503 prints `BYOK_VAULT_UNAVAILABLE`. For `disable-key --cancel`, conflicts
and uncertain outcomes (such as HTTP 499, 5xx, transport errors, or invalid
receipts) print status lookup guidance with the original operation ID without
retrying. The CLI does not print raw response bodies.

## Manage project BYOK provider drafts

Use `project byok` to list and edit one Project's model-provider metadata
through the public Draft API. Sign in with `projects.admin`, bind the identity
to the exact project, and leave the run selector unset. The API checks current
Owner or PlatformAdmin authority on each request. The OAuth scope does not
grant either role.

```text
agentweaver auth login --scope projects.admin --project-id <projectId>
agentweaver project byok list --project-id <projectId> [other target options]
agentweaver project byok show <providerId> --project-id <projectId> [--credential-status | --operation-id <uuid>] [other target options]
agentweaver project byok add --project-id <projectId> --name <name> --type <openai|azure|anthropic>
    --base-url <https-url> --model <deployment> [metadata options] [--operation-id <guid>]
    [--rate-card-id <id> --rate-card-version <version>]
agentweaver project byok update <providerId> --project-id <projectId>
    --expected-revision <revision> --name <name> --type <openai|azure|anthropic>
    --base-url <https-url> --model <deployment> [metadata options] [--operation-id <guid>]
    [--rate-card-id <id> --rate-card-version <version>]
agentweaver project byok prices --project-id <projectId> --region <region> --search <meter-text>
agentweaver project byok set-key <providerId> --project-id <projectId> --from-stdin
    --expected-revision <providerRevision> --operation-id <guid>
agentweaver project byok recheck-key <providerId> --project-id <projectId>
    --expected-revision <providerRevision> --expected-credential-revision <credentialRevision>
    --operation-id <guid>
agentweaver project byok disable-key <providerId> --project-id <projectId>
    --expected-revision <providerRevision> --expected-credential-revision <credentialRevision>
    --operation-id <guid>
agentweaver project byok disable-key <providerId> --project-id <projectId>
    --operation-id <guid> --cancel
```

The project ID selects the token binding and the Project route. The CLI sends
that ID as `project_id` with no `run_id`. These commands ignore
`AGENTWEAVER_RUN_ID`, and they reject `--run-id`. Each request also checks current project authority, so a
removed or changed role is denied.

`list` reads every cursor page. Its JSON output contains the page switch
metadata and one `providers` array.
Use the stable `providerId` field from `list` or `add` with `show` and
`update`; the provider name is display text and can change. Pass
`--credential-status` to `project byok show` to read the saved credential
state (`not_set`, `enabled`, `disabled`, `pending`, or `quarantined`) and both
revision numbers (`providerRevision` and `credentialRevision`) without
returning key bytes. Pass `--operation-id <uuid>` instead of
`--credential-status` to read one credential operation's status without
resending key bytes. `add` creates a Draft. `update` writes a complete metadata
revision and requires the current `--expected-revision`. The metadata options
and write recovery rules match
[platform BYOK](#manage-platform-byok-provider-drafts).

`prices` searches Azure Retail meter candidates through the authenticated
Gateway route. `recheck-key` verifies a saved credential version, and
`disable-key` disables it. These commands never upload key bytes. For the procedure,
see [Manage project BYOK provider metadata](./manage-project-byok-provider-metadata.md).

## Configure a BYOK model binding

`models byok` plans an Azure OpenAI BYOK model binding. The `prices`, `plan`,
and `apply` commands do not need a target or sign-in.

```text
agentweaver models byok prices --region <azure-region> --search <meter-text>
agentweaver models byok plan --input <byok.json> --source <deploy/k8s> --output <new-plan-file> [--overlay-output <new-model-bindings-file>]
agentweaver models byok apply --plan <plan-file>
agentweaver models byok import-context --output <new-context-file> [target options]
agentweaver models byok import --model-bindings <file> --file-sha256 <digest> --expected-context <context-file> [target options]
```

`import-context` captures the selected account and owner-issued installation ID
in a new file. Compare the ID with the intended installation ID from a trusted
independent source before importing. `import` creates inactive platform Drafts
and returns each exact reference and provider ID. Keep the context file,
bindings file, digest, account, and target options for retry. For the complete
procedure and recovery steps, see
[Import model bindings as drafts](./manage-model-providers.md#import-model-bindings-as-drafts).

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
The `prices` command reads Azure Retail directly. The separate project-scoped
Gateway route serves authenticated API clients; see
[Search Azure Retail model prices](./gateway.md#search-azure-retail-model-prices).

## Run workflow generation through the Gateway

The `workflow-generation` case uses `api invoke` to submit a workflow draft,
check job status and result, and verify the stored assistant reply against the
workflow completion receipt. It uses a run-bound `projects.orchestrator`
identity and a separate unbound
`projects.admin` identity for the same actor and tenant. The case also cancels
and verifies the named disposable run.

Use an approved non-production Gateway and a disposable run with a bounded
model budget. The case creates a durable generation job and draft. For the
input, sign-in, dry run, and evidence steps, follow
[Run workflow generation](./api-acceptance-migration.md#run-workflow-generation).
This route-specific case does not replace full P1 API acceptance.

## Run task dispatch through the Gateway

The `task-model-completion` case uses normal CLI `api invoke` commands to
dispatch a claimed task through the public Gateway. It checks the accepted
BYOK model and hard run budget, the stored assistant reply against the child
receipt, priced usage, and cleanup of the disposable run.

The case sends a model request and cancels the named run. Use an approved
non-production Gateway and a disposable run. Follow
[Run task model completion](./api-acceptance-migration.md#run-task-model-completion)
for the input, identities, dry run, and evidence steps.

## Run the read-only Gateway API case

The checked-in case script checks Gateway discovery and the signed-in actor’s authorization context through normal CLI commands. It runs auth status, api operations, api describe, and rest; it does not mutate resources, send model requests, or launch a browser.

For the input fields, exact apphost requirements, dry run, and evidence results, follow [Run a read-only CLI API case](./api-acceptance-migration.md). Use only an explicitly approved non-production target. The read-only case does not prove full API or P1 acceptance.

To check the `workspace` commands, follow [Run the Workspace repository-read case](./api-acceptance-migration.md#run-the-workspace-repository-read-case).
