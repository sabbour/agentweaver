import assert from "node:assert/strict";
import { after, test } from "node:test";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { projectRun, parseRepository, publish, readRepository, githubReference } from "./metadata.mjs";
import { createObserver } from "./observer.mjs";
import { startPanel } from "./server.mjs";

const temp = await mkdtemp(join(tmpdir(), "workflow-canvas-"));
after(async () => { const { rm } = await import("node:fs/promises"); await rm(temp, { recursive: true, force: true }); });
const context = { repository: "sabbour/agentweaver", directory: join(temp, "sessions") };
const run = (id = "run-1", status = "running") => ({
  runId: id, workflowName: "delivery", status, createdAt: 1000, updatedAt: Date.now(),
  currentPhase: { id: "phase-1" }, totalSpawnedAgentCount: 1,
});
const detail = {
  phases: [{ id: "phase-1", title: "Review" }],
  agents: [{ agentId: "agent-1", label: "Review", status: "running", phaseId: "phase-1", prompt: "secret" }],
};

test("repository identity rejects credentials outside a supported GitHub origin", () => {
  assert.equal(parseRepository("git@github.com:Sabbour/Agentweaver.git"), "sabbour/agentweaver");
  assert.equal(parseRepository("https://github.com/sabbour/agentweaver"), "sabbour/agentweaver");
  assert.throws(() => parseRepository("https://example.org/owner/repo"), /github.com/);
});

test("projection contains only allowlisted fields and nested completed PR refs", () => {
  const result = projectRun({ ...run(), description: "secret" }, detail, {
    status: "completed", result: { issueNumber: 1751, pr: { prNumber: 24, headSha: "a".repeat(40) }, task: "secret" },
  });

  assert.equal(result.phase, "Review");
  assert.equal(result.issueNumber, 1751);
  assert.equal(result.prNumber, 24);
  assert.deepEqual(result.referenceSources, { issue: "completed-result", pr: "completed-result", head: "completed-result" });
  assert.equal(result.agents[0].agentId, "agent-1");
  assert.doesNotMatch(JSON.stringify(result), /secret|prompt|task/);
});

test("terminal runs show last entered phase, not later skipped phase", () => {
  const ended = run("finished", "error");
  ended.currentPhase = null;
  const phases = [
    { id: "p0", title: "Validate", entryCount: 1, lastEnteredRunAttempt: 1, startedAt: 1000 },
    { id: "p6", title: "Monitor CI and admit", entryCount: 1, lastEnteredRunAttempt: 1, startedAt: 2000 },
    { id: "p7", title: "Cleanup handoff", entryCount: 0, lastEnteredRunAttempt: 0, startedAt: 3000, status: "skipped" },
  ];
  assert.equal(projectRun(ended, { phases, agents: [] }).phase, "Monitor CI and admit");
  ended.currentPhase = { id: "p0" };
  assert.equal(projectRun(ended, { phases, agents: [] }).phase, "Validate");
  ended.currentPhase = { id: "phase-not-in-detail" };
  assert.equal(projectRun(ended, { phases, agents: [] }).phase, "");
});

test("per-session atomic publication is isolated, durable and strips unknown stored fields", async () => {
  await Promise.all([
    publish(context, "owner-a", { runs: [projectRun(run(), detail)], truncated: false }),
    publish(context, "owner-b", { runs: [projectRun(run("run-b"), detail)], truncated: false }),
  ]);
  let state = await readRepository(context);
  assert.equal(state.sessions.length, 2);
  assert.deepEqual(state.sessions.map((entry) => entry.sessionId).sort(), ["owner-a", "owner-b"]);
  const path = join(context.directory, "owner-a.json");
  const persisted = JSON.parse(await readFile(path, "utf8"));
  persisted.runs[0].secret = "never return";
  persisted.runs[0].issueNumber = 999;
  persisted.runs[0].referenceSources.issue = "not-observed";
  await writeFile(path, JSON.stringify(persisted));
  state = await readRepository(context);
  assert.doesNotMatch(JSON.stringify(state), /never return/);
  assert.equal(state.sessions.find((s) => s.sessionId === "owner-a").runs[0].issueNumber, null);
  assert.equal((await readRepository({ ...context, directory: join(temp, "other") })).sessions.length, 0);
  assert.equal((await readRepository(context, Date.now() + 100_000)).sessions[0].freshness, "stale");
});

test("invalid snapshot appears as error rather than success", async () => {
  await writeFile(join(context.directory, "broken.json"), '{"repository":"wrong","runs":[]}');
  const state = await readRepository(context);
  assert.match(state.errors.join(" "), /unreadable projection/);
});

