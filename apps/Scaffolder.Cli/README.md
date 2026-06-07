# Scaffolder CLI

A terminal client over the Scaffolder backend API. It submits runs, streams a
run's steps as they happen, shows run details, and records your review decision
before any change is merged. The CLI holds no run logic of its own; every action
is an API call.

## Configuration

The CLI reads two environment variables:

| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `SCAFFOLDER_API_URL` | no | `http://localhost:5000` | API base URL |
| `SCAFFOLDER_API_KEY` | yes | none | Bearer key sent on every request |

The same build works against a local backend or a hosted one; only the URL and
key change.

PowerShell:

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

### scaffolder run submit

Prompts for the run details, submits the run, then starts streaming its events.

Prompts, in order:

1. Repository path
2. Originating branch
3. Task description (type one or more lines, then an empty line to finish)
4. Model source (select `github-copilot` or `microsoft-foundry`)

On a successful submit the run id and status are printed and the stream begins.
If the run reaches the review gate you are asked to approve the changes inline.

### scaffolder run watch &lt;run-id&gt;

Connects to the run's event stream and prints each event as it arrives. The
connection resumes automatically after a drop, sending the last event id so the
backend replays only what you missed; duplicate events are ignored.

Event lines are labelled by type, for example:

```
[run.started]        Run abc12345 started (github-copilot)
[agent.message]      Looking at the source files
[tool.call]          read_file: src/main.cs
[tool.result]        OK src/main.cs (1024 bytes)
[tool.rejected]      REJECTED ../outside.txt: path traversal not permitted
[tool.error]         ERROR src/missing.cs: File not found
[review.requested]   Awaiting review (tree: abc123def)
[run.completed]      Run complete after 7 steps
[run.failed]         Run failed: the reason
[run.bounded]        Run bounded: step-count limit reached after 12 steps
```

When the run requests review, you are asked `Approve changes?`. Answering
records your decision through the API. Watching ends once the run fails, is
bounded, or a review decision and any resulting merge have been recorded.

### scaffolder run review &lt;run-id&gt;

Fetches the run and prints its diff, with added lines in green and removed lines
in red. Select `Approve` or `Decline`. The decision is sent to the API and the
resulting status and merge result are printed. A run only accepts a decision
once it has completed and has no decision yet.

### scaffolder run show &lt;run-id&gt;

Fetches the run and prints its details in a table: run id, status, model source,
start and end times, step count, and whether a diff is available.

## Exit codes

`0` on success. `1` when configuration is missing, an argument is missing, or the
API returns an error. API errors are printed with the status code and the
response body.
