# Agentweaver CLI

The Agentweaver 1.x CLI is a native .NET 10 executable named `agentweaver`.
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
agentweaver api invoke <operationId> [--path name=value] [--query name=value] [--body <JSON|@file>] [--header name=value] [--output <new-file>]
```

`--output` is for an advertised binary response and must name a new file.
Server-sent events stream as they arrive and stop on cancellation. The CLI
preflights the current authorization context and applies same-origin,
redirect, and reserved-header guards. It does not accept caller-supplied
bearer tokens or project-authorization headers. When an operation advertises
`X-Agentweaver-Project-Authorization`, the CLI obtains the separate,
unbound `projects.admin` authorization for the same actor and tenant.

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

The production evidence provider is wired to verify source-bound publication,
AgentHost archive measurements, and service readback. A non-dry-run needs the
exact external manifest and publication/deployment proof files, and may
change API state during fixture setup and cleanup; do not use it against
production. Full P1 acceptance remains blocked when required native/session-
material proof or authorized target evidence is unavailable. The manifest and
evidence are external files; relative proof paths resolve from the manifest
directory.

```powershell
agentweaver test --input manifest.json --output evidence.jsonl
```

The command accepts the runner's pinned manifest contract and optional
envelopes:

```text
agentweaver test --input <manifest> --output <evidence> [--native-probe-envelope <file>] [--dispatch-recovery-envelope <file>] [--dry-run] [--interactive]
```

The manifest's `environment` object must provide `baseUrl` (Gateway origin),
`issuer` (Broker/OIDC issuer), `audience`, `tenantId`, and `actorId`. The runner
builds its target from those fields and uses the `agentweaver-cli` client; it
does not substitute target flags or environment variables. For each
invocation, the runner creates its owned project, takes its ID from the
`createProject` receipt, and derives the run ID from the invocation identity.

Interaction is disabled by default (`InteractionPolicy.Never`). Only
`--interactive` allows browser authorization. For a test run, the CLI first
sets up its owned fixture and observes the selected run, then obtains
authorization bound to both that exact project and run. Setup and run
authorization must identify the same actor and tenant. An unbound token or
refresh cannot stand in for exact-run authority.

`--dry-run` validates the manifest without network calls, authentication,
browser launch, or evidence creation, even when `--interactive` is present.
The intended distributed runner does not need Node.js, a .NET SDK/runtime,
Git, `gh`, or a repository checkout. The intended source and deployment checks
use read-only pinned GitHub and OCI evidence; full deployed evidence also
requires `kubectl` configured for the operator-selected context. These
production evidence paths remain incomplete as noted above. The CLI does not
download or execute repository code or forward Gateway credentials to GitHub,
a registry, or a cluster.

Source verification is bound to the committed `releases/foundation.json` in
`sabbour/agentweaver` at the exact pinned commit and tree. The supplied
manifest's local name or path cannot select a different repository or source
blob.

The expected result contract is `0` for passed, `1` for failed, and `2` for
blocked; spawned-process behavior for `test` has not been verified yet. A
missing or denied exact-run authorization is blocked only if owned-fixture
cleanup succeeds. Failed or uncertain cleanup takes precedence and returns
failed; reconcile that invocation before retrying. Do not start a new
invocation or reuse the cleaned project/run while cleanup is unresolved. After
successful cleanup or reconciliation, a new attempt uses
`--interactive`, a new invocation identity, and a new evidence path. Native
probe evidence is not fabricated or dispatched by this CLI, so the overall
acceptance can remain blocked even when its API surface passes. The current
launcher does not invent a second account; missing independent native probes
or a second denied-actor result remains blocked.
