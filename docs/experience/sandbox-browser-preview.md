# Sandbox browser preview

Sometimes an agent starts a **server inside its sandbox pod** — a dev server, a freshly built web app, an
API it stood up, a debug endpoint — and you want to actually *open it in your browser* and look. Because
each run executes in its own isolated pod, that server isn't reachable by default. The **sandbox browser
preview** is the supported way to reach it: a live preview tunnelled from the run's own pod, scoped to that
one run.

This page walks through the user experience. For the API see the
[reference](../reference/sandbox-browser-preview.md); for how the tunnel works under the hood see the
[deep dive](../deep-dive/sandbox-browser-preview.md).

## When the Preview button is available

A **Preview** button appears in the run/execution view (`WorkflowRunPage`) **only** when **both** are true:

- the run is using the **Kubernetes sandbox** (the run's `sandbox.selected` event reports backend
  `kubernetes-sandbox-claim`), **and**
- the run is **still active**.

On local/dev sandbox backends, or after the run has finished, the button is not shown — there is no claim
pod to forward into. The button sits in the run header, next to the **Auto-approve tools** toggle.

![Workflow run page showing the run graph, Preview button, and Auto-approve tools toggle](/screenshots/workflow-run-graph.png)

::: info Screenshot is a placeholder
The AKS-hosted UI isn't published yet, so the image above is a committed placeholder served from
`/screenshots/{name}.png` (`docs/public/screenshots/`). It will be replaced with a real capture once the
preview UI is available to screenshot.
:::

## Step by step

1. **Click Preview.** The **Sandbox Preview** dialog opens. It notes that preview traffic is **proxied
   through the Agentweaver API server**.
2. **Pick a port.** Enter the port your app is listening on *inside* the sandbox pod. The **Target port
   (inside sandbox)** field defaults to `3000`. (It must be `1–65535`.)
3. **Start.** Click **Start**. The app calls `POST /api/runs/{runId}/sandbox/port-forward` with that port,
   and a `kubectl port-forward` tunnel is opened from the run's sandbox pod to a loopback port on the API
   host.
4. **See it become active.** The dialog confirms **"Preview active for port {target_port} on pod
   {pod_name}"** and shows the session id.
   - If the API returned a proxied `preview_url`, the dialog embeds the preview in an **iframe** and offers
     an **Open preview** button (opens it in a new tab).
   - If it did not — the backend does not currently populate `preview_url` — the dialog honestly says
     *"The API server did not return a proxied preview URL."*
5. **Stop when done.** Click **Stop** to tear the tunnel down (`DELETE` on that session). You can run more
   than one preview at a time (up to the per-run cap), each on its own port and its own entry, and stop
   them individually. **Close** just dismisses the dialog without stopping the session.

![Sandbox Preview dialog proxying the running pod preview](/screenshots/sandbox-preview-dialog.png)

::: info Screenshot is a placeholder
As above, this is a committed placeholder image; it will be swapped for a real capture of the Sandbox
Preview dialog once the AKS UI is published.
:::

## What to expect

- **Kubernetes-only.** The preview tunnels into the run's sandbox pod. On local/dev runs there is no claim
  pod to forward, so the button doesn't appear — the same "this is a cluster feature" boundary as the pod
  pill.
- **A loopback port, not a public URL.** The API returns a `local_port` it bound on the API host. Whether a
  browser-openable preview appears in the dialog depends on whether a proxied `preview_url` is returned
  (today it is not).
- **Scoped to this run's pod.** A preview reaches only the run's own sandbox pod, never another run's, and
  is capped — default **3** previews per run and **20** globally. Hitting a cap returns `429`.
- **Tied to the live pod.** Because the hybrid lifecycle can release and re-claim a pod across a
  suspension, a preview is valid while the *current* pod is bound. After a release/resume, start a fresh
  preview against the re-claimed pod (the same reason the pod pill name can change). Sessions have no TTL
  and end on **Stop**, run end, or API shutdown.

## Related reading

- [Sandbox browser preview — Reference](../reference/sandbox-browser-preview.md) — routes, DTO fields, status codes, limits.
- [Sandbox browser preview — Deep Dive](../deep-dive/sandbox-browser-preview.md) — the tunnel, caps, and cleanup.
- [Sandbox pod execution experience](./sandbox-pod-execution.md) — the pod pill and the pod-per-run model.
- [Runs, board & watch](./runs-board-watch.md) — where the run/execution view lives.
