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

Run the commands in the [root README](../../README.md#build-and-check-the-foundation).
The PostgreSQL suite requires a running Docker-compatible engine and permission to
pull its test image. Missing Docker, an unavailable image, or a database startup
failure fails the suite; it is not an automatic skip.

Testcontainers is a test dependency, not a supported local product deployment.
The suite disposes only its own database/container. It never cleans up unrelated
containers or contacts production resources.

These tests are **not platform E2E**. There is no deployable v1 service yet, and
this slice does not create an Azure environment.

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
