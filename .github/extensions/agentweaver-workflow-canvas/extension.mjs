import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { repositoryContext } from "./metadata.mjs";
import { createObserver } from "./observer.mjs";
import { startPanel } from "./server.mjs";
import { fileURLToPath } from "node:url";

const servers = new Map();
const projectRoot = fileURLToPath(new URL("../../../", import.meta.url));
let observer;
let context;

await joinSession({
  canvases: [createCanvas({
    id: "repository-workflows",
    displayName: "Repository workflows",
    description: "Read-only observed dynamic workflows and their issue, PR, session, and direct-agent relations in this repository.",
    inputSchema: {
      type: "object", required: ["scope"],
      properties: { scope: { type: "string", enum: ["repository"] } },
    },
    actions: [
      {
        name: "refresh",
        description: "Refresh workflow observations for this owning session, then read the shared repository projection.",
        handler: async () => {
          try { await observer.refresh(true); return await observer.state(); }
          catch (error) { throw new CanvasError("workflow_observation_failed", `Owner observation failed: ${error.code || error.name || "unknown"}`); }
        },
      },
      {
        name: "get_model",
        description: "Read the shared repository projection without changing workflow state.",
        handler: async () => {
          try { return await observer.state(); }
          catch (error) { throw new CanvasError("workflow_observation_failed", `Repository observation failed: ${error.code || error.name || "unknown"}`); }
        },
      },
      {
        name: "associate",
        description: "Publish validated issue/PR references for an observed run owned by this session; does not change a workflow, issue, or PR.",
        inputSchema: {
          type: "object", required: ["runId"],
          properties: {
            runId: { type: "string" }, issueNumber: { type: "integer" },
            prNumber: { type: "integer" }, headSha: { type: "string" },
          },
        },
        handler: async ({ input }) => {
          try { return await observer.associate(input); }
          catch (error) { throw new CanvasError("workflow_reference_invalid", `Owner run or GitHub reference could not be validated: ${error.code || error.name || "unknown"}`); }
        },
      },
    ],
    open: async ({ instanceId }) => {
      if (!observer) throw new CanvasError("workflow_observation_failed", "Observer not initialized");
      let panel = servers.get(instanceId);
      if (!panel) {
        panel = await startPanel(observer);
        servers.set(instanceId, panel);
      }
      try { await observer.refresh(); } catch { /* State endpoint reports the explicit owner error. */ }
      return { title: `Workflows · ${context.repository}`, url: panel.url };
    },
    onClose: async ({ instanceId }) => {
      const panel = servers.get(instanceId);
      if (panel) { servers.delete(instanceId); await panel.close(); }
    },
  })],
}).then(async (session) => {
  context = await repositoryContext(projectRoot);
  observer = createObserver(session, context);
  const stop = observer.start();
  process.once("SIGTERM", async () => {
    stop();
    await Promise.all([...servers.values()].map((panel) => panel.close()));
    process.exit(0);
  });
  await observer.refresh().catch(() => {});
});
