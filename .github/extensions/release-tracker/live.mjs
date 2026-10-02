export function buildLiveStatus({ sessions, tasks }, sessionId, version, issues) {
    if (!Array.isArray(sessions) || !Array.isArray(tasks)) throw new TypeError("Live session response is missing sessions or tasks");
    const current = sessions.find((entry) => entry.id === sessionId);
    if (!current?.project_id) throw new Error("Current project session is missing from live activity");
    const byId = new Map(sessions.map((entry) => [entry.id, entry]));
    const issueFor = (entry) => issues.find((issue) =>
        (issue.prNumber && issue.prNumber === (entry.source_pr_number || entry.created_pr_number)) ||
        new RegExp(`(^|\\D)${issue.number}(\\D|$)`).test(`${entry.name || ""} ${entry.branch || ""}`));
    const versionTag = new RegExp(`(^|\\D)v${version.replaceAll(".", "\\.")}(?![\\d.])`, "i");
    const branchTag = new RegExp(`(^|\\D)v${version.replaceAll(".", "-")}(?!\\d)`, "i");
    const selected = new Set();
    for (const entry of sessions) {
        if (entry.project_id !== current.project_id) continue;
        if (issueFor(entry) || versionTag.test(entry.name || "") || branchTag.test(entry.branch || "")) {
            selected.add(entry.id);
        }
    }
    for (const id of selected) {
        const parentId = byId.get(id)?.creator_session_id || byId.get(id)?.parent_session_id;
        if (parentId && byId.has(parentId)) selected.add(parentId);
    }
    const nodes = sessions.filter((entry) => selected.has(entry.id)).map((entry) => ({
        id: entry.id,
        parentId: selected.has(entry.creator_session_id || entry.parent_session_id) ? entry.creator_session_id || entry.parent_session_id : null,
        name: entry.name || "Unnamed session",
        status: entry.awaiting_user_input ? "waiting for input" : entry.awaiting_plan_approval ? "awaiting plan" : entry.activity?.status || "unknown",
        kind: entry.session_type === "general_chat" ? "coordinator" : "project agent",
        issueNumber: issueFor(entry)?.number || null,
        busyForSeconds: entry.activity?.busy_for_seconds || null,
    }));
    for (const task of selected.has(sessionId) ? tasks : []) {
        if (task.type !== "agent") continue;
        nodes.push({
            id: `task:${task.id}`,
            parentId: sessionId,
            name: task.displayName || task.agentType || "Subagent",
            status: task.status,
            kind: "subagent",
            issueNumber: null,
            busyForSeconds: null,
        });
    }
    return { observedAt: new Date().toISOString(), nodes };
}
