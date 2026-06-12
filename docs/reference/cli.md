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
[run.outcome]        Achieved: true — completed all requested changes
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

### `scaffolder run artifacts <run-id>`

Opens an interactive artifact browser for a run. Lists all changed files in the worktree (filtered by a selection prompt: All / Committed / Uncommitted / Last commit), then lets you select a file and view its diff in a color-coded panel. Added lines render in green and removed lines render in red. Hunk headers and metadata lines are dimmed. After viewing a file, you are prompted to view another or exit.

```text
scaffolder run artifacts <run-id>
```

The command exits immediately if no changes are found in the run. For in-progress runs, the file list reflects the current worktree state with a notice that the run is still active.

### `scaffolder project`

Commands for managing projects. A project groups related runs under a named workspace with a fixed working directory, default branch, and optional provider defaults.

#### `scaffolder project create`

Creates a project.

```text
scaffolder project create --name <name> --dir <path> [--origin blank|github] [--source-repo <owner/repo>] [--provider <provider>] [--model-copilot <id>] [--model-foundry <id>]
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--name <name>` | Yes | Display name for the project |
| `--dir <path>` | Yes | Absolute path to the working directory |
| `--origin blank\|github` | No | `blank` (default) uses the directory as-is; `github` clones the repository first |
| `--source-repo <owner/repo>` | When `--origin github` | GitHub repository to clone |
| `--provider <provider>` | No | Default provider: `github-copilot` or `microsoft-foundry` |
| `--model-copilot <id>` | No | Model override for the GitHub Copilot provider |
| `--model-foundry <id>` | No | Model override for the Microsoft Foundry provider |

On success prints the project id, name, working directory, origin, and default branch.

#### `scaffolder project list`

Lists all projects with their id, name, availability, and working directory.

```text
scaffolder project list
```

#### `scaffolder project show <project-id>`

Prints all fields for a single project: name, id, origin, source repository (if any), working directory, default branch, owner, provider and model defaults, availability, state, and creation date.

```text
scaffolder project show <project-id>
```

#### `scaffolder project configure <project-id>`

Updates the provider and model defaults for a project.

```text
scaffolder project configure <project-id> [--provider <provider>] [--model-copilot <id>] [--model-foundry <id>]
```

Options:

| Option | Description |
| --- | --- |
| `--provider <provider>` | New default provider: `github-copilot` or `microsoft-foundry` |
| `--model-copilot <id>` | Model override for the GitHub Copilot provider |
| `--model-foundry <id>` | Model override for the Microsoft Foundry provider |

#### `scaffolder project rename <project-id>`

Renames a project.

```text
scaffolder project rename <project-id> --name <new-name>
```

#### `scaffolder project relink <project-id>`

Updates the working directory path for a project after moving the repository to a new location.

```text
scaffolder project relink <project-id> --dir <new-path>
```

#### `scaffolder project delete <project-id>`

Deletes the project record. Does not touch the working directory or git history. Requires `--confirm`.

```text
scaffolder project delete <project-id> --confirm
```

Without `--confirm` the command prints a warning and exits 1.

#### `scaffolder project run <project-id>`

Starts a run within a project. Prints the new run id and a `scaffolder run watch` command you can use to follow the run.

```text
scaffolder project run <project-id> --task <text> [--provider <provider>] [--model <id>] [--base-branch <branch>]
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--task <text>` | Yes | Task description for the agent |
| `--provider <provider>` | No | Provider override for this run |
| `--model <id>` | No | Model override for this run |
| `--base-branch <branch>` | No | Branch override; falls back to the project default branch |

#### `scaffolder project runs <project-id>`

Lists all runs for a project with their id, status, start time, and task description.

```text
scaffolder project runs <project-id>
```

### `scaffolder github`

Commands for GitHub authentication. These manage the OAuth token used by the GitHub Copilot provider.

#### `scaffolder github sign-in`

Starts the GitHub device authorization flow. Prints the verification URL and one-time code, then polls until the user completes the authorization or the code expires.

```text
scaffolder github sign-in
```

