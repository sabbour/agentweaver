# Agentweaver Web

A React 19 and Fluent 2 client over the Agentweaver backend API. It submits runs,
streams a run's steps live, shows run details, and records your review decision
before any change is merged. The web client holds no run logic of its own; every
action goes through `src/api/client.ts`.

## Configuration

Configuration comes from Vite environment variables. Copy `.env.example` to
`.env` and set the values:

| Variable | Default | Purpose |
|----------|---------|---------|
| `VITE_API_URL` | `http://localhost:5000` | API base URL |
| `VITE_API_KEY` | empty | Bearer key sent on every request |

The same build runs against a local backend or a hosted one; only these values
change.

## Develop and build

```powershell
cd apps/web
npm install
npm run dev      # start the dev server
npm run build    # type-check and produce a production build
npm run lint     # run eslint
```

## Routes

| Path | Screen | Purpose |
|------|--------|---------|
| `/` | Submit a run | Form to start a run |
| `/watch/:runId` | Watch run | Live event stream for a run |
| `/review/:runId` | Review run | Diff with approve or decline |

## Flows

### Submit a run

The home page form takes a repository path, an originating branch, a task
description, and a model source (`GitHub Copilot` or `Microsoft Foundry`).
Submitting calls the API and navigates to the watch screen for the new run.
Submit is disabled until the path, branch, and task are filled in. API errors are
shown above the form.

### Watch a run

The watch screen streams the run's events over server-sent events, sent through
`fetch` so the bearer key and a resume cursor can be supplied. Each event shows
its type, a relative time, and a summary. The stream reconnects after a drop
using the last event id and ignores duplicate events. A status badge appears for
the terminal state (completed, failed, bounded, merged, approved, or declined).
When the run is waiting for review, a button links to the review screen.

### Review a run

The review screen fetches the run and renders its diff with added lines in green
and removed lines in red, alongside a details table. Approve or Decline sends the
decision to the API and shows the resulting status and merge result.

## Structure

```
src/
  api/
    types.ts        API shapes
    client.ts       fetch-based API client
    apiClient.ts    shared client built from config
    sse.ts          run-stream hook
    eventFormat.ts  event summary and relative time helpers
  components/
    RunSubmitForm.tsx
    RunWatcher.tsx
    RunReview.tsx
    RunDetail.tsx
  pages/
    HomePage.tsx
    WatchPage.tsx
    ReviewPage.tsx
  App.tsx           Fluent provider and routing
  main.tsx          entry point
  config.ts         reads VITE_API_URL and VITE_API_KEY
```
