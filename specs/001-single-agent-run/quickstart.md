# Quickstart Validation Guide: Single-Agent File-Editing Run

## Prerequisites
- Node.js 22 LTS
- Git installed and available on PATH
- Access credentials for:
  - GitHub Copilot SDK
  - Microsoft Foundry

## Setup
1. Install dependencies:
   - `npm install`
2. Start backend API:
   - `npm run dev --workspace apps/api`
3. Start CLI client (optional watcher):
   - `npm run dev --workspace apps/cli`
4. Start Web client:
   - `npm run dev --workspace apps/web`

## Validation Scenarios

### 1) Submit run from CLI and stream steps
1. Submit task from CLI with originating branch and model source.
2. Observe stream: agent messages, tool calls, tool results in sequence.
3. Confirm run reaches a terminal state.

Expected:
- First step appears in under 10 seconds.
- Stream order remains monotonic by `sequence`.

### 2) Sandbox path rejection
1. Use a test task that attempts absolute path or `../` traversal.
2. Observe tool result for rejection.

Expected:
- Tool operation rejected with path-escape error.
- No file outside artifact directory is read or modified.

### 3) Cross-client parity (CLI + Web)
1. Start one run.
2. Watch same run from both CLI and Web.

Expected:
- Both clients display the same ordered step stream and same terminal status.

### 4) Model source selection enforcement
1. Submit one run with `copilot_sdk`.
2. Submit one run with `microsoft_foundry`.
3. Attempt unsupported model source.

Expected:
- First two runs accepted with recorded provider.
- Unsupported source rejected at submission.

### 5) Review and merge gate
1. Complete a run and inspect diff output.
2. Submit `decline` decision.
3. Complete another run and submit `approve`.

Expected:
- Declined run does not modify originating branch.
- Approved run merges reviewed diff (or reports merge conflict without branch mutation).

## References
- API contract: `specs/001-single-agent-run/contracts/run-api.yaml`
- Step event schema: `specs/001-single-agent-run/contracts/run-step-event.schema.json`
- Data model: `specs/001-single-agent-run/data-model.md`
