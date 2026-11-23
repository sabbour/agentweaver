# Read stored AgentHost egress

Use the native CLI to read one page of attributed outbound traffic from
storage for a run. The command performs a read operation on the stored audit
records.

## Before you begin

Configure the Gateway, Broker issuer, client ID, API audience, and tenant as
described in [Agentweaver CLI](./cli.md#configure-a-target). Sign in to the
selected Broker:

```powershell
$env:AGENTWEAVER_PROJECT_ID = $null
$env:AGENTWEAVER_RUN_ID = $null
agentweaver auth login
```

The base sign-in requests `api.read`. The caller must have current project or
tenant `ReadProjects` authority for the run.

For the Platform/default administrator path, sign in with `projects.admin`:

```powershell
$env:AGENTWEAVER_PROJECT_ID = $null
$env:AGENTWEAVER_RUN_ID = $null
agentweaver auth login --scope projects.admin
```

The server requires current Platform/default
`ReadPlatformRuntimeDefaults` authority in addition to that OAuth scope.

## Read one page

Run:

```powershell
agentweaver run egress RUN_ID
```

Replace `RUN_ID` with the run identifier. The command encodes the identifier as
one URL path segment. It uses an unbound credential, then the server resolves
the run inside the selected tenant. Do not pass `--project-id` or `--run-id`.

To use the administrator path, add the scope:

```powershell
agentweaver run egress RUN_ID --scope projects.admin
```

To choose a page size, use an integer from 1 through 100:

```powershell
agentweaver run egress RUN_ID --limit 25
```

If you omit `--limit`, the Gateway uses 50.

## Continue to the next page

Copy the returned `nextCursor` value into the next command without changing it:

```powershell
agentweaver run egress RUN_ID --limit 25 --cursor CURSOR
```

Keep the same run and `--limit` while you page through the result. For the
Platform/default administrator path, keep its scope and tenant selection:

```powershell
agentweaver run egress RUN_ID --scope projects.admin --tenant-id TENANT_ID --limit 25 --cursor CURSOR
```

Replace `TENANT_ID` with the tenant used on the first page. When `nextCursor`
is `null`, the page has no successor.

## Read the result

The command writes one JSON object to standard output:

```json
{
  "runId": "RUN_ID",
  "coverage": "Unavailable",
  "delayedUntil": null,
  "items": [],
  "nextCursor": null
}
```

Each item contains nullable `host` and `address`, plus `port`, `verdict`,
`firstSeen`, `lastSeen`, and `count`. Items retain the server order.

`coverage: "Unavailable"` is a successful response from the stored-data path.
The value describes the stored evidence coverage. The CLI exits with code 0
and writes a clear notice to standard error.

Usage, contract, transport, response-shape, and API errors leave standard
output empty and exit with code 1. Cancellation exits with code 130.

## Understand the API path

The CLI calls
`GET /api/v1/runs/{runId}/egress` through the Gateway. It verifies the live
OpenAPI contract before it authenticates or sends the egress request. For the
server data flow, authorization rules, and response limits, see
[Read stored run egress](../architecture/environment-egress.md#read-stored-run-egress).
