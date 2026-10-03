import { createHash, randomUUID } from "node:crypto";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { homedir } from "node:os";
import { join } from "node:path";
import { mkdir, readdir, readFile, rename, writeFile } from "node:fs/promises";

const exec = promisify(execFile);
const text = (value, max = 100) =>
  typeof value === "string" ? value.slice(0, max).replace(/[\x00-\x1f\x7f]/g, " ") : "";
const stamp = (value) => Number.isFinite(value) && value > 0 ? value : null;
const positive = (value) => Number.isSafeInteger(value) && value > 0 ? value : null;
const sha = (value) => typeof value === "string" && /^[0-9a-f]{40}$/i.test(value) ? value.toLowerCase() : null;

export function parseRepository(remote) {
  if (typeof remote !== "string") throw new Error("Git remote is missing");
  const match = remote.trim().match(/^(?:https:\/\/(?:[^@/]+@)?github\.com\/|git@github\.com:|ssh:\/\/git@github\.com\/)([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+?)(?:\.git)?\/?$/i);
  if (!match) throw new Error("Expected a github.com owner/repository origin; repository identity unavailable");
  return `${match[1]}/${match[2]}`.toLowerCase();
}

export async function repositoryContext(workspace, home = process.env.COPILOT_HOME || join(homedir(), ".copilot")) {
  if (!workspace) throw new Error("No workspace path supplied by the session");
  const { stdout } = await exec("git", ["-C", workspace, "config", "--get", "remote.origin.url"], { timeout: 5000 });
  const repository = parseRepository(stdout);
  const repositoryId = createHash("sha256").update(repository).digest("hex");
  return {
    repository, repositoryId,
    directory: join(home, "extensions", "agentweaver-workflow-canvas", "artifacts", repositoryId, "sessions"),
  };
}

export function projectRun(summary, detail, outcome, refs = {}) {
  const phase = detail?.phases?.find((item) => item.id === summary.currentPhase?.id);
  // Results and arguments are untrusted; copy only exact, validated fields.
  const result = outcome?.status === "completed" && outcome.result && typeof outcome.result === "object" && !Array.isArray(outcome.result)
    ? outcome.result : {};
  const pr = result.pr && typeof result.pr === "object" && !Array.isArray(result.pr) ? result.pr : {};
  const issueNumber = positive(refs.issueNumber) ?? positive(result.issueNumber);
  const prNumber = positive(refs.prNumber) ?? positive(result.prNumber) ?? positive(pr.prNumber);
  const headSha = sha(refs.headSha) ?? sha(result.headSha) ?? sha(pr.headSha);
  return {
    runId: text(summary.runId, 80),
    workflowName: text(summary.workflowName, 100),
    status: text(summary.status, 20),
    phase: text(phase?.title, 100),
    updatedAt: stamp(summary.updatedAt),
    createdAt: stamp(summary.createdAt),
    issueNumber, prNumber, headSha,
    referenceSources: {
      issue: issueNumber ? positive(refs.issueNumber) ? "owner-associated" : "completed-result" : "not-observed",
      pr: prNumber ? positive(refs.prNumber) ? "owner-associated" : "completed-result" : "not-observed",
      head: headSha ? sha(refs.headSha) ? "owner-associated" : "completed-result" : "not-observed",
    },
    agents: (detail?.agents || []).slice(0, 60).map((agent) => ({
      agentId: text(agent.agentId, 80),
      label: text(agent.label, 80),
      status: text(agent.status, 24),
      phaseId: text(agent.phaseId, 80),
      startedAt: stamp(agent.startedAt),
      completedAt: stamp(agent.completedAt),
    })),
    totalSpawnedAgentCount: Math.max(0, Number(summary.totalSpawnedAgentCount) || 0),
    directAgentsObserved: Boolean(detail),
  };
}

function sanitizePublishedRun(run) {
  if (!run || typeof run !== "object" || !text(run.runId, 80)) throw new Error("invalid run");
  return {
    runId: text(run.runId, 80), workflowName: text(run.workflowName, 100),
    status: text(run.status, 20), phase: text(run.phase, 100),
    updatedAt: stamp(run.updatedAt), createdAt: stamp(run.createdAt),
    issueNumber: positive(run.issueNumber), prNumber: positive(run.prNumber),
    headSha: sha(run.headSha), totalSpawnedAgentCount: positive(run.totalSpawnedAgentCount) ?? 0,
    referenceSources: Object.fromEntries(["issue", "pr", "head"].map((key) => [
      key, ["owner-associated", "completed-result"].includes(run.referenceSources?.[key])
        ? run.referenceSources[key] : "not-observed",
    ])),
    directAgentsObserved: run.directAgentsObserved === true,
    agents: (Array.isArray(run.agents) ? run.agents : []).slice(0, 60).map((agent) => ({
      agentId: text(agent.agentId, 80), label: text(agent.label, 80),
      status: text(agent.status, 24), phaseId: text(agent.phaseId, 80),
      startedAt: stamp(agent.startedAt), completedAt: stamp(agent.completedAt),
    })),
  };
}

export async function publish(context, sessionId, snapshot) {
  if (!/^[A-Za-z0-9._-]{1,128}$/.test(sessionId)) throw new Error("Invalid session ID");
  await mkdir(context.directory, { recursive: true });
  const target = join(context.directory, `${sessionId}.json`);
  const temp = join(context.directory, `${sessionId}.${randomUUID()}.tmp`);
  try {
    await writeFile(temp, JSON.stringify({
      version: 1, repository: context.repository, sessionId,
      observedAt: Date.now(), ...snapshot,
    }), { encoding: "utf8", flag: "wx", mode: 0o600 });
    await rename(temp, target);
  } catch (error) {
    const { unlink } = await import("node:fs/promises");
    await unlink(temp).catch(() => {});
    throw error;
  }
}

export async function readRepository(context, now = Date.now()) {
  let files;
  try {
    files = (await readdir(context.directory)).filter((name) => /^[A-Za-z0-9._-]{1,128}\.json$/.test(name));
  } catch (error) {
    if (error.code === "ENOENT") return { sessions: [], errors: [] };
    throw error;
  }
  const sessions = [];
  const errors = files.length > 300 ? [`Session observation limit reached; ${files.length - 300} publisher(s) omitted`] : [];
  for (const file of files.slice(0, 300)) {
    try {
      const raw = await readFile(join(context.directory, file), "utf8");
      if (raw.length > 1_000_000) throw new Error("oversized projection");
      const value = JSON.parse(raw);
      if (value.version !== 1 || value.repository !== context.repository || value.sessionId !== file.slice(0, -5) || !Array.isArray(value.runs)) {
        throw new Error("invalid projection");
      }
      const observedAt = stamp(value.observedAt);
      sessions.push({
        sessionId: value.sessionId,
        observedAt,
        freshness: !observedAt || Math.abs(now - observedAt) > 90_000 ? "stale" : "observed",
        truncated: Boolean(value.truncated),
        error: text(value.error, 160),
        runs: value.runs.slice(0, 80).map(sanitizePublishedRun),
      });
    } catch {
      errors.push(`${file}: unreadable projection`);
    }
  }
  return { sessions, errors };
}

export async function githubReference(repository, kind, number, fetcher) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) || !["issues", "pulls"].includes(kind) || !positive(number)) {
    throw new Error("Invalid GitHub reference");
  }
  const path = `repos/${repository}/${kind}/${number}`;
  let item;
  if (fetcher) {
    const response = await fetcher(`https://api.github.com/${path}`, {
      headers: { Accept: "application/vnd.github+json" },
      signal: AbortSignal.timeout(5000),
    });
    if (!response.ok) throw new Error(`GitHub ${kind} #${number}: HTTP ${response.status}`);
    item = await response.json();
  } else {
    try {
      const { stdout } = await exec("gh", ["api", path], { timeout: 8000, maxBuffer: 250_000, windowsHide: true });
      item = JSON.parse(stdout);
    } catch (error) {
      throw new Error(`GitHub ${kind} #${number}: CLI lookup failed (${error.code || error.name || "unknown"})`);
    }
  }
  if (kind === "issues" && item.pull_request) throw new Error(`GitHub #${number} is a pull request, not an issue`);
  return {
    number,
    title: text(item.title, 120),
    state: text(item.state, 24),
    labels: Array.isArray(item.labels) ? item.labels.slice(0, 10).map((label) => text(label.name, 40)) : [],
    milestone: text(item.milestone?.title, 60),
    headSha: kind === "pulls" ? sha(item.head?.sha) : null,
  };
}
