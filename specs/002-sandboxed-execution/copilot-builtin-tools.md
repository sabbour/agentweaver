# Copilot CLI Built-In Tool Schemas (Static Analysis Evidence)

**Source:** `C:\Users\asabbour\AppData\Local\copilot\pkg\win32-arm64\1.0.61\app.js` (v1.0.61, ~11 MB minified)
**Date:** 2026-06-10
**Analyst:** Morpheus (Runtime/Architecture Lead)

---

## In-Scope Tools

### 1. read_file

- **Bundle location:** Line ~2178, function `$sn()`
- **Type:** `function` (standard JSON tool call)
- **Description:** "Read the contents of a file. You must specify the line range you're interested in. Line numbers are 1-indexed."

| Arg | Type | Required | Description |
|---|---|---|---|
| `filePath` | string | YES | The absolute path of the file to read. |
| `startLine` | number | YES | The line number to start reading from, 1-based. |
| `endLine` | number | YES | The inclusive line number to end reading at, 1-based. |

**Notes:** Max 500 lines per read (constant `Fsn=500`). Line content truncated at 256 chars (constant `Usn=256`).

---

### 2. grep_search

- **Bundle location:** Line ~2176, function `Dsn()`
- **Type:** `function` (standard JSON tool call)
- **Description:** "Do a fast text search in the workspace. Use this tool when you want to search with an exact string or regex."

| Arg | Type | Required | Description |
|---|---|---|---|
| `query` | string | YES | Pattern to search for. Supports regex alternation. Case-insensitive. |
| `isRegexp` | boolean | YES | Whether the pattern is a regex. |
| `includePattern` | string | no | Glob pattern to filter files. Applied to relative paths. |
| `maxResults` | number | no | Maximum results to return (default: 20). |

**Notes:** Internally spawns `rg` (ripgrep) with `-n -i --hidden --with-filename`. Excludes `.git`, `node_modules`, `__pycache__`, `venv`, `.venv`, `build`, `dist`. Content per match truncated at 500 chars.

---

### 3. file_search

- **Bundle location:** Line ~2172, function `xsn()`
- **Type:** `function` (standard JSON tool call)
- **Description:** "Search for files in the workspace by glob pattern. This only returns the paths of matching files."

| Arg | Type | Required | Description |
|---|---|---|---|
| `query` | string | YES | Glob pattern to match file names/paths. |
| `maxResults` | number | no | Maximum results (default: 20). |

**Notes:** Uses `glob` npm module. Rejects patterns with absolute paths or `..` traversal. Returns relative paths.

---

### 4. str_replace_editor

- **Bundle location:** Line ~2329-2330, function `XYt()` (returns tool object)
- **Type:** `function` (standard JSON tool call, multi-command)
- **Description:** "Editing tool for viewing, creating and editing files. State is persistent across command calls."

| Arg | Type | Required | Description |
|---|---|---|---|
| `command` | enum: "view", "create", "str_replace", "insert" | YES | The operation to perform. |
| `path` | string | YES | Absolute path to file or directory. |
| `view_range` | int[] | no (view only) | Line range [start, end]. 1-indexed. [-1] = to end. |
| `forceReadLargeFiles` | boolean | no (view only) | Skip large-file size check. |
| `file_text` | string | no (create only) | Content of the file to create. |
| `old_str` | string | no (str_replace only) | String to find and replace. Must be unique. |
| `new_str` | string | no (str_replace/insert) | Replacement string or string to insert. |
| `insert_line` | integer | no (insert only) | Line number after which to insert. |

**Notes:** Internal variable `Rd = "view"`. The `mat` set contains all valid commands. `uat = 10MB` max file size. Edit tracking via internal `lat` class.

---

### 5. apply_patch

- **Bundle location:** Line ~5619, function `FDn()`
- **Type:** `custom` (freeform -- NOT JSON arguments)
- **Description:** "Use the apply_patch tool to edit files. This is a FREEFORM tool, so do not wrap the patch in JSON."

