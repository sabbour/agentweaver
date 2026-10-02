import { open } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

const cache = new Map();
const sessionIdPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;

export async function readSubagents(sessionId, root = join(homedir(), ".copilot", "session-state")) {
    if (!sessionIdPattern.test(sessionId)) throw new TypeError("Invalid local session ID");
    const path = join(root, sessionId, "events.jsonl");
    const handle = await open(path, "r");
    try {
        const stat = await handle.stat();
        const previous = cache.get(path);
        if (previous && stat.ino === previous.ino && stat.size === previous.size && stat.mtimeMs === previous.mtimeMs) {
            return [...previous.agents.values()];
        }
        const continuous = previous && stat.ino === previous.ino && stat.size >= previous.size;
        const agents = continuous ? new Map(previous.agents) : new Map();
        let partial = continuous ? previous.partial : "";
        const start = continuous ? previous.size : 0;
        if (stat.size > start) {
            const stream = handle.createReadStream({ start, end: stat.size - 1, encoding: "utf8", autoClose: false });
            for await (const chunk of stream) {
                const lines = (partial + chunk).split("\n");
                partial = lines.pop();
                for (const line of lines) {
                    if (!line.startsWith('{"type":"subagent.')) continue;
                    const event = JSON.parse(line);
                    if (!["subagent.started", "subagent.completed", "subagent.failed"].includes(event.type) || !sessionIdPattern.test(event.agentId)) continue;
                    const prior = agents.get(event.agentId);
                    agents.set(event.agentId, {
                        id: `task:${event.agentId}`,
                        parentId: sessionId,
                        name: event.data?.agentDisplayName || event.data?.agentName || prior?.name || "Subagent",
                        status: event.type === "subagent.started" ? "running" : event.type === "subagent.failed" ? "failed" : "completed",
                        kind: "subagent",
                        issueNumber: null,
                        busyForSeconds: null,
                        lastEventAt: event.timestamp,
                    });
                }
            }
        }
        cache.set(path, { ino: stat.ino, size: stat.size, mtimeMs: stat.mtimeMs, partial, agents });
        return [...agents.values()];
    } finally {
        await handle.close();
    }
}
