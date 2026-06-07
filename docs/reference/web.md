# Web UI reference

The Scaffolder web UI is a React 19 and Fluent 2 client over the backend API. It submits runs, streams live events, shows run details, and records your review decision before anything merges. The browser client keeps all run logic in the API layer.

## Configuration

Copy `.env.example` to `.env` in `apps/web`, then set the Vite variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `VITE_API_URL` | `http://localhost:5000` | API base URL |
| `VITE_API_KEY` | empty | Bearer key sent on every request |

## Develop and build

```powershell
cd apps/web
npm install
npm run dev
npm run build
npm run lint
```

`npm run build` type-checks the app and produces a production bundle. `npm run lint` runs ESLint.

## Routes

| Path | Screen | Purpose |
| --- | --- | --- |
| `/` | Submit a run | Form to start a run |
| `/watch/:runId` | Watch run | Live event stream for a run |
| `/review/:runId` | Review run | Diff with approve or decline |

## Flows

### Submit a run

The home page collects the repository path, originating branch, task description, and model source. Submit stays disabled until the path, branch, and task are filled in. On success the app navigates to the watch screen for the new run.

### Watch a run

The watch screen streams events with `fetch`, not `EventSource`, so it can send the bearer key and `Last-Event-ID`. Each event shows its type, a relative time, and a summary. The stream reconnects after a drop and deduplicates by `sequence`.

A status badge marks terminal states such as completed, failed, bounded, approved, declined, or merged. When the run requests review, the screen links you to the review page.

### Review a run

The review screen fetches the run, renders its diff, and shows a details table. Approve or Decline sends the decision to the API and shows the resulting status plus any merge result.

## Structure

```text
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
