import assert from "node:assert/strict";
import { test } from "node:test";
import { copyFile, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { addIssue, createMilestone, listMilestones, normalizeVersion, readStatus, updateReview, updateStatus } from "./state.mjs";
import { buildLiveStatus } from "./live.mjs";

const source = fileURLToPath(new URL("./milestones/0.34.1.json", import.meta.url));

async function fixture(run) {
    const dir = await mkdtemp(join(tmpdir(), "agentweaver-release-tracker-"));
    const path = join(dir, "0.34.1.json");
    try {
        await copyFile(source, path);
        await run(dir, path);
    } finally {
        await rm(dir, { recursive: true, force: true });
    }
}

test("seeded release gates stay not run and include all tracked issues", async () => {
    const state = await readStatus();
    assert.deepEqual(state.issues.map(({ number }) => number), [1705, 1707, 1687, 1690, 1718, 1720, 1721, 1709, 1713, 1719]);
    assert.equal(state.issues[0].status, "closed");
    assert.equal(state.issues[1].status, "active");
    assert.equal(state.directApi.status, "not run");
    assert.equal(state.directUi.status, "not run");
    assert.equal(state.liveGate.status, "not run");
    assert.equal(state.issues.find(({ number }) => number === 1719).prNumber, 1722);
    assert.equal(state.issues.find(({ number }) => number === 1719).checks.rubberDuck.status, "not run");
});

test("updates persist for fresh reads without replacing other gates", async () => fixture(async (dir) => {
    await updateStatus({ version: "0.34.1", section: "issue", number: 1707, changes: { status: "closed", prNumber: 1730, prStatus: "merged" } }, dir);
    await updateStatus({ version: "0.34.1", section: "integrationRc", changes: { status: "deployed", revision: "abc123", digest: "sha256:abcd" } }, dir);
    await updateStatus({ version: "0.34.1", section: "directApi", changes: { status: "passed", evidence: "RC test result", revision: "abc123", digest: "sha256:abcd" } }, dir);
    const state = await readStatus("0.34.1", dir);
    assert.equal(state.issues.find(({ number }) => number === 1707).prNumber, 1730);
    assert.equal(state.directApi.evidence, "RC test result");
    assert.equal(state.directUi.status, "not run");
}));

test("invalid updates fail without changing the durable artifact", async () => fixture(async (dir, path) => {
    const original = await readFile(path, "utf8");
    await assert.rejects(updateStatus({ version: "0.34.1", section: "issue", number: 9999, changes: { status: "closed" } }, dir), /tracked issue/);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "release", changes: { issues: [] } }, dir), /Cannot change/);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "directUi", changes: { status: "success" } }, dir), /Unknown status/);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "directUi", changes: { status: "passed" } }, dir), /requires evidence/);
    assert.equal(await readFile(path, "utf8"), original);
}));

test("concurrent updates to a shared artifact retain both changes", async () => fixture(async (dir) => {
    await Promise.all([
        updateStatus({ version: "0.34.1", section: "directApi", changes: { note: "API evidence pending" } }, dir),
        updateStatus({ version: "0.34.1", section: "directUi", changes: { note: "UI evidence pending" } }, dir),
    ]);
    const state = await readStatus("0.34.1", dir);
    assert.equal(state.directApi.note, "API evidence pending");
    assert.equal(state.directUi.note, "UI evidence pending");
}));

test("a second milestone is isolated, selectable, and can track issues", async () => fixture(async (dir) => {
    assert.equal(normalizeVersion("v0.35.0"), "0.35.0");
    await assert.rejects(createMilestone("../0.35.0", dir), /version/);
    await createMilestone("v0.35.0", dir);
    await assert.rejects(createMilestone("0.35.0", dir), { code: "EEXIST" });
    await addIssue({ version: "0.35.0", number: 1800, title: "New milestone task", prNumber: 1801, prStatus: "open" }, dir);
    assert.deepEqual(await listMilestones(dir), ["0.35.0", "0.34.1"]);
    const next = await readStatus("0.35.0", dir);
    assert.equal(next.issues[0].prNumber, 1801);
    assert.equal(next.issues[0].checks.codeReview.status, "not run");
    assert.equal(next.directApi.status, "not run");
    assert.equal((await readStatus("0.34.1", dir)).issues.length, 10);
}));

test("reviews require evidence on the tracked head and reset when the head or PR changes", async () => fixture(async (dir) => {
    const first = "a".repeat(40);
    const second = "b".repeat(40);
    await assert.rejects(updateReview({ version: "0.34.1", number: 1709, kind: "rubberDuck", status: "passed", head: first, evidence: "Local pass" }, dir), /tracked PR head/);
    await updateStatus({ version: "0.34.1", section: "issue", number: 1709, changes: { head: first } }, dir);
    await assert.rejects(updateReview({ version: "0.34.1", number: 1709, kind: "rubberDuck", status: "passed", head: "abc", evidence: "Local pass" }, dir), /full current PR commit SHA/);
    await assert.rejects(updateReview({ version: "0.34.1", number: 1709, kind: "rubberDuck", status: "passed", head: first }, dir), /evidence/);
    await updateReview({ version: "0.34.1", number: 1709, kind: "rubberDuck", status: "passed", head: first, evidence: "Local pass" }, dir);
    await updateReview({ version: "0.34.1", number: 1709, kind: "api", status: "passed", head: first, evidence: "API behavior tested" }, dir);
    await updateReview({ version: "0.34.1", number: 1709, kind: "codeReview", status: "failed", head: first, evidence: "One bug" }, dir);
    let issue = (await readStatus("0.34.1", dir)).issues.find(({ number }) => number === 1709);
    assert.equal(issue.checks.rubberDuck.status, "passed");
    assert.equal(issue.checks.codeReview.status, "failed");
    assert.equal(issue.checks.api.status, "passed");
    await updateStatus({ version: "0.34.1", section: "issue", number: 1709, changes: { head: second } }, dir);
    issue = (await readStatus("0.34.1", dir)).issues.find(({ number }) => number === 1709);
    assert.equal(issue.checks.rubberDuck.status, "not run");
    assert.equal(issue.checks.codeReview.status, "not run");
    assert.equal(issue.checks.api.status, "not run");
    await assert.rejects(updateReview({ version: "0.34.1", number: 1709, kind: "codeReview", status: "passed", head: first, evidence: "Old pass" }, dir), /tracked PR head/);
    await updateStatus({ version: "0.34.1", section: "issue", number: 1709, changes: { prNumber: 1800 } }, dir);
    issue = (await readStatus("0.34.1", dir)).issues.find(({ number }) => number === 1709);
    assert.equal(issue.head, "");
    assert.equal(issue.checks.codeReview.status, "not run");
    await assert.rejects(updateReview({ version: "0.34.1", number: 1709, kind: "codeReview", status: "passed", head: first, evidence: "Old pass" }, dir), /tracked PR head/);
}));

