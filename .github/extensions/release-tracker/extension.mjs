import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import { addIssue, createMilestone, defaultVersion, listMilestones, normalizeVersion, readStatus, updateAgent, updateReview, updateStatus } from "./state.mjs";

const servers = new Map();
const page = fileURLToPath(new URL("./index.html", import.meta.url));

async function startServer() {
    const server = createServer(async (req, res) => {
        try {
            const request = new URL(req.url, "http://localhost");
            if (req.method === "GET" && request.pathname === "/") {
                const html = await readFile(page);
                res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
                res.end(html);
            } else if (req.method === "GET" && request.pathname === "/milestones") {
                const versions = await listMilestones();
                res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
                res.end(JSON.stringify(versions));
            } else if (req.method === "GET" && request.pathname === "/status") {
                const state = await readStatus(request.searchParams.get("version") || defaultVersion);
                res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
                res.end(JSON.stringify(state));
            } else {
                res.writeHead(404);
                res.end("Not found");
            }
        } catch (error) {
            const code = error.code === "ENOENT" ? 404 : error instanceof TypeError ? 400 : 500;
            res.writeHead(code, { "Content-Type": "text/plain; charset=utf-8" });
            res.end(`Release tracker unavailable: ${error.message}`);
        }
    });
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    const address = server.address();
    return { server, url: `http://127.0.0.1:${address.port}/` };
}

await joinSession({
    canvases: [
        createCanvas({
            id: "agentweaver-release-tracker",
            displayName: "Agentweaver release tracker",
            description: "Shared milestone issue, PR, behavior-test, RC, release, and AKS deployment status by version.",
            inputSchema: {
                type: "object",
                additionalProperties: false,
                properties: { version: { type: "string", pattern: "^v?(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$" } },
            },
            actions: [
                {
                    name: "refresh",
                    description: "Read the current project-owned release status artifact for a milestone version (defaults to 0.34.1).",
                    inputSchema: { type: "object", additionalProperties: false, properties: { version: { type: "string" } } },
                    handler: async ({ input }) => readStatus(input?.version || defaultVersion),
                },
                {
                    name: "create_milestone",
                    description: "Create a project-owned release status artifact for a new milestone version; refuses to overwrite existing versions.",
                    inputSchema: { type: "object", additionalProperties: false, required: ["version"], properties: { version: { type: "string" } } },
                    handler: async ({ input }) => {
                        try {
                            return await createMilestone(input.version);
                        } catch (error) {
                            if (error.code === "EEXIST") throw new CanvasError("milestone_exists", `Milestone ${input.version} is already tracked`);
                            if (error instanceof TypeError) throw new CanvasError("invalid_milestone", error.message);
                            throw error;
                        }
                    },
                },
                {
                    name: "add_issue",
                    description: "Add a milestone issue with its title, priority, and optional linked PR to an existing version.",
                    inputSchema: {
                        type: "object", additionalProperties: false, required: ["version", "number", "title"],
                        properties: {
                            version: { type: "string" }, number: { type: "integer" }, title: { type: "string" },
                            status: { type: "string" }, priority: { type: "string" }, prNumber: { type: ["integer", "null"] },
                            prStatus: { type: "string" }, note: { type: "string" }, head: { type: "string" },
                        },
                    },
                    handler: async ({ input }) => {
                        try {
                            return await addIssue(input);
                        } catch (error) {
                            if (error instanceof TypeError || error instanceof RangeError) throw new CanvasError("invalid_issue", error.message);
                            throw error;
                        }
                    },
                },
                {
                    name: "update_status",
                    description: "Update a tracked issue or release gate for a specified milestone version. Evidence is required before asserting test success.",
                    inputSchema: {
                        type: "object",
                        additionalProperties: false,
                        required: ["version", "section", "changes"],
                        properties: {
                            version: { type: "string" },
                            section: { type: "string", enum: ["issue", "priority", "directApi", "directUi", "integrationRc", "release", "aksDeployment", "liveGate"] },
                            number: { type: "integer" },
                            changes: {
                                type: "object",
                                minProperties: 1,
                                additionalProperties: false,
                                properties: {
                                    title: { type: "string" },
                                    status: { type: "string" },
                                    note: { type: "string" },
                                    evidence: { type: "string" },
                                    prNumber: { type: ["integer", "null"] },
                                    prStatus: { type: "string" },
                                    head: { type: "string" },
                                    priority: { type: "string" },
                                    revision: { type: "string" },
                                    digest: { type: "string" },
                                },
                            },
                        },
                    },
                    handler: async ({ input }) => {
                        try {
                            return await updateStatus(input);
                        } catch (error) {
                            if (error instanceof TypeError || error instanceof RangeError) {
                                throw new CanvasError("invalid_status_update", error.message);
                            }
                            throw error;
                        }
                    },
                },
                {
                    name: "update_review",
                    description: "Record a rubber-duck, code-review, issue-specific API, UI, or GitHub gate against a linked PR's current head; passing requires evidence.",
                    inputSchema: {
                        type: "object", additionalProperties: false, required: ["version", "number", "kind", "status", "head"],
                        properties: {
                            version: { type: "string" }, number: { type: "integer" },
                            kind: { type: "string", enum: ["rubberDuck", "codeReview", "api", "ui", "github"] },
                            status: { type: "string", enum: ["not run", "in progress", "passed", "failed"] },
                            head: { type: "string" }, evidence: { type: "string" },
                        },
                    },
                    handler: async ({ input }) => {
                        try {
                            return await updateReview(input);
                        } catch (error) {
                            if (error instanceof TypeError || error instanceof RangeError) throw new CanvasError("invalid_review", error.message);
                            throw error;
                        }
                    },
                },
                {
                    name: "update_agent",
                    description: "Publish a timestamped agent or subagent report for this milestone. Parent ID and tracked issue assignment are optional.",
                    inputSchema: {
                        type: "object", additionalProperties: false, required: ["version", "id", "name", "status"],
                        properties: {
                            version: { type: "string" }, id: { type: "string" }, name: { type: "string" },
                            parentId: { type: ["string", "null"] }, issueNumber: { type: ["integer", "null"] },
                            status: { type: "string", enum: ["active", "idle", "blocked", "done", "unknown"] }, note: { type: "string" },
                        },
                    },
                    handler: async ({ input }) => {
                        try {
                            return await updateAgent(input);
                        } catch (error) {
                            if (error instanceof TypeError || error instanceof RangeError) throw new CanvasError("invalid_agent", error.message);
                            throw error;
                        }
                    },
                },
            ],
            open: async ({ instanceId, input }) => {
                const version = normalizeVersion(input?.version || defaultVersion);
                await readStatus(version);
                let entry = servers.get(instanceId);
                if (!entry) {
                    entry = await startServer();
                    servers.set(instanceId, entry);
                }
                return { title: `v${version} release tracker`, url: `${entry.url}?version=${encodeURIComponent(version)}` };
            },
            onClose: async ({ instanceId }) => {
                const entry = servers.get(instanceId);
                if (entry) {
                    servers.delete(instanceId);
                    await new Promise((resolve, reject) => entry.server.close((error) => error ? reject(error) : resolve()));
                }
            },
        }),
    ],
});
