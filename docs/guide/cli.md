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
`--gateway-url`. Keep project/run overrides paired. The read-only API case
script supplies its target as CLI options from its case input; it does not use
inherited target environment values.

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

## Run the read-only Gateway API case

The checked-in case script checks Gateway discovery and the signed-in actor’s authorization context through normal CLI commands. It runs auth status, api operations, api describe, and rest; it does not mutate resources, send model requests, or launch a browser.

For the input fields, exact apphost requirements, dry run, and evidence results, follow [Run a read-only CLI API case](./api-acceptance-migration.md). Use only an explicitly approved non-production target. The read-only case does not prove full API or P1 acceptance.
