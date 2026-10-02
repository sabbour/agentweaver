import { open, readFile, readdir, rename, unlink } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

export const defaultVersion = "0.34.1";
const artifacts = fileURLToPath(new URL("./milestones/", import.meta.url));
const allowed = {
    issue: ["title", "status", "note", "prNumber", "prStatus", "priority", "head"],
    priority: ["status", "note", "priority"],
    directApi: ["status", "note", "evidence", "revision", "digest"],
    directUi: ["status", "note", "evidence", "revision", "digest"],
    integrationRc: ["status", "note", "evidence", "revision", "digest"],
    release: ["status", "note", "evidence", "revision", "digest"],
    aksDeployment: ["status", "note", "evidence", "revision", "digest"],
    liveGate: ["status", "note", "evidence", "revision", "digest"],
};
const statuses = new Set(["closed", "active", "pending", "blocked", "not run", "in progress", "passed", "failed", "merged", "deployed"]);
const prStatuses = new Set(["none", "pending", "open", "draft", "merged", "closed"]);
const gateStatuses = {
    release: new Set(["pending", "blocked", "in progress", "failed", "passed"]),
    aksDeployment: new Set(["pending", "blocked", "in progress", "failed", "deployed"]),
};
const reviewStatuses = new Set(["not run", "in progress", "passed", "failed"]);
const agentStatuses = new Set(["active", "idle", "blocked", "done", "unknown"]);
const emptyReview = () => ({ status: "not run", evidence: "", head: "", updatedAt: "" });
const issueChecks = ["rubberDuck", "codeReview", "api", "ui", "github"];
const commitHead = /^[a-fA-F0-9]{40}$|^[a-fA-F0-9]{64}$/;

