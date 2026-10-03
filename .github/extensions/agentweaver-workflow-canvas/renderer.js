const $ = (id) => document.getElementById(id);
let model;
const el = (tag, value, className) => {
  const node = document.createElement(tag);
  if (value !== undefined) node.textContent = String(value);
  if (className) node.className = className;
  return node;
};
const notice = (message, error = false) => {
  const node = el("div", message, `notice${error ? " error" : ""}`);
  $("notices").append(node);
};
const node = (title, detail) => {
  const box = el("div", undefined, "node");
  box.append(el("strong", title), el("div", detail || "Not observed", "muted"));
  return box;
};
const date = (ms) => ms ? new Date(ms).toLocaleString() : "Unknown";
const active = (status) => status === "running" || status === "pending";

function render(data) {
  model = data;
  const observedRuns = data.sessions.reduce((sum, session) => sum + session.runs.length, 0);
  $("repository").textContent = data.repository;
  $("coverage").textContent = data.coverage;
  $("summary").replaceChildren(
    el("span", `${data.sessions.length} publishing session(s)`, "pill"),
    el("span", `${observedRuns} observed run(s)`, "pill"),
  );
  $("notices").replaceChildren();
  if (!data.sessions.length) notice("No sessions have published workflow observations yet. This is not evidence that the repository has no workflows.");
  else if (!observedRuns) notice("This session has published no workflow runs. Runs active in other sessions cannot appear until those owning sessions load this project extension and reload; this view is incomplete.", true);
  if (data.ownObservation !== "observed") notice(`Current session: ${data.ownObservation}`, true);
  for (const error of data.errors) notice(error, true);
  const rows = data.sessions.flatMap((session) => {
    if (session.error) notice(`${session.sessionId}: ${session.error}; last known runs may be out of date.`, true);
    if (session.freshness !== "observed") notice(`${session.sessionId}: stale; last observed ${date(session.observedAt)}.`, true);
    if (session.truncated) notice(`${session.sessionId}: only a bounded recent selection is shown; older runs are omitted.`);
    return session.runs.map((run) => ({ session, run }));
  }).filter(({ run }) => $("filter").value === "all" || active(run.status));
  rows.sort((a, b) => (b.run.updatedAt || 0) - (a.run.updatedAt || 0));
  $("runs").replaceChildren();
  if (!rows.length) $("runs").append(el("p", observedRuns ? "No observed runs match this filter. Unpublished sessions are not included." : "No workflow runs have been published to this canvas yet.", "muted"));
  for (const { session, run } of rows) {
    const card = el("article", undefined, "card");
    card.append(el("h2", `${run.workflowName || "Unnamed workflow"} · ${run.status}`),
      el("div", `Run ${run.runId} · Phase ${run.phase || "not observed"} · Updated ${date(run.updatedAt)}`, "muted"));
    const graph = el("div", undefined, "nodes");
    graph.append(node("Workflow run", run.runId));
    for (const [kind, number, label] of [["issues", run.issueNumber, "Issue"], ["pulls", run.prNumber, "Pull request"]]) {
      if (!number) continue;
      const ref = data.references?.[`${kind}:${number}`];
      const detail = ref?.error || (ref ? `${ref.title} · ${ref.state} · ${ref.milestone || "No milestone"} · ${(ref.labels || []).join(", ") || "No labels"}` : "Lookup not covered or not yet available");
      graph.append(el("span", "→", "arrow"), node(`${label} #${number}`, detail));
    }
    graph.append(el("span", "→", "arrow"), node("Owning session", session.sessionId));
    for (const agent of (run.agents || []).slice(0, 8)) {
      graph.append(el("span", "→", "arrow"), node("Direct agent", `${agent.label || agent.agentId} · ${agent.status}`));
    }
    card.append(graph, el("div", `Reference provenance: issue ${run.referenceSources?.issue || "not observed"} · PR ${run.referenceSources?.pr || "not observed"} · head ${run.referenceSources?.head || "not observed"}`, "muted"));
    const list = el("ul");
    for (const agent of run.agents || []) {
      list.append(el("li", `${agent.label || "Agent"} (${agent.status}) · ${agent.agentId}`));
    }
    card.append(el("div", run.directAgentsObserved ? `Direct agents: ${run.agents?.length || 0} observed / ${run.totalSpawnedAgentCount || 0} spawned${(run.agents?.length || 0) > 8 ? " (first 8 in graph)" : ""}; nested subagents are not exposed by this API.` : "Direct agent detail not observed.", "muted"), list);
    $("runs").append(card);
  }
}
async function load(refresh = false) {
  $("refresh").disabled = true;
  try {
    if (refresh) {
      const response = await fetch("/refresh", { method: "POST" });
      if (!response.ok) throw new Error(`Owner refresh failed: HTTP ${response.status}`);
    }
    const response = await fetch("/state");
    if (!response.ok) throw new Error(`Snapshot failed: HTTP ${response.status}`);
    render(await response.json());
  } catch (error) {
    $("notices").replaceChildren();
    notice(error.message, true);
  } finally { $("refresh").disabled = false; }
}
$("filter").addEventListener("change", () => { if (model) render(model); });
$("refresh").addEventListener("click", () => load(true));
load();
setInterval(() => { if (!document.hidden) load(); }, 30_000);
