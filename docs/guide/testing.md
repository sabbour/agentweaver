# Testing and Azure acceptance

Unit tests, real-dependency integration tests, and deployed end-to-end (E2E) tests
answer different questions. Passing one does not substitute for the others.

| Layer | What it proves | When it runs |
| --- | --- | --- |
| Unit and provider conformance | Selection, capabilities, immutable bindings, explicit failures | Every affected change |
| PostgreSQL integration | State/event atomicity, schema migration, idempotency, stream ordering, concurrent claims, lease fencing and restart recovery | Every persistence change; the foundation CI runs the whole small suite |
| Service compatibility | Current and N-1 API/event peers agree during rollout | When real service APIs and consumers exist |
| Azure-backed service integration | Actual identity, network access, managed dependencies, rollout and recovery | From the first deployable vertical slice |
| Platform E2E | A user's task completes correctly through the assembled platform | From the end of P1, and for integrated candidates thereafter |

## What works now

The provider tests use in-memory descriptors. Persistence tests use a disposable
PostgreSQL container, not mocks, SQLite, or a shared developer database. They verify
durable state through database reads as well as returned results.
The Azure Key Vault adapter tests exercise the real Azure SDK authentication,
request/response and error pipeline through an in-memory HTTP transport and fake
`TokenCredential`; they require no Azure account, outbound network or provisioned
vault. They prove adapter transport behavior, not live workload identity, RBAC or
an Azure deployment.

Run the commands in the [root README](../../README.md#build-and-check-the-foundation).
The PostgreSQL suite requires a running Docker-compatible engine and permission to
pull its test image. Missing Docker, an unavailable image, or a database startup
failure fails the suite; it is not an automatic skip.

Testcontainers is a test dependency, not a supported local product deployment.
The suite disposes only its own database/container. It never cleans up unrelated
containers or contacts production resources.

These tests are **not platform E2E**. There is no deployable v1 service yet, and
this slice does not create an Azure environment.

## Code coverage

After the locked restore and Release build documented in the root README, run:

```powershell
npm run coverage:dotnet
npm run coverage:node
node --test scripts\coverage\tests\*.test.mjs
```

The .NET command restores the pinned local ReportGenerator tool, runs all three
test suites once with Coverlet, and merges their reports using
ReportGenerator. Its scope is all four current production libraries:
Abstractions, Providers, Persistence.Postgres and Secrets.AzureKeyVault. Shared sources are merged,
not summed twice. Node uses its built-in test coverage and spec/LCOV reporters
for the release validator; test files, fixtures, and coverage wrappers are not
production targets. No external JavaScript coverage dependency is needed.

Reports are written beneath `artifacts\coverage\` (ignored by Git):

| Location | Contents |
| --- | --- |
| `dotnet\combined\` | HTML (`index.html`), Cobertura XML, JSON/text summaries, and GitHub Markdown summary |
| `dotnet\providers\`, `dotnet\postgres\` | Individual test-suite Cobertura reports |
| `dotnet\keyvault\` | Azure Key Vault adapter Cobertura report |
| `node\` | `lcov.info` and `summary.txt` containing live test output and the native coverage table |
| `source.json` (CI only) | Tested checkout SHA, PR head SHA, run ID, and run attempt |

Each runner clears only its own report directory before collecting, rejects
missing or unexecuted production coverage, and preserves failing test status.
CI replaces plain test steps with these collectors rather than running suites
twice. Its summary and downloadable artifact are published even after a failed
step when available; partial reports do not turn a failed run green. Reports
remain in GitHub Actions for 30 days, with no external analytics upload.

The initial measured foundation baseline is:

| Scope | Lines | Branches | Methods/functions |
| --- | --- | --- | --- |
| .NET combined | 423/452 (93.5%) | 184/240 (76.6%) | 90/97 (92.7%) |
| Abstractions | 81/87 (93.1%) | 15/16 (93.7%) | 48/53 (90.5%) |
| Providers | 126/144 (87.5%) | 102/148 (68.9%) | 17/18 (94.4%) |
| Persistence.Postgres | 216/221 (97.7%) | 67/76 (88.1%) | 25/26 (96.1%) |
| Node release validator | 95.92% | 92.59% | 100% |

These are a starting observation, not a required percentage or a claim of
behavioral completeness. Read the report for the exact tested candidate as
source and tests change. Instrumented line, branch, and method/function coverage
does not prove integration correctness, Azure connectivity, or platform E2E
acceptance; the distinct test layers above still apply.
The table predates the Azure Key Vault adapter; use the new combined report for
the current four-library measurement rather than interpreting the old baseline
as a threshold.

## When Azure is added

Create a dedicated **v1 integration environment during P0 when the first deployable
service exists**, before accepting its cloud integration. Do not reuse or mutate
0.x staging. Provisioning needs an explicitly selected subscription, region, resource
group, and cost/lifecycle policy; none is assumed by the library test commands.

Deploy an exact source commit to AKS and record image digests and component versions.
As the components land, verify Azure Database for PostgreSQL, workload identity and
Key Vault, Blob access, network restrictions, and telemetry export with their real
cloud configuration. A missing environment blocks cloud acceptance; it does not
turn it into a successful test.

Start with a deployed service request that writes durable state and an outbox event,
then verify consumption, retry and recovery. A health endpoint alone is only a
deployment smoke check, not an E2E success criterion.

## Platform E2E

At the end of P1, run one representative project through the assembled platform:
start work, provision a sandbox, execute an agent, encounter and resolve an
approval or recovery gate, produce reviewed output, and clean up the run's
resources. Add preview verification when the application/preview slice lands in
P2; check both the initial and corrected revisions.

Use the API, UI, and MCP persona harnesses against that same exact-SHA deployment
as their real surfaces become available. Add focused scenarios for each new
feature. Keep the full challenge catalog for scheduled or explicitly requested
sweeps rather than rerunning it for every small change.

Store the tested source SHA, platform composition, scenario results, and evidence
links together. Release acceptance requires the representative journey and the
affected feature scenarios to pass. Failure produces a repair, another exact-SHA
deployment, and a rerun; unit tests or a green health probe cannot waive it.

E2E cleanup removes only resources created by that test run, including its
previews. It must not remove another project's deployments.