export function normalizeVersion(version) {
    if (typeof version !== "string" || !/^v?(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/.test(version)) {
        throw new TypeError("Milestone version must be a release number such as 0.34.1");
    }
    return version.replace(/^v/, "");
}

export async function listMilestones(directory = artifacts) {
    return (await readdir(directory))
        .filter((file) => /^\d+\.\d+\.\d+\.json$/.test(file))
        .map((file) => file.slice(0, -5))
        .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
}

export async function readStatus(version = defaultVersion, directory = artifacts) {
    const normalized = normalizeVersion(version);
    const state = JSON.parse(await readFile(join(directory, `${normalized}.json`), "utf8"));
    if (state.version !== normalized || !Array.isArray(state.issues) || !Array.isArray(state.agents) || !Object.keys(allowed).every((key) => key === "issue" || state[key] && typeof state[key] === "object")) {
        throw new TypeError(`Invalid ${normalized} release tracker artifact`);
    }
    return state;
}

function validateChanges(section, changes) {
    if (!changes || typeof changes !== "object" || Array.isArray(changes) || !Object.keys(changes).length) {
        throw new TypeError("Provide at least one status change");
    }
    for (const [key, value] of Object.entries(changes)) {
        if (!allowed[section].includes(key)) throw new TypeError(`Cannot change ${key} in ${section}`);
        if (key === "prNumber") {
            if (value !== null && (!Number.isSafeInteger(value) || value <= 0)) throw new TypeError("PR number must be positive or null");
        } else if (typeof value !== "string" || value.length > 2000 || (key !== "note" && key !== "evidence" && key !== "head" && !value.trim())) {
            throw new TypeError(`${key} must be a nonempty string of at most 2000 characters`);
        }
        if (key === "status" && !statuses.has(value)) throw new TypeError(`Unknown status: ${value}`);
        if (key === "status" && gateStatuses[section] && !gateStatuses[section].has(value)) throw new TypeError(`Invalid ${section} status: ${value}`);
        if (key === "prStatus" && !prStatuses.has(value)) throw new TypeError(`Unknown PR status: ${value}`);
    }
}

async function mutate(version, directory, operation) {
    const path = join(directory, `${normalizeVersion(version)}.json`);
    const lock = `${path}.lock`;
    let handle;
    for (let attempt = 0; attempt < 25; attempt++) {
        try {
            handle = await open(lock, "wx");
            break;
        } catch (error) {
            if (error.code !== "EEXIST") throw error;
            await new Promise((resolve) => setTimeout(resolve, 100));
        }
    }
    if (!handle) throw new Error(`Release tracker is locked: ${lock}`);
    let temporary;
    try {
        const state = await readStatus(version, directory);
        operation(state);
        state.updatedAt = new Date().toISOString();
        temporary = `${path}.${randomUUID()}.tmp`;
        const file = await open(temporary, "wx");
        try {
            await file.writeFile(`${JSON.stringify(state, null, 2)}\n`);
        } finally {
            await file.close();
        }
        await rename(temporary, path);
        temporary = undefined;
        return state;
    } finally {
        if (temporary) await unlink(temporary);
        await handle.close();
        await unlink(lock);
    }
}

export async function createMilestone(version, directory = artifacts) {
    const normalized = normalizeVersion(version);
    const state = {
        version: normalized,
        updatedAt: new Date().toISOString(),
        priority: { status: "pending", priority: "Set current priority", note: "Each current-head milestone PR needs local rubber-duck + code-review and issue-specific API/UI and GitHub gates before serialized rebase auto-merge." },
        issues: [],
        agents: [],
        integrationRc: { status: "pending", note: "Assemble and deploy the combined RC after serialized merges.", evidence: "", revision: "", digest: "" },
        directApi: { status: "not run", note: "Run direct API behavior tests against the pinned RC.", evidence: "", revision: "", digest: "" },
        directUi: { status: "not run", note: "Run direct UI behavior tests against the pinned RC.", evidence: "", revision: "", digest: "" },
        liveGate: { status: "not run", note: "Setup/integrity checks are not live behavior proof.", evidence: "", revision: "", digest: "" },
        release: { status: "pending", note: "Release after RC behavior gates pass.", evidence: "", revision: "", digest: "" },
        aksDeployment: { status: "pending", note: "Deploy the verified release to AKS.", evidence: "", revision: "", digest: "" },
    };
    const file = await open(join(directory, `${normalized}.json`), "wx");
    try {
        await file.writeFile(`${JSON.stringify(state, null, 2)}\n`);
    } finally {
        await file.close();
    }
    return state;
}

export async function addIssue({ version, number, title, status = "pending", priority = "Implementation path", prNumber = null, prStatus = "pending", note = "", head = "" }, directory = artifacts) {
    if (!Number.isSafeInteger(number) || number <= 0) throw new TypeError("Issue number must be positive");
    const issue = { number, title, status, priority, prNumber, prStatus, note, head, checks: Object.fromEntries(issueChecks.map((kind) => [kind, emptyReview()])) };
    validateChanges("issue", { title, status, priority, prNumber, prStatus, note, head });
    return mutate(version, directory, (state) => {
        if (state.issues.some((entry) => entry.number === number)) throw new RangeError(`Issue #${number} already tracked`);
        state.issues.push(issue);
    });
}

export async function updateStatus({ version, section, number, changes }, directory = artifacts) {
    if (!Object.hasOwn(allowed, section)) throw new TypeError("Unknown tracker section");
    validateChanges(section, changes);
    return mutate(version, directory, (state) => {
        if (section === "issue" ? !Number.isSafeInteger(number) || !state.issues.some((issue) => issue.number === number) : number !== undefined) {
            throw new RangeError("Issue updates require a tracked issue number; other sections must omit it");
        }
        const target = section === "issue" ? state.issues.find((issue) => issue.number === number) : state[section];
        const previousRc = section === "integrationRc" && [target.status, target.revision, target.digest].join("|");
        const previousPr = section === "issue" && [target.prNumber, target.head].join("|");
        if (section === "issue" && Object.hasOwn(changes, "prNumber") && changes.prNumber !== target.prNumber && !Object.hasOwn(changes, "head")) {
            target.head = "";
        }
        Object.assign(target, changes);
        if (section === "issue" && previousPr !== [target.prNumber, target.head].join("|")) {
            target.checks = Object.fromEntries(issueChecks.map((kind) => [kind, emptyReview()]));
        }
        if (section === "integrationRc" && previousRc !== [target.status, target.revision, target.digest].join("|")) {
            for (const gate of [state.directApi, state.directUi, state.liveGate]) {
                gate.status = "not run";
                gate.evidence = "";
                gate.revision = "";
                gate.digest = "";
            }
            for (const gate of [state.release, state.aksDeployment]) gate.status = "pending";
        }
        if (["directApi", "directUi"].includes(section) && target.status !== "passed") {
            state.liveGate.status = "not run";
            state.liveGate.evidence = "";
            state.liveGate.revision = "";
            state.liveGate.digest = "";
        }
        if (["directApi", "directUi", "liveGate"].includes(section) && target.status !== "passed") {
            state.release.status = "pending";
            state.aksDeployment.status = "pending";
        }
        if (section === "release" && target.status !== "passed") state.aksDeployment.status = "pending";
        if (["directApi", "directUi", "liveGate"].includes(section) && target.status === "passed") {
            if (!target.evidence?.trim() || !target.revision?.trim() || !target.digest?.trim()) {
                throw new TypeError(`${section} requires evidence, revision and digest before passing`);
            }
            if (state.integrationRc.status !== "deployed" ||
                target.revision !== state.integrationRc.revision || target.digest !== state.integrationRc.digest) {
                throw new TypeError(`${section} must match the deployed combined RC revision and digest`);
            }
            if (section === "liveGate" && (state.directApi.status !== "passed" || state.directUi.status !== "passed")) {
                throw new TypeError("Live gate requires both direct API and UI behavior tests to pass");
            }
        }
        if (section === "release" && target.status === "passed" && state.liveGate.status !== "passed") {
            throw new TypeError("Release requires the passed live behavior gate");
        }
        if (section === "aksDeployment" && target.status === "deployed" && state.release.status !== "passed") {
            throw new TypeError("AKS deployment requires the completed release");
        }
    });
}

export async function updateReview({ version, number, kind, status, head, evidence = "" }, directory = artifacts) {
    if (!Number.isSafeInteger(number) || number <= 0) throw new TypeError("Issue number must be positive");
    if (!issueChecks.includes(kind)) throw new TypeError("Unknown issue check kind");
    if (!reviewStatuses.has(status)) throw new TypeError("Unknown review status");
    if (typeof head !== "string" || head.length > 200 || (status === "passed" && !commitHead.test(head))) throw new TypeError("Passing issue check requires a full current PR commit SHA");
    if (typeof evidence !== "string" || evidence.length > 2000 || (status === "passed" && !evidence.trim())) throw new TypeError("Passing issue check requires evidence");
    return mutate(version, directory, (state) => {
        const issue = state.issues.find((item) => item.number === number);
        if (!issue || !issue.prNumber) throw new RangeError("Review requires a tracked issue with a linked PR");
        if (issue.head !== head) throw new TypeError("Issue check head must match the tracked PR head");
        issue.checks[kind] = { status, head, evidence, updatedAt: new Date().toISOString() };
    });
}

export async function updateAgent({ version, id, name, parentId = null, issueNumber = null, status, note = "" }, directory = artifacts) {
    if (typeof id !== "string" || !/^[a-zA-Z0-9_-]{1,100}$/.test(id)) throw new TypeError("Agent ID must be 1-100 letters, numbers, hyphens or underscores");
    if (typeof name !== "string" || !name.trim() || name.length > 150) throw new TypeError("Agent name is required");
    if (parentId !== null && (typeof parentId !== "string" || !/^[a-zA-Z0-9_-]{1,100}$/.test(parentId) || parentId === id)) throw new TypeError("Invalid parent agent ID");
    if (issueNumber !== null && (!Number.isSafeInteger(issueNumber) || issueNumber <= 0)) throw new TypeError("Invalid assigned issue number");
    if (!agentStatuses.has(status)) throw new TypeError("Unknown agent status");
    if (typeof note !== "string" || note.length > 2000) throw new TypeError("Agent note must be at most 2000 characters");
    return mutate(version, directory, (state) => {
        if (issueNumber !== null && !state.issues.some((issue) => issue.number === issueNumber)) throw new RangeError("Assigned issue is not tracked");
        const parent = parentId && state.agents.find((agent) => agent.id === parentId);
        if (parentId && !parent) throw new RangeError("Parent agent is not tracked");
        for (let ancestor = parent; ancestor; ancestor = state.agents.find((agent) => agent.id === ancestor.parentId)) {
            if (ancestor.id === id) throw new RangeError("Agent hierarchy cannot contain a cycle");
        }
        const agent = { id, name, parentId, issueNumber, status, note, updatedAt: new Date().toISOString() };
        const index = state.agents.findIndex((item) => item.id === id);
        if (index === -1) state.agents.push(agent);
        else state.agents[index] = agent;
    });
}
