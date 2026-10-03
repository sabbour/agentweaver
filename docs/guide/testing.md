# Testing and Azure acceptance

Unit tests, real-dependency integration tests, and deployed end-to-end (E2E) tests
answer different questions. Passing one does not substitute for the others.

| Layer | What it proves | When it runs |
| --- | --- | --- |
| Unit and provider conformance | Selection, capabilities, immutable bindings, explicit failures | Every affected change |
| Identity authorization candidate | Exact trusted actor/project/run/purpose/SecretRef bindings, immutable grant identity/revision/expiry races, metadata-only credential lifetime narrowing, cancellation and error invalidation | Every Identity or credential contract change |
| Identity broker candidate | Native OIDC/cookie/OpenIddict protocol, authenticated consent/CSRF, S256 PKCE, configured resource audiences, permanent PostgreSQL grants, refresh replay, revisioned Identity-owned run grants, authenticated redemption HTTP, restart, cancellation, and Trace-level log redaction | Every broker change |
| PostgreSQL integration | State/event/consumer-receipt atomicity, version-1 schema upgrade, consumer-scoped duplicate admission and concurrent retry, stream ordering, concurrent claims, bounded relay publish-before-fenced-ack outcomes, cancellation at publish/ack and real lock waits, restart redelivery with inbox-gated effects | Every persistence change; the foundation CI runs the whole small suite |
| Telemetry in-process | Native trace, metric, and log export composition, resource identity, disposal, and failed-export isolation; no network destination. Cancellation status awaits an instrumented cancellable operation. | Every telemetry foundation change |
| Azure Monitor exporter composition | Configuration and injected-credential validation; SDK trace, metric, and log wiring with a fake HTTP transport, plus healthy exporter continuity when simulated ingestion fails. No Azure connection. | Every Azure Monitor integration change |
| Azure Blob transport fake | SDK HTTP requests, streamed binary data, create-only conditions, missing/conflict responses and failures without a live account | Every Object Store change |
| Azure P0 infrastructure tooling | Dedicated-target/digest/clean-tree guardrails, plan/deploy command construction, and acceptance-check blocking logic, all through an injected fake `az`/`git`; no live call | Every `scripts/azure` or `infra/bicep` change |
| Service compatibility | Current and N-1 API/event peers agree during rollout | When real service APIs and consumers exist |
| Azure-backed service integration | Actual identity, network access, managed dependencies, rollout and recovery | From the first deployable vertical slice |
| Platform E2E | A user's task completes correctly through the assembled platform | From the end of P1, and for integrated candidates thereafter |

## What works now

The provider tests use in-memory descriptors and cover exclusive, singleton,
ordered Guardrails/Telemetry, and layered Network Policy selection and pinning.
They do not apply or verify a real egress policy. Persistence tests use a disposable
PostgreSQL container, not mocks, SQLite, or a shared developer database. They verify
durable state through database reads as well as returned results.
The caller-driven relay integration tests also verify disjoint competing workers,
expired and replaced leases, and a real transaction/receipt-gated consumer effect
across restart. They do not provision a broker or exercise a deployed relay service.
Cancellation regressions verify that a pending publisher which ignores its token
does not block relay cancellation or cause a late acknowledgment, and that
cancellation takes precedence over synchronous or asynchronous transport failure.
The Azure Key Vault adapter tests exercise the real Azure SDK authentication,
request/response and error pipeline through an in-memory HTTP transport. The
injected-credential cases use a fake `TokenCredential`; the workload-identity cases
use the real Azure Identity OAuth token exchange with generated, nonsensitive
projected token files and fake OAuth and Key Vault transports. They require no
Azure account, outbound network, real credentials or provisioned vault. They prove
the composition pipeline, not live workload identity, RBAC or an Azure deployment.
Identity redemption integration tests run the actual OpenIddict validation
middleware and HTTP endpoint against disposable PostgreSQL. Generated signed
tokens include validated `sub`, `project_id`, and `run_id` claims; the test
backend is in-memory. A Testcontainers end-to-end test also runs the supported
external-login, consent, authorization-code, and token flow with durable
project/run grants, then redeems with the broker-issued token and verifies
cross-project/run denial before backend contact. A separate host-composition
check resolves the production Key Vault workload-identity adapter. Persistent
grant tests cover append-only revisions, compare-and-swap writers, idempotent
replay, revocation, restart, expiry, and schema ownership. These tests prove
local composition, not deployed token issuance, Key Vault authorization, or
cloud acceptance.
Object Store tests inject a fake HTTP transport into the Azure Blob SDK; these
verify SDK requests and adapter mappings but do not prove cloud credentials,
Azure permissions, durability, or deployed integration.

The [Azure P0 infrastructure tooling](../specs/1777-azure-p0-infrastructure.md)
tests inject a fake `az`/`git` executor into every guardrail, plan, deploy, and
acceptance-check function. They prove dedicated-target naming is enforced,
deploy stays dry-run unless `--execute` and every guardrail passes, and that
the acceptance entrypoint returns an explicit `blocked` result (never a
placeholder `passed`) when evidence or a dependent service is missing. They do
not call a live target. Local version/argv tests exercise the actual CLI wrapper.
They do not prove a deployed AKS cluster, Key Vault, Blob, PostgreSQL, or Monitor resource exists.
`infra/bicep/main.bicep` and its modules are validated with `az bicep build`
(offline compilation, no Azure login); `deploy/k8s/base/` is validated with
`kubectl kustomize` (offline rendering, no cluster connection).

