# Agentweaver project workflow routing

Read the repository README and its linked v1 architecture/testing guides for canonical
commands. `AGENTS.md` and `CONTRIBUTING.md` are not present on this branch.

The project extension `agentweaver-issue-to-merge` is discoverable in Agentweaver
sessions; discovery does **not** automatically run it for every prompt. Use the
`run_dynamic_workflow` tool only when the user explicitly requests an issue-to-merge
workflow, or a matching skill/slash command specifically instructs its use. An
issue-backed request for complete implementation, local reviews, PR, CI, and
serialized merge is eligible. Explanations, read-only investigations, and
release/deployment requests are not. Surface the intended workflow use to the
user, respect native workflow approval, and do not invent resource ceilings.

For authorized delivery, supply the concrete task, positive issue number, target
branch (`v1` by default), existing `type:` and `area:` labels, milestone, and
`mode: "deliver"`; omission of mode runs a no-mutation rehearsal. Inspect the
durable run result and resume the same run ID after a resolvable interruption.
Do not treat a completed rehearsal as delivery proof. The run is bound to its
initiating session, not shared across project sessions. There is no auto-run hook.

For Agentweaver 1.x API acceptance, exercise the native .NET CLI apphost with
`agentweaver test --input <manifest> --output <new-evidence-file>`. Use the
self-contained apphost published from source, or the exact extracted apphost
when validating a distribution candidate. Do not substitute targeted `rest`
requests, unit or controlled tests, or the Node runner for API journey
acceptance. The Node `harness:p1 -- --surface api` entrypoint is compatibility
delegation only: if used, set `AGENTWEAVER_CLI_PATH` to the exact apphost; it
does not fall back to a Node API runner. `test` takes its target from the
manifest, not target flags or environment variables. `--interactive` explicitly
permits browser authorization and should be used only when authorized; a live
test creates and cleans up API fixtures, so run it only against an explicitly
approved non-production target.
A successful `--dry-run` validates input without network, authentication, or
browser launch and writes no evidence, even with `--interactive`; validation
failure may write a blocked result to the requested output. See the [testing
guide](../docs/guide/testing.md) and [CLI guide](../docs/guide/cli.md) for
commands and target requirements.

## Release and deployment

For v1 package, image, and Kubernetes release operations, follow the
[release workflow runbook](../releases/README.md). Keep NuGet package
publication, container image publication, and workload deployment distinct;
do not revive image archives, publication receipts, source-claim gates, or
manual migration-token handoffs.

## Documentation as you go

Update affected v1 documentation and diagrams in the same PR as each behavior,
interface, provider, model-support, or configuration change. Run
`npm run docs:build`, `npm run docs:check-links`, and the diagram checks.