On success prints the authenticated GitHub username. On expiry or denial prints a message and exits 1.

#### `scaffolder github sign-out`

Deletes the stored GitHub token.

```text
scaffolder github sign-out
```

#### `scaffolder github status`

Prints the current authentication state: signed in (with username), signed out, or never signed in.

```text
scaffolder github status
```

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

### `scaffolder team`

Commands for managing a project's agent team. These commands cover the full casting workflow — from selecting roles through to committing the resulting `.squad/` files — as well as day-to-day roster management.

#### `scaffolder team scenarios`

Lists the available scenario groupings that can be used with `team cast --scenario`.

```text
scaffolder team scenarios
```

Prints each scenario's id, name, and description.

#### `scaffolder team cast`

Creates a casting proposal and prints the proposal id. For `--goal` and `--analyze` modes, the command streams the model run events while the proposal is being generated, then prints the proposal id when the run completes.

```text
scaffolder team cast --scenario <id> [--universe <name>]
scaffolder team cast --goal <text> [--model <id>]
scaffolder team cast --analyze [--model <id>]
```

Options:

| Option | Mode | Description |
| --- | --- | --- |
| `--scenario <id>` | scenario | Cast from the named scenario grouping |
| `--universe <name>` | scenario | Optional thematic universe applied to agent personas |
| `--goal <text>` | free_text | Natural-language description of the team goal |
| `--analyze` | analysis | Let the model analyze the project and propose roles |
| `--model <id>` | free_text, analysis | Model override for this proposal run |

After a proposal is created, use `team proposal show`, `team proposal amend`, or `team proposal confirm` to review and act on it.

#### `scaffolder team proposal show <id>`

Prints the proposal details: mode, status, and the list of proposed roles with their descriptions.

```text
scaffolder team proposal show <id>
```

#### `scaffolder team proposal amend <id>`

Opens an interactive editor to amend the proposed roles. Prompts you to add, remove, or modify roles before confirming.

```text
scaffolder team proposal amend <id>
```

#### `scaffolder team proposal confirm <id>`

Confirms a proposal and writes the team to `.squad/`. If an existing team is detected, the CLI prompts for the intent: replace the team entirely (`new`), add the proposed roles to the existing team (`augment`), or rewrite all charters using the proposed configuration (`recast`).

```text
scaffolder team proposal confirm <id>
```

#### `scaffolder team proposal reject <id>`

Rejects a proposal. No `.squad/` files are written or modified.

```text
scaffolder team proposal reject <id>
```

#### `scaffolder team show`

Prints the current team roster: member names and their charter paths.

```text
scaffolder team show
```

#### `scaffolder team charter show <name>`

Prints the raw Markdown charter for the named team member.

```text
scaffolder team charter show <name>
```

#### `scaffolder team charter edit <name>`

Opens the named member's charter in your default editor.

```text
scaffolder team charter edit <name>
```

#### `scaffolder team member add`

Prompts for a member name and role description, then adds the member to the team and creates an initial charter file.

```text
scaffolder team member add
```

#### `scaffolder team member remove <name>`

Retires the named team member and removes their `.squad/` directory.

```text
scaffolder team member remove <name>
```

#### `scaffolder team member rerole <name>`

Prompts for a new role description and updates the named member's charter.

```text
scaffolder team member rerole <name>
```

#### `scaffolder team sync status`

Shows the pending uncommitted changes in the project's `.squad/` directory and the current change set hash.

```text
scaffolder team sync status
```

#### `scaffolder team sync commit`

Commits the pending `.squad/` changes to the repository. Fetches the current change set hash automatically before committing.

```text
scaffolder team sync commit [--message <text>]
```

Options:

| Option | Description |
| --- | --- |
| `--message <text>` | Commit message. A default message is used when omitted. |

If the change set has shifted between the status check and the commit, the server returns a conflict and the CLI reports it; run `team sync status` again and retry.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Missing configuration, missing arguments, API error, or terminal merge conflict |
| `2` | Retriable approve failure — the run stays at the review gate; fix the reported condition and approve again |

API errors print the HTTP status code and response body.
