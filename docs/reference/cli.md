# CLI reference

The Scaffolder CLI is a terminal client over the backend API. It submits runs, streams live events, shows run details, and records your review decision before anything merges. The CLI holds no run logic of its own.

## Configuration

The CLI reads two environment variables:

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `SCAFFOLDER_API_URL` | No | `http://localhost:5000` | API base URL |
| `SCAFFOLDER_API_KEY` | Yes | none | Bearer key sent on every request |

PowerShell example:

```powershell
$env:SCAFFOLDER_API_URL = "http://localhost:5000"
$env:SCAFFOLDER_API_KEY = "dev-local-key"
```

## Build and run

```powershell
dotnet build apps/Scaffolder.Cli/Scaffolder.Cli.csproj
dotnet run --project apps/Scaffolder.Cli -- <command>
```

In the examples below, `scaffolder` stands for `dotnet run --project apps/Scaffolder.Cli --`.

## Commands

### `scaffolder run submit`

Prompts for the run details, submits the run, and starts streaming events immediately.

Prompts, in order:

1. Repository path
2. Originating branch
3. Task description, one or more lines ending with an empty line
4. Model source: `github-copilot` or `microsoft-foundry`

On success the CLI prints the run id and initial status, then switches to live watch mode. If the run reaches the review gate in an interactive session, the CLI asks whether you want to approve the changes.

### `scaffolder run watch <run-id>`

Connects to the run's event stream and prints each event as it arrives. The client reconnects after a drop, sends the last seen sequence through `Last-Event-ID`, and ignores duplicate events. Reconnection works while the run's entry is retained in the server's memory; after a process restart only the final result is available.

Example output:

```text
[agent.message.delta] {"delta":"Looking at the source files","messageId":"turn-0"}
[tool.call]          read_file: src/main.cs
[tool.result]        OK: namespace Demo { public static class Program { ... } }
[tool.error]         ERROR: Path '../outside.txt' rejected: resolves outside the sandbox boundary
[review.requested]   Awaiting review (tree: abc123def)
[run.completed]      Run complete after 7 steps
```

`agent.message.delta` events carry raw JSON because the renderer has no dedicated case for them. `agent.message` appears only when a turn produced no token deltas.

Watching stops when the run reaches a client-terminal event such as `run.failed`, `run.bounded`, `merge.completed`, `merge.failed`, or `review.declined`.

### `scaffolder run review <run-id>`

Fetches the run, prints its diff, and prompts you to approve or decline. Added lines render in green and removed lines render in red. The command then submits the decision through the API.

On a clean approval the command prints `Merged successfully.` and the `merge_result` string (e.g. `merged:34c09ee...`), then exits 0.

If the approve cannot proceed because of a retriable precondition — uncommitted local changes, staged files, untracked files that would be overwritten, or an in-progress merge in the working tree — the server returns the reason and the CLI prints it along with a prompt to fix the issue and run approve again. This exits with code 2, leaving the run at the review gate.

If the merge reaches a terminal conflict the CLI prints `Merge failed.`, the reason, and a note that the worktree has been preserved for manual resolution. This exits with code 1.

### `scaffolder run show <run-id>`

Fetches the run and prints a table with the run id, status, model source, start and end times, step count, and whether a diff is available.

### `scaffolder sandbox-policy`

Commands for reading and writing the per-project sandbox execution policy. Policies control whether shell execution is enabled, which commands require human approval, and output handling. See [sandbox-setup.md](sandbox-setup.md) for full setup instructions.

#### `scaffolder sandbox-policy get`

Fetches and prints the sandbox policy for a repository.

```text
scaffolder sandbox-policy get --repository-path <path>
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--repository-path <path>` | Yes | Absolute path to the repository |

Output is printed as a JSON object. If no policy has been stored for the repository, the default policy is returned.

Example:

```powershell
scaffolder sandbox-policy get --repository-path C:\repos\myproject
```

```json
{
  "repository_path": "C:/repos/myproject",
  "shell_enabled": true,
  "require_approval_for_all_shell": false,
  "redact_pii": true,
  "max_output_bytes": 4194304,
  "allowed_repository_roots": [],
  "destructive_command_patterns": ["rm -rf", "del /s", "format ", "mkfs", "dd if=", "git push --force", "git reset --hard"]
}
```

#### `scaffolder sandbox-policy set`

Updates fields on the sandbox policy for a repository. The command fetches the current policy, applies the provided options, and writes the result back.

```text
scaffolder sandbox-policy set --repository-path <path> [options]
```

Options:

| Option | Type | Description |
| --- | --- | --- |
| `--repository-path <path>` | string | Required. Absolute path to the repository. |
| `--shell-enabled <true\|false>` | bool | Whether `run_command` is available for runs on this repository. `false` disables shell regardless of whether the host has a real isolation backend. |

Example — disable shell for a project:

```powershell
scaffolder sandbox-policy set --repository-path C:\repos\myproject --shell-enabled false
```

On success the command prints the updated policy as JSON and exits 0. API errors print the HTTP status and response body and exit 1.

Additional policy fields (`destructive_command_patterns`, `require_approval_for_all_shell`, `redact_pii`, `max_output_bytes`, `allowed_repository_roots`) can only be set directly through the API (`PUT /api/sandbox-policy`) for now. The CLI exposes `--shell-enabled` as the most common operator action.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Missing configuration, missing arguments, API error, or terminal merge conflict |
| `2` | Retriable approve failure — the run stays at the review gate; fix the reported condition and approve again |

API errors print the HTTP status code and response body.
