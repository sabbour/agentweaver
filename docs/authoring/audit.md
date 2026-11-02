# Current documentation audit and rewrite priorities

## Scope and evidence

This is a representative audit, not a claim that every article is poor.
It inspected the current `origin/v1` snapshot
[`7aa88911ce56dbe5eebe136bd2181ed36a8f720c`](https://github.com/sabbour/agentweaver/tree/7aa88911ce56dbe5eebe136bd2181ed36a8f720c/docs)
in a fresh worktree on 2026-11-02. Paths and section names below identify
passages in that snapshot, not root-checkout or 0.x behavior. These findings
concern reader outcomes; they do not establish which services are deployed.

The recurring problem is **mixed purpose and audience**, not merely sentence
length. New users need a path to a useful outcome; operators need safe,
discriminating recovery; developers need coherent views and trade-offs.
Source inventories and delivery evidence answer different questions.

## Findings

P1 means reader correctness or a missing primary journey. P2 means significant
findability or task friction. “Likely improvement” is a judgement about reader
effort, not a demonstrated false claim.

| Priority / class | Location and concrete example | Reader harm | Follow-up |
| --- | --- | --- | --- |
| P1 / definite conflict | [Foundation architecture](../architecture/overview.md), paragraph beginning “The repository does not contain…” says there is no product MCP server. [System overview](../deep-dive/00-system-overview.md), “The system at a glance” and “Follow a request,” describe an MCP client and `/mcp`; [MCP tools reference](../reference/mcp-tools.md), “Connect and authenticate,” instructs connection. The [landing page](../index.md), “Reference,” advertises that surface. | A reader cannot determine whether these are supported instructions, a source candidate or a proposal. A source folder cannot resolve the public availability claim by itself. | **Lead: reconcile supported entrypoints**, with the owning service/release evidence; align the landing, reference, architecture and deep-dive statements. Do not silently choose one as true. |
| P1 / definite journey gap | [Landing page](../index.md), “Get started,” offers source building and library composition. [Quickstart](../guide/quickstart.md) explicitly ends at local checks and says it does not start a product platform. [README](../../README.md), “Standalone CLI,” sends the reader to a source publish procedure. | A prospective user seeking an install-to-first-use result has no short path here. The quickstart's scope is honest; changing its title alone cannot create a supported product journey. | **Lead: define the supported first outcome** with release/operation prerequisites, then publish the shortest verified path or an explicit availability boundary. Preserve the source-build task for contributors. |
| P2 / definite discoverability gap | [Site configuration](../.vitepress/config.ts) defines a `/deep-dive/` sidebar, but top navigation has no deep-dive entry. [Landing page](../index.md) links architecture, not the [deep-dive index](../deep-dive/README.md). | The cross-service runtime/state explanations are harder to discover than the component inventory, despite answering different reader questions. This is a missing direct entry, not proof the pages are completely unreachable. | **Lead: add a reader-facing architecture/deep-dive route** from the appropriate landing page and test the journey. |
| P2 / likely improvement | [Configure providers](../guide/configure-providers.md), “Review the current provider catalog,” “Understand source candidates,” and “Configure a provider.” The five generic steps say to set the owning service's options but do not complete one concrete provider task. Later sections also cover upstream identity, model bindings and spend budgets. | Readers must select among several unrelated goals and leave the article to determine the actual edit, validation and result. The content is useful reference/explanation, but the how-to label overpromises a bounded procedure. | **Lead: split one supported configuration task from provider explanation/reference**; link exact per-owner instructions and keep security/resource-identity constraints. |
| P2 / likely improvement | [Runtime configuration](../reference/runtime-configuration.md) opens with “OpenPullRequest workflow executor,” “Scribe workflow executor,” and issue-sync TLS prose before “Configuration rules.” The rules also narrate successful PostgreSQL bootstrap and independent verification of nine runtime snapshots. | An operator searching for a key/default must pass integration narratives and environment-specific delivery evidence. Those receipts do not explain the setting's general contract. | **Lead: restore lookup-first configuration reference**, separating exact settings from how-to operations and environment history. Do not remove real validation/default information. |
| P2 / likely improvement | [Foundation architecture](../architecture/overview.md) places P1 invocation binding, no-send/cursor proof slots, receipts and fixture limits before “Component overview.” [Deep-dive system overview](../deep-dive/00-system-overview.md) is clearer about owners and flows but is a separate starting point. | Developers meet internal acceptance vocabulary before a mental model of boundaries and responsibility. Repeated verification caveats obscure the architecture rather than proving it. | **Lead: reshape the architecture overview around arc42 context/strategy/building blocks**, linking canonical runtime/deployment/ADR views and keeping one precise state boundary. |
| P2 / likely improvement | [CLI guide](../guide/cli.md) combines source publishing, target configuration, credential behavior, API discovery/invocation, raw requests and acceptance use. “Discover and invoke API operations” mixes commands, transport/security rules and P1 setup. | A new CLI reader must distinguish local inspection, network discovery, sign-in and mutation inside one long topic. The content should not be deleted; goals need their own entrypoints. | **Lead: introduce focused CLI tasks and a factual command reference** after parallel CLI packaging work. The small [local-help exemplar](../guide/find-cli-commands.md) demonstrates the standard, not a complete CLI rewrite. |
| P2 / likely improvement | [Troubleshooting](../guide/troubleshooting.md), “Resolve common failures,” recommends restoring resource identity for a changed binding and restarting a reviewed Deployment after ConfigMap edits. “Collect a safe diagnostic” comes later; immutable ConfigMap handling is in the reference. | These are plausible actions, but the table does not give enough observations to distinguish a legitimate resource change, an immutable map, or an unrelated failure before acting. This is a diagnostic-guidance gap, not evidence that the remedies are always wrong. | **Lead: turn high-risk rows into symptom/check/conditioned remedy flows**, linking the existing immutable-map procedure and reconciliation boundaries. |

Choosing a different heading style or preferring one long article over two is
not, by itself, a defect. The changes above are justified by missing outcomes,
contradictory claims, or observable lookup/navigation friction—not taste.

## Comparison with the reference sites

The [primary-source comparison](./sources.md) supports four practical changes:

- **Microsoft Learn:** task headings, required starting conditions and ordered
  actions make the next step apparent. Its CLI reference separates syntax,
  arguments and effects. Agentweaver already does parts of this well in the
  source quickstart; use that discipline beyond build/test tasks.
- **GitHub Docs:** purpose-specific articles and a predictable content model
  reduce navigation decisions. Keep canonical facts in one home instead of
  making readers reconcile inventories and deep dives.
- **The Good Docs Project:** separate learning, doing, lookup and diagnosis.
  Reusable author prompts should produce a useful result, not uniform pages
  with empty sections.
- **arc42:** explain drivers, context, static blocks, runtime and deployment
  as different views, then connect decisions, measurable qualities and risks.
  Do not simply rename the existing component inventory “architecture.”

Preserve the useful material: the quickstart's numbered PowerShell procedure,
honest scope boundaries, exact reference keys and error codes, troubleshooting
secret-handling cautions, and the deep dives' state ownership and failure
explanations. This audit does not justify deleting safety qualifiers.

## 0.x prior art: structure only

Git-ref inspection used `origin/dev` at
[`fa1a8c5cf125acd82be92ed573b4f092826747cd`](https://github.com/sabbour/agentweaver/tree/fa1a8c5cf125acd82be92ed573b4f092826747cd/docs);
no legacy checkout was opened or modified.

- `docs/guide/index.md` starts by explaining what a user does and links named
  concepts. Reuse the reader-first introduction, not its 0.x workflow, board
  heartbeat, provider or MCP assumptions.
- `docs/deep-dive/README.md` groups subsystems by reader interest. The current
  v1 deep-dive index already retains this useful pattern; improve entrypoints
  rather than replace the index.
- `docs/guide/runs.md` begins with starting a run but embeds extensive provider
  internals. That is a caution against copying legacy pages wholesale, not a
  model for the new task template.

## What this delivery does and does not resolve

[Standards delivery](https://github.com/sabbour/agentweaver/issues/2506) adds the
authoring guide, original templates, source comparison, this audit and one
worked task. The [broader reader-doc/comment repair](https://github.com/sabbour/agentweaver/issues/2204)
remains open. **Lead owns triaging the named rewrite follow-ups above** with
the coordinator; they are not claims of completed rewrites or assignments of
new product work. First reconcile availability and the supported first outcome,
then navigation/task separation, reference lookup and architecture views.

### Worked-example source check

The [local CLI-help task](../guide/find-cli-commands.md) uses existing Windows
publish paths and options from [the CLI guide](../guide/cli.md#build-and-run-from-source).
Source verification includes:

- `tools/Agentweaver.Cli/Program.cs`: constructs clients and authentication
  before calling `RunAsync`; construction is not a request.
- `Auth/CliAuthService.cs`, `Vault/ICliCredentialVault.cs`,
  `Vault/WindowsCredentialVault.cs`, `Vault/CliCredentialLock.cs`,
  `Auth/SystemBrowser.cs` and `Auth/LoopbackOAuthCallback.cs` under that CLI
  project: Windows construction does not read credentials, start a callback
  listener or open a browser. Credential-lock construction resolves the
  per-user local path; it does not create lock files.
- `Commands/CliApplication.cs`, `RunAsync`, `HelpText` and `VersionText`:
  help/version return before target configuration and network operations.
- `tests/Agentweaver.Cli.Tests/CliApplicationTests.cs`,
  `HelpAndVersionWorkWithoutTargetConfiguration`: checks successful codes,
  command output, source metadata and an empty error stream at the application
  entrypoint, not a published executable.

This is a source-verified worked example. Its executable and unit test were
**not run for this documentation change**; it makes no release, deployed
service, browser, provider or acceptance claim.