**Input format:** Raw string input accepted via `input` property, `patch` property, or direct string. Parsed as a custom patch grammar (Lark syntax):

```
start: begin_patch hunk+ end_patch
begin_patch: "*** Begin Patch" LF
end_patch: "*** End Patch" LF?

hunk: add_hunk | delete_hunk | update_hunk
add_hunk: "*** Add File: " filename LF add_line+
delete_hunk: "*** Delete File: " filename LF
update_hunk: "*** Update File: " filename LF change_move? change?

filename: /(.+)/
add_line: "+" /(.*)/ LF -> line

change_move: "*** Move to: " filename LF
change: context_and_changes+
context_and_changes: context_line* (remove_line | add_line)+
context_line: " " /(.*)/ LF -> line
remove_line: "-" /(.*)/ LF -> line
add_line: "+" /(.*)/ LF -> line
```

**Notes:** Paths are embedded in the patch text (after "Add File:", "Delete File:", "Update File:" markers). No separate JSON path argument. Partial application is possible (earlier hunks applied even if a later one fails).

---

### 6. create

- **Bundle location:** Line ~2296, via `ZYt()` function
- **Type:** `function` (standard JSON tool call)
- **Description:** "Tool for creating new files. Cannot be used if the specified path already exists. Parent directories must exist. Path MUST be absolute."

| Arg | Type | Required | Description |
|---|---|---|---|
| `path` | string | YES | Full absolute path to file to create. Must not exist. |
| `file_text` | string | YES | The content of the file to be created. |

**Notes:** Internally dispatches to `str_replace_editor` with `command = "create"`.

---

### 7. edit

- **Bundle location:** Line ~2297, via `ZYt()` function
- **Type:** `function` (standard JSON tool call)
- **Description:** "Tool for making string replacements in files. Replaces exactly one occurrence of old_str with new_str. Path MUST be absolute."

| Arg | Type | Required | Description |
|---|---|---|---|
| `path` | string | YES | Full absolute path to file to edit. Must exist. |
| `old_str` | string | no | The string to replace. Must match exactly one occurrence. |
| `new_str` | string | no | The replacement string. |

**Notes:** Internally dispatches to `str_replace_editor` with `command = "str_replace"` (or "edit" -- both are equivalent in the discriminated union).

---

## Out-of-Scope Tools

### semantic_search

- **Bundle location:** Line ~2187, function `zsn()`
- **Type:** `function`
- **Reason for exclusion:** Requires GitHub embeddings API (`/embeddings/code/search`) with repo indexing and authentication. Not available in self-hosted/local deployments. Per Constitution VII, scoped OUT rather than stubbed.

### exit_plan_mode

- **Bundle location:** Line ~1708, variable `nie="exit_plan_mode"`
- **Type:** Internal SDK orchestration tool (not a standard model-callable function)
- **Schema:** `{ summary: string, actions?: enum[]("autopilot"|"interactive"|"exit_only"|"autopilot_fleet"), recommendedAction?: enum }`
- **Reason for exclusion:** This is an internal orchestration mechanism that triggers a UI-level plan review flow (`exit_plan_mode.requested` ephemeral event). It controls the Copilot CLI session's plan-mode lifecycle (approve plan, switch to autopilot, etc.). Our runners have their own run lifecycle management. Not model-callable in the sense of a standard tool -- it is injected by the session orchestrator. Scoped OUT per Constitution VII.

### task

- **Bundle location:** Line ~1315, permission map entry `task:null`
- **Reason for exclusion:** Subagent orchestration. Explicitly excluded per user direction.

### notebook

- **Reason for exclusion:** Explicitly excluded per user direction. Not relevant to scaffolder runtime.

### web_fetch / web_search

