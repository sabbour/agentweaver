# Testing strategy

## The mental model

Test each ownership boundary with the smallest real dependency that proves its behavior. Pure tests cover deterministic rules. PostgreSQL tests cover transactions and concurrency. Controlled transports cover external contracts. Deployed acceptance checks the actual source, identity, runtime, and cleanup.

These results are not interchangeable. Compiling a PostgreSQL case does not run it. A controlled browser-page test does not launch a browser. A successful surface journey does not prove every native execution boundary.

## Where this lives

- `tests/` — .NET package and service suites.
- `scripts/api-harness/lib/client.mjs` and `test/` — live OpenAPI discovery and controlled HTTP tests.
- `scripts/ui-harness/lib/client.mjs` and `test/` — injected-page observation and owner receipt checks.
- `scripts/mcp-harness/mcp-client/` and `test/` — MCP SDK transport, discovery, and tool-result tests.
- `scripts/harness-shared/` — target/auth guards, shared journeys, redaction, and acceptance results.
- `scripts/coverage/dotnet.mjs`, `node.mjs`, and `tests/` — coverage collection and receipt guards.
- `coverage.runsettings` and `.config/dotnet-tools.json` — .NET collection and report tooling.
- `.github/workflows/v1-ci.yml` and `v1-docs-ci.yml` — automatic source validation.
- `.github/workflows/v1-integration-images.yml`, `v1-release-pack.yml`, and `v1-partial-publication-continuation.yml` — artifact preparation and controlled publication.

## Core concepts and invariants

### Keep ordinary tests independent of cloud credentials

Provider and identity tests use in-memory state, fake HTTP/Azure transports, and local hosts. Source Control tests use real isolated Git repositories for filesystem behavior and controlled GitHub responses for remote behavior.

Store suites use disposable PostgreSQL through Testcontainers. Docker-compatible container access is required for those cases. Broker browser integration uses Chromium through Playwright. Native AgentHost tests use separately configured native runtime or image fixtures.

Do not replace a required database case with an in-memory provider and report the same evidence. Row locks, advisory locks, transactional rollback, and lease races require PostgreSQL.

### Preserve the actual owner result

Harnesses keep HTTP errors, owner denials, and `202 Accepted` responses distinct from completed work. They record correlation and revision/fence evidence. Redaction runs before durable JSON Lines output; transient raw bodies are not the saved transcript.

Authentication helpers check supplied token metadata and expiry. Server-side signature validation and current owner authorization remain the Broker, Gateway, and service responsibilities.

## Local validation flow

From the repository root, use the canonical build sequence:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
```

Then run the affected test project. For example:

```powershell
dotnet test tests\Agentweaver.Providers.Tests\Agentweaver.Providers.Tests.csproj --no-build --no-restore --configuration Release
```

Use the harness contract suites without contacting a deployed environment:

```powershell
npm run test:harness:api
npm run test:harness:ui
npm run test:harness:mcp
```

The API suite uses controlled loopback HTTP. The UI suite uses an injected-page contract. The MCP suite includes a real SDK connection to a controlled loopback JSON-RPC HTTP server. Its locked dependencies live under `scripts\mcp-harness`.

## API, UI, and MCP journeys

The API client discovers `/openapi/v1.json` and invokes advertised `/api/v1` operations. It rejects unknown operation IDs, extra parameters, cross-origin requests, and redirects.

The UI adapter accepts an already-authorized page on the exact Web origin. It observes real Gateway request/response shapes and validates tenant, actor, gate, and owner receipt bindings. It does not sign in or seed browser storage.

The MCP adapter uses Streamable HTTP at `/mcp`. It reads all tool-discovery pages, rejects duplicate or foreign tools and repeated cursors, and checks the structured owner result. It preserves `isError`; closing the client does not release Environments or archive projects.

`npm run harness:p1` runs the shared surface entry point when explicit target and authorized runtime inputs are supplied. Its result separates `surfaceStatus` from `fullP1Status`. Required deployment-source, image-measurement, and native probes remain blocked until evidence exists. The aggregate exits `0` for passed, `1` for failed, and `2` for blocked.

Native probes cover Storage isolation, network datapath, Sandbox lifecycle, AgentHost readiness, numeric dispatch limits, and restart/duplicate-fence recovery. A policy-object readback or usage query cannot replace those probes.

## Coverage as an artifact

`npm run coverage:dotnet` runs the configured .NET suites against the Release build with TRX and Cobertura collection. It requires nonempty case receipts, one distinct coverage report per suite, and combined reports containing the expected production assemblies. It restores the repository-local report tool and writes under `artifacts/coverage/dotnet/`.

`npm run coverage:node` runs release and Azure tooling tests with Node coverage. It writes LCOV and a text summary under `artifacts/coverage/node/` and requires executed release-validator lines. It is not coverage for every Node file or every harness.

The Web package has its own coverage command. Missing reports fail the relevant guard; a present file alone is not sufficient evidence.

## How CI combines the checks

`v1-ci.yml` runs on `v1` pull requests and pushes. It records tested-source identity, validates release composition, restores locked dependencies, builds .NET, and runs Web coverage, build, and lint. It also builds source-bound Probe, Broker, and AgentHost images, checks the pinned native runtime, and runs isolated native prompt-capacity tests.

The workflow installs Chromium, runs .NET and Node coverage, tests controlled P1 journeys and coverage guards, and checks the issue-to-merge workflow. It uploads source-bound coverage artifacts and publishes a summary. Those controlled journeys are not automatic deployed acceptance.

`v1-docs-ci.yml` builds VitePress, checks links and source coverage, and validates diagram provenance and readability. On a `v1` push it stages a source-bound site artifact.

The integration-image and release-pack workflows are manual artifact workflows, not substitutes for test CI. Preparation and publication are separate jobs. The partial-publication workflow admits only its exact recovery dispatch and missing artifacts; it does not rerun the general test strategy.

## Documentation validation

Public documentation changes run the same local checks used by docs CI:

```powershell
npm run docs:build
npm run docs:check-links
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
```

Update affected public explanations with behavior and contract changes. Keep source mapping and diagrams aligned with the implemented owner boundary, not a proposed future service.

## Related pages

- [Test commands and suite boundaries](../guide/testing.md)
- [Environment suspend testing](../guide/environment-sandbox-testing.md)
- [Release reference](../reference/releases.md)
- [Infrastructure and deployment](./infra-deployment.md)
