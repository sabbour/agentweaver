# API Harness

The API harness drives Agentweaver's REST API and captures objective evidence. Persona cores and API adapters are loaded from `../persona-briefs`; end-of-run verdicts are rendered by `../harness-judge`.

## Run

```powershell
npm test
node run-persona.mjs --scenario priya-ticket-triage --persona priya --target https://agentweaver.example.staging.example --token <token> --batch-id batch-1 --seed seed-1
```

`--target` is an alias for the legacy `--base-url`. Legacy fixed scenarios remain supported. The default rung is `scoping`; deeper approval driving remains opt-in through scenario configuration.

Targets are restricted to localhost or staging hosts at `AgentweaverClient` construction. Production additionally requires both `--allow-prod` and `--i-understand-this-targets-production`; this is independent of the TLS-only `--allow-insecure-prod` flag.

The generated verdict uses `agentweaver.persona-judge-verdict/v1`, including its batch/scenario join key and repro provenance. When running as the `Harness` agent, the preferred judging path is agent-native: build the prompt with `node scripts/harness-judge/core.mjs <evidence.json> --prompt-out <prompt.txt>`, dispatch it via the `task` tool to `agent_type: "Judge"` (`.github/agents/judge.agent.md`, `tools: []` — pure text-in/text-out, no file/shell/network access), then parse and persist the result with `scripts/harness-judge/save-verdict.mjs`. See `.github/agents/harness.agent.md`'s "Judging" section for the exact flow.

For headless/CI use with no agent session to dispatch a `task` call from, set `AGENTWEAVER_JUDGE_CMD` to an external judge command consumed by `core.mjs`'s `makeDefaultJudge()`; without it, a schema-valid `CANNOT_DETERMINE` verdict is emitted.

For the interactive tool driver, pass `--session <path>` (or set `AGENTWEAVER_HARNESS_SESSION`) to isolate concurrent sessions. Without it the legacy `session.current.json` location is retained.