- **Bundle location:** Line ~6811 (web_fetch), Line ~1577 (web_search as MCP tool `github-mcp-server-web_search`)
- **Type:** MCP server tools (delivered via GitHub's MCP server, not native built-ins)
- **Reason for exclusion:** Network access tools. Not needed in sandboxed scaffolder runs. MCP-delivered, not built-in.

### sql / session_store_sql

- **Bundle location:** Line ~173 (session.db SQLite), Line ~2699 (session_store_sql prompt)
- **Type:** Session-local SQLite database tools
- **Reason for exclusion:** Copilot CLI's own session management (checkpoints, turn history, state). Not a scaffolder concern -- we have our own event store.

---

### report_intent

- **Bundle location:** Line ~1139 (`Jm="report_intent"`), schema: `Rbi=Z.object({intent:Z.string()})`
- **Type:** `function` (standard JSON tool call)
- **Internal category:** `"think"` (from line ~6274 tool category mapping)
- **Status:** IN SCOPE -- reimplemented as a UI observability tool. The custom `report_intent` `AIFunction` takes a short `intent` string and emits the `agent.intent` run event so the run view can surface the agent's current high-level intent/plan (Principle V: Observable Runs). It performs NO filesystem or shell action and persists nothing beyond the event stream -- it is NOT a memory or todo tool. Registered with `is_override = true` (matches this native bundle name) and included in the AvailableTools allowlist (9 tools). See plan section 4.7.8.

### update_todo

- **Bundle location:** Line ~5422 (`aK="update_todo"`), schema: `ees=Z.object({todos:Z.string()})`
- **Type:** `function` (standard JSON tool call)
- **Internal category:** `"think"`
- **Reason for exclusion:** The todo tool is OUT OF SCOPE for this feature per product decision. The native tool remains excluded via `NativeToolExclusion`. Not reimplemented.

---

### Memory and Todo Tools (OUT OF SCOPE)

> **Status:** Memory tools (`store_memory`, `vote_memory`) and the todo tool (`update_todo`) are OUT OF SCOPE for this feature per product decision. They are NOT reimplemented in the current scope. The native memory and todo permission-kind rows have been intentionally removed from the Permission Map above (native memory/todo tools remain excluded via `NativeToolExclusion`). `report_intent` is NOT in this list -- it IS reimplemented as a UI observability tool (see above).

---

## Compatibility Group Map

From bundle line ~2330 (variable `_fn`):

```
edit         -> ["apply_patch", "str_replace_editor", "create", "edit"]
MultiEdit    -> ["apply_patch", "str_replace_editor", "create", "edit"]
Write        -> ["apply_patch", "str_replace_editor", "create", "edit"]
Grep         -> ["search"]  (internally: grep_search, file_search)
Glob         -> ["search"]  (internally: grep_search, file_search)
read         -> ["view"]    (internally: str_replace_editor view command)
search       -> ["search"]  (internally: grep_search, file_search)
```

These aliases allow permission/configuration systems to reference tool groups by short names.

---

## Full Permission Map (bundle line ~1315, `srn` object)

Complete tool-to-permission-kind mapping from the bundle:

```
bash         -> { kind: "shell", argument: null }
shell        -> { kind: "shell", argument: null }
write        -> { kind: "write", argument: null }
edit         -> { kind: "write", argument: null }
create       -> { kind: "write", argument: null }
read         -> null (no permission required)
view         -> null
glob         -> null
grep         -> null
ls           -> null
task         -> null
webfetch     -> null
web_fetch    -> null
websearch    -> null
web_search   -> null
```

This is the complete set of native tool names that our `NativeToolExclusion` class targets. Tools with `null` permission are always allowed by the native permission system; tools with a `kind` require explicit permission approval.

---

## Event Shapes (tool.execution_start / tool.execution_complete)

From bundle observations (line ~2172 area, `SessionAgentExecutor` class):

- **tool.execution_start** is not a distinct event in the bundle. Tool calls are tracked via the LLM completion loop.
- **tool.execution_complete** (internal): Tracked via `a.type==="tool.execution_complete"` in the subagent session event loop. Contains a tool call count increment. The event data shape was not fully extractable from the minified bundle but the type string is confirmed.
- **External events:** The bundle emits progress events with `kind: "tool_execution"` during tool processing, containing tool name and timing.

For our implementation, we use the existing `tool.result` / `tool.error` event schema from section 4.5, which is sufficient for observability.