test("live activity selects milestone agents and coordinator, nesting local subagents", () => {
    const sessions = [
        { id: "coordinator", session_type: "general_chat", name: "Release coordinator", activity: { status: "busy" } },
        { id: "self", project_id: "project-a", creator_session_id: "coordinator", name: "v0.34.1 tracker", activity: { status: "busy" } },
        { id: "repair", project_id: "project-a", creator_session_id: "coordinator", name: "V0.34.1 #1707 repair", activity: { status: "idle" } },
        { id: "pr", project_id: "project-a", creator_session_id: "coordinator", name: "Blueprint fix", source_pr_number: 1722, activity: { status: "busy", busy_for_seconds: 12 } },
        { id: "other", project_id: "project-a", name: "v0.35.0 #1900", activity: { status: "busy" } },
        { id: "nearby", project_id: "project-a", name: "v0.34.10 unrelated", activity: { status: "busy" } },
        { id: "foreign", project_id: "project-b", name: "v0.34.1 #1707", activity: { status: "busy" } },
    ];
    const issues = [{ number: 1707, prNumber: null }, { number: 1719, prNumber: 1722 }];
    const tasks = [
        { type: "agent", id: "child", agentType: "code-review", status: "running" },
        { type: "shell", id: "shell", status: "running" },
    ];
    const live = buildLiveStatus({ sessions, tasks }, "self", "0.34.1", issues);
    assert.deepEqual(live.nodes.map(({ id }) => id), ["coordinator", "self", "repair", "pr", "task:child"]);
    assert.equal(live.nodes.find(({ id }) => id === "repair").issueNumber, 1707);
    assert.equal(live.nodes.find(({ id }) => id === "pr").issueNumber, 1719);
    assert.equal(live.nodes.find(({ id }) => id === "task:child").parentId, "self");
    assert.deepEqual(buildLiveStatus({ sessions, tasks }, "self", "0.36.0", []).nodes, []);
    assert.throws(() => buildLiveStatus({ sessions: [], tasks }, "self", "0.34.1", issues), /missing from live activity/);
});

test("live behavior proof requires matching RC and both direct test gates", async () => fixture(async (dir) => {
    const proof = { status: "passed", evidence: "Observed full behavior", revision: "abc123", digest: "sha256:abcd" };
    await assert.rejects(updateStatus({ version: "0.34.1", section: "directApi", changes: proof }, dir), /deployed combined RC/);
    await updateStatus({ version: "0.34.1", section: "integrationRc", changes: { status: "passed", revision: "abc123", digest: "sha256:abcd" } }, dir);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "directApi", changes: proof }, dir), /deployed combined RC/);
    await updateStatus({ version: "0.34.1", section: "integrationRc", changes: { status: "deployed", revision: "abc123", digest: "sha256:abcd" } }, dir);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "directApi", changes: { ...proof, digest: "sha256:wrong" } }, dir), /deployed combined RC/);
    await updateStatus({ version: "0.34.1", section: "directApi", changes: proof }, dir);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "liveGate", changes: proof }, dir), /both direct API and UI/);
    await updateStatus({ version: "0.34.1", section: "directUi", changes: proof }, dir);
    await updateStatus({ version: "0.34.1", section: "liveGate", changes: proof }, dir);
    assert.equal((await readStatus("0.34.1", dir)).liveGate.status, "passed");
    await updateStatus({ version: "0.34.1", section: "release", changes: { status: "passed" } }, dir);
    await updateStatus({ version: "0.34.1", section: "aksDeployment", changes: { status: "deployed" } }, dir);
    await updateStatus({ version: "0.34.1", section: "integrationRc", changes: { digest: "sha256:new" } }, dir);
    const afterRcChange = await readStatus("0.34.1", dir);
    assert.equal(afterRcChange.directApi.status, "not run");
    assert.equal(afterRcChange.directUi.status, "not run");
    assert.equal(afterRcChange.liveGate.status, "not run");
    assert.equal(afterRcChange.release.status, "pending");
    assert.equal(afterRcChange.aksDeployment.status, "pending");
}));

test("downstream release and deployment cannot precede live behavior proof", async () => fixture(async (dir) => {
    await assert.rejects(updateStatus({ version: "0.34.1", section: "release", changes: { status: "passed" } }, dir), /live behavior gate/);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "release", changes: { status: "deployed" } }, dir), /Invalid release status/);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "aksDeployment", changes: { status: "deployed" } }, dir), /completed release/);
    await assert.rejects(updateStatus({ version: "0.34.1", section: "aksDeployment", changes: { status: "passed" } }, dir), /Invalid aksDeployment status/);
}));