Run the commands in the [root README](../../README.md#build-and-check-the-foundation).
The PostgreSQL suite requires a running Docker-compatible engine and permission to
pull its test image. Missing Docker, an unavailable image, or a database startup
failure fails the suite; it is not an automatic skip.

Testcontainers is a test dependency, not a supported local product deployment.
The suite disposes only its own database/container. It never cleans up unrelated
containers or contacts production resources.

The [Identity broker candidate](../specs/1779-identity-broker.md) supplies a service host and Docker build contract.
Its tests use generated credentials and isolated PostgreSQL, not a deployed Identity provider or resource server.
These tests are **not platform E2E**. They do not create an Azure environment or prove publication.

## Release tooling regressions

Run `npm run test:release` after a release-tooling change.
The suite uses real isolated Git repositories under ignored `artifacts/release-tests/`.
It covers source notes, receipt forgery, extra product edits, independent version intent,
explicit compatibility, exact rollback, cleanup errors, and verified reruns.
One fixture runs actual locked .NET restore, build, and pack for an offline library.
Image preparation and publication tests inject command results and never push artifacts.
Those tests prove command and receipt guards, not registry access or a published service.

Run `npm run release:validate` to validate the manifest and current records.
For committed candidate coverage, add `-- --base <full-base-commit-sha>`.
The guard only credits source-derived version mirrors from consumed records.
Other product edits still require fresh component records.
No standalone documentation checker exists in this branch's package commands or CI.

## Code coverage

After the locked restore and Release build documented in the root README, run:

```powershell
npm run coverage:dotnet
npm run coverage:node
node --test scripts\coverage\tests\*.test.mjs
```

The .NET command restores the pinned local ReportGenerator tool, runs all eight
test suites once with Coverlet, and merges their reports using ReportGenerator.
Its scope is eight production libraries and the Identity broker service: Abstractions, Providers,
Persistence.Postgres, Secrets.AzureKeyVault, Telemetry, Telemetry.AzureMonitor,
ObjectStore.AzureBlob, Identity, and Identity.Broker. Shared sources are merged, not summed twice. Node
uses its built-in test coverage and spec/LCOV reporters
for the release validator; test files, fixtures, and coverage wrappers are not
production targets. No external JavaScript coverage dependency is needed.

Reports are written beneath `artifacts\coverage\` (ignored by Git):

| Location | Contents |
| --- | --- |
| `dotnet\combined\` | HTML (`index.html`), Cobertura XML, JSON/text summaries, and GitHub Markdown summary |
| `dotnet\providers\`, `dotnet\postgres\` | Individual test-suite Cobertura reports |
| `dotnet\keyvault\` | Azure Key Vault adapter Cobertura report |
| `dotnet\identity\` | Identity authorization candidate Cobertura report |
| `dotnet\identity-broker\` | Native broker and real-PostgreSQL regression Cobertura report |
| `dotnet\telemetry\` | Telemetry foundation Cobertura report |
| `dotnet\azure-monitor\` | Azure Monitor adapter Cobertura report |
| `dotnet\azure-blob\` | Azure Blob Object Store adapter Cobertura report |
| `node\` | `lcov.info` and `summary.txt` containing live test output and the native coverage table |
| `source.json` (CI only) | Tested checkout SHA, PR head SHA, run ID, and run attempt |

Each runner clears only its own report directory before collecting, rejects
missing or unexecuted production coverage, and preserves failing test status.
CI replaces plain test steps with these collectors rather than running suites
twice. Its summary and downloadable artifact are published even after a failed
step when available; partial reports do not turn a failed run green. Reports
remain in GitHub Actions for 30 days, with no external analytics upload.

The table retains the measured seven-library foundation snapshot before the
consumer inbox change, except for Persistence.Postgres, which is updated from the
published relay candidate's `npm run coverage:dotnet` report, before the
cancellation corrections. For current combined and
other-library numbers, read that generated report rather than interpreting the
historical rows as one contemporaneous run.

| Scope | Lines | Branches | Methods/functions |
| --- | --- | --- | --- |
| .NET combined | 669/698 (95.8%) | 351/422 (83.1%) | 131/138 (94.9%) |
| Abstractions | 133/139 (95.6%) | 68/72 (94.4%) | 68/73 (93.1%) |
| Providers | 126/144 (87.5%) | 102/148 (68.9%) | 17/18 (94.4%) |
| Persistence.Postgres (published relay candidate) | 263/268 (98.1%) | 84/94 (89.3%) | 32/33 (96.9%) |
| Secrets.AzureKeyVault | 99/99 (100%) | 82/86 (95.3%) | 12/12 (100%) |
| Telemetry | 27/27 (100%) | 6/10 (60%) | 2/2 (100%) |
| Telemetry.AzureMonitor | 36/36 (100%) | 18/22 (81.8%) | 2/2 (100%) |
| ObjectStore.AzureBlob | 32/32 (100%) | 8/8 (100%) | 5/5 (100%) |
| Node release validator | 95.92% | 92.59% | 100% |

These are a starting observation, not a required percentage or a claim of
behavioral completeness. Read the report for the exact tested candidate as
source and tests change. Instrumented line, branch, and method/function coverage
does not prove integration correctness, Azure connectivity, or platform E2E
acceptance; the distinct test layers above still apply.

## When Azure is added

The existing Node CI collector also runs the Azure tooling regressions and
offline Bicep/Kustomize checks. Its coverage report still measures release tooling.
The Azure checks require no login, secrets, subscription, or cluster connection.

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
