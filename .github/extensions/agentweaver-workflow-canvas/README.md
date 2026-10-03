# Repository workflow canvas

Project extension `project:agentweaver-workflow-canvas`, canvas type
`repository-workflows`. After checking out a branch containing this extension,
reload extensions in each participating project session. Open the canvas with
`{ "scope": "repository" }` and any fresh panel instance ID. **Refresh** reads
the owning session again; **Active / Active and history** changes the view.
The agent-facing `get_model` and `refresh` actions return the same projection.

Each session sees only its own native Dynamic Workflow runs through the Copilot
SDK. It publishes a small allowlisted observation atomically to
`$COPILOT_HOME/extensions/agentweaver-workflow-canvas/artifacts/<SHA-256-of-lowercase-owner-repo>/sessions/<sessionId>.json`
(`~/.copilot` if `COPILOT_HOME` is unset). Other sessions with this extension
in the same GitHub repository can read these projections. No session journals,
prompts, results, tool arguments, private app stores, credentials, or arbitrary
remote URLs are read or published. Repository identity comes from the GitHub
`origin` owner/repository; an unsupported origin fails explicitly.

Direct workflow agents and phases come from `getRunDetail`; the SDK does not
expose nested subagent relationships or run arguments to observers. Completed
workflow results contribute **only** exact issue/PR number and head-SHA fields.
For an active run whose input has not reached a public result, an agent in the
**owning session** may call the `associate` canvas action with `{ "runId": "...",
"issueNumber": 1751, "prNumber": 123 }`. That validates ownership and resolves
any supplied issue or PR with GitHub's public read-only API before publishing
the reference; it never changes the workflow or GitHub objects. For private
repositories or API failures, lookups report errors rather than inventing links.
Titles, milestone, state, and labels are fetched at view time, not persisted.

The canvas reports unpublished sessions as **unknown**, old publications as
**stale** after 90 seconds, and observation/lookup failures explicitly. It
samples the latest 50 native runs and details up to 24 (active first); truncation
is indicated. An older session without the extension cannot be discovered by
this canvas. No automatic permissions, workflow controls, or merge actions are
registered. Run `node --test .github/extensions/agentweaver-workflow-canvas/canvas.test.mjs`
for the focused Node suite.