test("owner observer pages, rehydrates refs, rejects foreign run, and surfaces failure", async () => {
  const owner = { sessionId: "owner-a", workflow: {
    listRuns: async () => ({ runs: [run()], omittedOlder: 1 }),
    getRunDetail: async (id) => { if (id !== "run-1") throw new Error("not owned"); return detail; },
    getRun: async () => ({ status: "running" }),
  }, on: () => () => {} };
  let lookups = 0;
  const lookup = async (repo, kind, number) => {
    lookups++;
    return { number, title: "Test", state: "open", labels: [], milestone: "", headSha: "a".repeat(40) };
  };
  const observer = createObserver(owner, context, { lookup });
  await observer.refresh(true);
  assert.equal((await observer.state()).sessions.find((s) => s.sessionId === "owner-a").truncated, true);
  await observer.associate({ runId: "run-1", issueNumber: 1751 });
  await observer.associate({ runId: "run-1", prNumber: 24 });
  const observed = (await readRepository(context)).sessions.find((s) => s.sessionId === "owner-a").runs[0];
  assert.equal(observed.issueNumber, 1751);
  assert.equal(observed.prNumber, 24);
  assert.equal(observed.referenceSources.issue, "owner-associated");
  await observer.state();
  const count = lookups;
  await observer.state();
  assert.equal(lookups, count);
  const restored = createObserver(owner, context, { lookup });
  await restored.refresh(true);
  assert.equal((await readRepository(context)).sessions.find((s) => s.sessionId === "owner-a").runs[0].issueNumber, 1751);
  await assert.rejects(observer.associate({ runId: "foreign", issueNumber: 42 }), /not owned/);
  const failed = createObserver({ ...owner, workflow: { listRuns: async () => { throw new Error("unavailable"); } } }, context, { lookup });
  await assert.rejects(failed.refresh(true), /unavailable/);
  assert.match((await failed.state()).ownObservation, /Owner observation failed/);
});

test("completed-result provenance survives subsequent refreshes", async () => {
  const owner = { sessionId: "completed-owner", workflow: {
    listRuns: async () => ({ runs: [run("finished", "completed")] }),
    getRunDetail: async () => detail,
    getRun: async () => ({ status: "completed", result: { issueNumber: 1744, pr: { prNumber: 42 } } }),
  }, on: () => () => {} };
  const observer = createObserver(owner, context);
  await observer.refresh(true);
  await observer.refresh(true);
  const observed = (await readRepository(context)).sessions.find((s) => s.sessionId === owner.sessionId).runs[0];
  assert.equal(observed.referenceSources.issue, "completed-result");
  assert.equal(observed.referenceSources.pr, "completed-result");
});

test("GitHub lookup validates inputs and exposes only selected fields", async () => {
  await assert.rejects(githubReference("bad", "issues", 1), /Invalid/);
  const value = await githubReference("sabbour/agentweaver", "issues", 1751, async () => ({
    ok: true, json: async () => ({
      title: "<unsafe>", state: "open", labels: [{ name: "type:feature" }],
      milestone: { title: "Squad" }, body: "secret",
    }),
  }));
  assert.equal(value.title, "<unsafe>");
  assert.doesNotMatch(JSON.stringify(value), /secret|body/);
  await assert.rejects(githubReference("sabbour/agentweaver", "issues", 24, async () => ({
    ok: true, json: async () => ({ pull_request: { url: "https://api.github.com/pulls/24" } }),
  })), /pull request, not an issue/);
});

test("loopback panel serves escaped-by-DOM renderer, rejects foreign origin and closes", async () => {
  const observer = { state: async () => ({ sessions: [] }), refresh: async () => {} };
  const panel = await startPanel(observer);
  try {
    const response = await fetch(panel.url);
    assert.equal(response.status, 200);
    assert.match(await response.text(), /renderer\.js/);
    const js = await (await fetch(`${panel.url}renderer.js`)).text();
    assert.match(js, /textContent/);
    assert.doesNotMatch(js, /innerHTML/);
    assert.equal((await fetch(`${panel.url}refresh`, { method: "POST", headers: { Origin: "https://evil.example" } })).status, 403);
    assert.equal((await fetch(`${panel.url}state`)).status, 200);
    const problemPanel = await startPanel({ state: async () => { throw new Error("secret"); }, refresh: async () => {} });
    try {
      const problem = await fetch(`${problemPanel.url}state`);
      assert.equal(problem.status, 503);
      assert.doesNotMatch(await problem.text(), /secret/);
    } finally { await problemPanel.close(); }
  } finally { await panel.close(); }
  await assert.rejects(fetch(panel.url));
});
