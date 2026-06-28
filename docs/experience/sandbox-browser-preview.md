# Sandbox browser preview

Sometimes an agent starts a **server inside its sandbox pod** — a dev server, a freshly built web app, an
API it stood up, a debug endpoint — and you want to actually *open it in your browser* and look. Because
each run executes in its own isolated pod, that server isn't reachable by default. The **sandbox browser
preview** is the supported way to reach it: a live preview served at a unique HTTPS URL that routes straight
to the run's own pod, scoped to that one run.

This page walks through the user experience. For the API see the
[reference](../reference/sandbox-browser-preview.md); for how the proxy works under the hood see the
[deep dive](../deep-dive/sandbox-browser-preview.md).

## When the Preview button is available

A **Preview** button appears in the run/execution view ([`WorkflowRunPage.tsx:822`](../deep-dive/sandbox-browser-preview.md#source)) **only** when **both** are true:

- the run is using the **Kubernetes sandbox** (the run's `sandbox.selected` event reports backend
  `kubernetes-sandbox-claim`), **and**
- the run is **still active**.

On local/dev sandbox backends, or after the run has finished, the button is not shown — there is no claim
pod to route into. The button sits in the run header.

## Step by step

1. **Click Preview.** The **Sandbox Preview** dialog opens.
2. **Pick a port.** Enter the port your app is listening on *inside* the sandbox pod. The **Target port
   (inside sandbox)** field defaults to `3000`. It must be within the allowed preview range
   (**3000–9000** by default); other values are rejected.
3. **Start.** Click **Start**. The app calls `POST /api/runs/{runId}/sandbox/port-forward` with that port.
   The API resolves the run's bound pod from cluster state and provisions a per-preview HTTPRoute and
   ClusterIP Service wiring the shared preview gateway to your pod.
4. **See it become active.** The dialog confirms **"Preview active for port {target_port} on pod
   {pod_name}"** and shows the session id.
   - When a `preview_url` is returned (the normal case in-cluster), the dialog embeds the live preview in an
     **iframe** (with `referrerPolicy="no-referrer"`) and offers an **Open preview** button that opens it in
     a new tab.
   - On a local/dev backend where the gateway path is off, the dialog notes that no proxied preview URL was
     returned.
5. **It stays alive while you watch.** While the dialog/preview is open the app pings a keepalive every
   ~60 seconds, sliding the preview's idle timeout. Stop watching and the preview lapses on its own.
6. **Stop when done.** Click **Stop** to tear it down (`DELETE` on that session). **Close** just dismisses
   the dialog.

## Agent-initiated preview (with your approval)

An agent can also open a preview **for you**, mid-run, when it has just started a server and wants to show
you the result — without you opening the dialog or typing a port. The agent calls a `start_preview(port)`
tool; instead of exposing the server silently, that request raises a **human-in-the-loop approval** on the
run timeline:

- A **"the agent wants to expose a preview server on port N"** approval card appears (the same kind of card
  used for the agent's URL-fetch requests). **Approve** it and the agent gets back the live `preview_url`;
  the preview behaves exactly like one you started yourself (same URL, same auto-expiry, same Stop).
- If you don't approve within ~5 minutes the request lapses and the agent is told the preview was not
  granted.
- The agent can only ever request a preview for **its own run** — the run is bound server-side, so a
  `start_preview` call can't reach another run's pod.

Operators running automated demos can set `SANDBOX_PREVIEW_AUTO_APPROVE=true` (or the per-run
auto-approve-tools option) to grant these requests automatically; in normal use the approval stays in your
hands.

## What to expect

- **Kubernetes-only.** The preview routes into the run's own sandbox pod. On local/dev runs there is no
  claim pod, so the button doesn't appear — the same "this is a cluster feature" boundary as the pod pill.
- **A real, public HTTPS URL.** Unlike the older loopback design, the preview is reachable at
  `https://{token}-preview.{ZoneSuffix}`. The URL is an **unguessable capability link** (128-bit token):
  anyone with the URL can open it, so don't share it. It is short-lived and auto-expires.
- **Scoped to this run's pod.** A preview reaches only the run's own sandbox pod, never another run's.
  Keepalive and stop verify the token actually belongs to the run before acting.
- **Auto-expiry.** A preview is reaped after **30 minutes** idle (no keepalive), after a hard **8-hour**
  cap, or once its pod is gone — whichever comes first. By default it survives the run ending (you can keep
  previewing a finished run's artifact) until one of those limits or an explicit **Stop**.
- **Bind to `0.0.0.0`.** For a server to be previewable it must listen on all interfaces, not just
  `127.0.0.1`. When the feature is enabled, agents are told this automatically.

## Related reading

- [Sandbox browser preview — Reference](../reference/sandbox-browser-preview.md) — routes, DTO fields, config, status codes.
- [Sandbox browser preview — Deep Dive](../deep-dive/sandbox-browser-preview.md) — the reverse proxy, lifecycle, and cleanup.
- [Sandbox pod execution experience](./sandbox-pod-execution.md) — the pod pill and the pod-per-run model.
- [Runs, board & watch](./runs-board-watch.md) — where the run/execution view lives.
