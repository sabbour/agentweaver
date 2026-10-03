import { projectRun, publish, readRepository, githubReference } from "./metadata.mjs";

export function createObserver(session, context, { interval = 30_000, lookup = githubReference } = {}) {
  let pending;
  let timer;
  let invalidation;
  let last = 0;
  let closed = false;
  const refs = new Map();
  const githubCache = new Map();

  async function refresh(force = false) {
    if (pending) return pending;
    if (!force && Date.now() - last < 1500) return readRepository(context);
    pending = (async () => {
      try {
        const page = await session.workflow.listRuns({ limit: 50 });
        const selected = [...page.runs].sort((a, b) =>
          Number(a.status === "running" || a.status === "pending") - Number(b.status === "running" || b.status === "pending") || a.createdAt - b.createdAt
        ).reverse().slice(0, 24);
        const previous = await readRepository(context);
        const own = previous.sessions.find((entry) => entry.sessionId === session.sessionId);
        const runs = [];
        for (const item of selected) {
          const detail = await session.workflow.getRunDetail(item.runId);
          const outcome = item.status === "completed" ? await session.workflow.getRun(item.runId) : null;
          const prior = own?.runs.find((run) => run.runId === item.runId);
          const retained = {
            issueNumber: prior?.referenceSources?.issue === "owner-associated" ? prior.issueNumber : null,
            prNumber: prior?.referenceSources?.pr === "owner-associated" ? prior.prNumber : null,
            headSha: prior?.referenceSources?.head === "owner-associated" ? prior.headSha : null,
            ...refs.get(item.runId),
          };
          runs.push(projectRun(item, detail, outcome, retained));
        }
        await publish(context, session.sessionId, {
          runs,
          truncated: Boolean(page.omittedOlder || page.hasMoreNewer || page.runs.length > 24),
          error: null,
        });
        last = Date.now();
        return readRepository(context);
      } catch (error) {
        const previous = await readRepository(context);
        await publish(context, session.sessionId, {
          runs: previous.sessions.find((entry) => entry.sessionId === session.sessionId)?.runs ?? [],
          truncated: true,
          error: `Owner observation failed: ${error.code || error.name || "unknown"}`,
        });
        throw error;
      }
    })();
    try { return await pending; } finally { pending = null; }
  }

  async function associate(input) {
    if (!input || typeof input !== "object" || !/^[A-Za-z0-9._-]{1,80}$/.test(input.runId || "")) {
      throw new Error("A valid owning-session runId is required");
    }
    const validNumber = (number) => number === undefined || Number.isSafeInteger(number) && number > 0;
    if (!validNumber(input.issueNumber) || !validNumber(input.prNumber) ||
        (input.headSha !== undefined && !/^[0-9a-f]{40}$/i.test(input.headSha)) ||
        (!input.issueNumber && !input.prNumber && !input.headSha)) throw new Error("Invalid issue, PR, or head SHA");
    await session.workflow.getRunDetail(input.runId);
    const own = (await readRepository(context)).sessions.find((item) => item.sessionId === session.sessionId);
    const prior = own?.runs.find((run) => run.runId === input.runId);
    const references = {
      issueNumber: prior?.referenceSources?.issue === "owner-associated" ? prior.issueNumber : undefined,
      prNumber: prior?.referenceSources?.pr === "owner-associated" ? prior.prNumber : undefined,
      headSha: prior?.referenceSources?.head === "owner-associated" ? prior.headSha : undefined,
      ...refs.get(input.runId),
      ...Object.fromEntries(["issueNumber", "prNumber", "headSha"].filter((key) => input[key] !== undefined).map((key) => [key, input[key]])),
    };
    if (input.issueNumber) await lookup(context.repository, "issues", input.issueNumber);
    if (input.prNumber || input.headSha && references.prNumber) {
      const pr = await lookup(context.repository, "pulls", references.prNumber);
      if (references.headSha && pr.headSha !== references.headSha.toLowerCase()) throw new Error("PR head SHA does not match GitHub");
    }
    refs.set(input.runId, references);
    await refresh(true);
    return { runId: input.runId, issueNumber: references.issueNumber ?? null, prNumber: references.prNumber ?? null };
  }

  async function state() {
    const snapshot = await readRepository(context);
    const own = snapshot.sessions.find((item) => item.sessionId === session.sessionId);
    const references = {};
    const requested = new Set();
    for (const owner of snapshot.sessions) for (const run of owner.runs) {
      if (run.issueNumber) requested.add(`issues:${run.issueNumber}`);
      if (run.prNumber) requested.add(`pulls:${run.prNumber}`);
    }
    await Promise.all([...requested].slice(0, 8).map(async (key) => {
      const cached = githubCache.get(key);
      if (cached && Date.now() - cached.at < 900_000) { references[key] = cached.value; return; }
      const [kind, number] = key.split(":");
      let value;
      try { value = await lookup(context.repository, kind, Number(number)); }
      catch (error) { value = { error: `GitHub lookup unavailable: ${error.message}` }; }
      githubCache.set(key, { at: Date.now(), value });
      references[key] = value;
    }));
    return {
      repository: context.repository,
      observerSessionId: session.sessionId,
      coverage: "Only sessions running this project extension publish; unobserved sessions are unknown.",
      sessions: snapshot.sessions,
      references,
      errors: snapshot.errors,
      ownObservation: own ? own.error || "observed" : "not yet observed",
    };
  }

  function start() {
    timer = setInterval(() => { if (!closed) refresh().catch(() => {}); }, interval);
    timer.unref();
    const unsubscribe = session.on("workflow.run_updated", () => {
      if (closed) return;
      clearTimeout(invalidation);
      invalidation = setTimeout(() => { if (!closed) refresh().catch(() => {}); }, 1600);
      invalidation.unref();
    });
    return () => {
      closed = true;
      clearInterval(timer);
      clearTimeout(invalidation);
      unsubscribe();
    };
  }
  return { refresh, associate, state, start };
}
