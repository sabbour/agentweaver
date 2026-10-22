# Chief of Staff

The Chief of Staff gives each user a private place to talk to Agentweaver and a
personal list of outcomes. It has two parts:

- **Conversations**: private text chats. Each conversation is a run that the
  user started in one project they selected.
- **Priorities**: an ordered list of outcomes. Each Priority has notes, a
  manual checklist, and links to runs, conversations, issues, pull requests,
  and artifacts across projects.

The Chief of Staff doesn't run work on its own. It starts work only through the
existing run routes, and it reads gates and run state from their owners.

## How a conversation works

1. The user selects one project and writes a first message.
2. The client calls `POST /api/v1/projects/{projectId}/runs` with an
   `Idempotency-Key`. The Orchestrator creates the run. A retry with the same
   key returns the same run, so a double submit creates one request.
3. The client calls `POST /api/v1/chief-of-staff/conversations` with the
   `projectId` and `runId`. The Orchestrator saves a private conversation entry
   for the caller. Repeating the call returns the same entry.
4. After a reload or reconnect, the client lists
   `GET /api/v1/chief-of-staff/conversations` and reopens the conversation.

The conversation entry stores only the project, run, and owner. Messages,
gates, and state stay with the run's owners.

Only the user who created the run can bind it. Other users can't list or read
the entry, even in the same project.

## How Priorities work

A Priority records an outcome the user cares about. Its `state` is `active`,
`achieved`, or `dropped`, and only the user changes it. Editing a Priority
never starts, cancels, or changes a run, session, or gate. A Priority stays in
the list after its linked runs end.

Each link names one item in a project the user can read:

| Link kind | Identifies |
| --- | --- |
| `run` | A run in the project. |
| `conversation` | One of the user's Chief of Staff conversations. |
| `issue` | Provider, repository owner/name, and issue number. |
| `pull_request` | Provider, repository owner/name, and pull request number. |
| `artifact` | An artifact in a run. |

Reads show the current state of run and conversation links. The Priority
owner does not resolve issue, pull request or artifact lifecycle state. The web
app separately reads stored issue and pull request records to show their titles
and recorded states. These are not live provider status checks.

## Handle conflicts

Two tabs or devices can edit the same Priority. Each write sends the revision
it read:

- A Priority write with an old `expectedRevision` returns
  `409 priority_revision_conflict`.
- A reorder with an old `expectedOrderRevision` returns
  `409 priority_order_conflict`.

On a conflict, reread the Priority or list, show the current values, and let
the user apply the change again. The service never merges edits.

## Lose access to a project

Every call reads the caller's current project permissions. When the user can no
longer read a project:

- Conversations in that project return `access: unavailable` with only their
  identifier and creation time.
- Links to that project return `access: unavailable` with only their
  identifier and kind.
- The Priority outcome, notes, and checklist stay visible, because they
  belong to the user.

The user can still remove a link to a project they no longer read.

## Flow and attention

Flow groups the user's work into **Needs input**, **Open**, **Working**, and
**Done**. It has no store of its own:

- **Needs input** reads pending gates from
  `GET /api/v1/notifications?projectId=...&includeDismissed=true`. Dismissing a
  notification doesn't hide a gate from Flow.
- **Open**, **Working**, and **Done** read runs from
  `GET /api/v1/projects/{projectId}/runs`. Completed, failed, cancelled, and
  indeterminate runs stay distinct.
- Answering a gate rereads the gate by project, run, session, request, and
  state version first. A stale gate fails without changing state.

## Use the web app

The web app shows Chief of Staff on **Overview** (`/overview`), on **Sessions**
(`/sessions`), in the notification bell, and on each project's **Flow** page.

### Start a chat

1. On **Overview**, go to **New chat**. On **Sessions**, select **New session**
   to open `/assistant`; `?project=<projectId>` preselects a project there.
1. Under **Project**, choose an active project. You don't type an ID.
1. Enter the first message and select **Send**.

The web app creates the run with one idempotency key per draft, adds it to
**My conversations**, and starts it with a token bound to that run. Sending the
same draft twice, or retrying after a lost response, creates one run. The
browser saves only the project, run, and root session IDs until start
finishes, so a reload or a sign-in redirect resumes the same chat. If sign-in
for the run needs your input, select **Sign in and start**. If start fails,
select **Retry start**. A start interrupted by a closed tab or a lost connection
resumes the next time you open **Overview** or **Sessions** in the same tab.

### My conversations

**My conversations** on **Overview** and **Sessions** lists the chats you
started, newest first, with the run state. The service stores the list for you
only, so it's the same after a reload, on another device, or after you sign in
again. Select **Open** to open the run page. A chat that was created but not
started shows **Not started** and a **Start** button. A conversation in a
project you can no longer read shows **Unavailable** and no project content.

### Continue a chat

On the run page, the **Chat** tab lists the run's activity and has a composer.
Enter a message and select **Send** to message the run's coordinator. The web
app sends it with a token bound to that run and one idempotency key per draft,
so a repeated send delivers one message. The conversation title comes from the
first message.

The **Messages** panel shows the run's user and assistant messages, oldest first.
The web app reads each message's text from the run's activity with a token bound
to that run, so the history is the same after a reload, a lost connection, or
sign-in on another device. A message the service no longer has shows **Not
found**; a message that fails to load shows **Could not load this message.** and
**Retry**. If you lose read access to the project, the panel hides all message
text and shows **You can no longer read this run. Messages are hidden.** A message
you send appears in **Messages** after the agent records it.

### Priorities

**Priorities** on **Overview** lists your Priorities in order.

- **Add Priority** creates a Priority from an outcome and optional notes.
- **Edit** changes the outcome, notes, and state: **Active**, **Achieved**, or
  **Dropped**.
- Add, check, and remove checklist items in place.
- **Move up** and **Move down** change the order.
- **Add link** links a run, a conversation, an issue, a pull request, or an artifact. Choose a project
  and a run, or pick one of your conversations. For an artifact, choose a
  session and a saved capture. Browse its files, then select **Link capture**.
  The link saves the whole capture, not one file. You never need to type an ID.
- For an issue or pull request, choose **Project**, **State**, then a stored
  record. Select **Load more issues** or **Load more pull requests** to read
  another page. Select **Link issue** or **Link pull request** to save it.
  The form never asks for a repository number or navigation ID.
- Issues come from the project's synced issue records, not a provider search.
  Pull requests come from records saved by project runs since recording began.
  The list shows its coverage date; earlier pull requests are not included.
  A service error is shown as an error, never as an empty list.
- After reload, saved issue and pull request links read their metadata again.
  Select **Open issue** or **Open pull request** to open the provider's page.
  Older targets may require **Load more** before they can be reopened.
  Changing identity, project or state discards the old choices. A denied read
  removes titles and navigation links but leaves the user's Priority intact.
- New issue and pull request links save the selected provider, repository owner
  and name, and number. Two repositories can use the same number without changing
  where either link opens. Before the database update is applied, saving shows
  **Database update pending** and leaves the Priority unchanged.
- Older number-only links are not rewritten. Reopening waits until every stored
  page has been loaded. If more than one repository has that number, the app
  explains that it cannot identify the target and offers no navigation link.
- Run and conversation links show the run state. A link you can't read shows
  **Unavailable** and its kind only. A deleted target shows **Not found**.

If another change lands first, the web app rereads Priorities and shows **This
Priority changed. Review the current values and try again.** Your edit stays in
the form. An order conflict shows **The order changed. Review the current order
and try again.** Saving a Priority never sends a run or gate action.

### Browse saved artifacts

Open a run's **Changes** tab, or select **Open artifact** on a Priority link.
Choose a session from the run's session list, then a **Saved capture**. A linked
capture is selected when it appears in that session's loaded page. If it is
not on the page, choose another session or select **Load more captures**.
The service lists captures newest first.

The detail view shows the saved branch, time, and file count. Select a file in
the tree to read it. Markdown opens in **Preview**; select **Source** to read
the text. Other text files open as source. Binary files are labeled **Binary
file**. The client checks each file's size and SHA-256 against the saved
manifest before showing its content.

These are saved outputs, not the current workspace. Empty lists show **No saved
captures**; failed or unknown runs may withhold captures. Read failures show an
error and **Try again**. Changing run, session, capture, or confirmed access
discards the old view and ignores late responses. The service rechecks read
access on each request. Artifact links still have no lifecycle state, so their
state badge is **Unavailable** even when the saved files can be opened.

The file tree and Source/Preview viewer reuse the 0.x components. The saved
capture list and Priority picker are new screens built on the Gateway capture
routes; 0.x had no equivalent.
All capture and file reads use a token bound to the selected run. Saving a
Priority link uses the caller's unbound token and does not start work.

### Flow

Open a project and choose **Flow** (`/projects/:projectId/flow`).

- **Needs attention** shows new task notifications and links to the Board.
- **Needs input** lists pending gates from your notifications for the project,
  including dismissed ones. Backlog promotion notifications aren't gates and
  don't appear. Select an item to open its decision on the run page.
- **Open** lists idle runs. **Working** lists active and blocked runs.
- **Done** lists ended runs. **Done**, **Failed**, **Cancelled**, and **Outcome
  unknown** keep their own labels.
- Run groups show **Pull request: Unavailable**. Stored project records do not
  establish each run's current provider-side pull request state.

Before it approves, rejects, or answers, the decision view rereads the gate.
If the gate is gone or its state version changed, it shows **This request
changed since you opened it. Nothing was sent. Review it and try again.**

### Notification bell

The bell in the navigation is your attention inbox across projects. It lists
the same pending gates and new task notifications as **Overview**, read with an
unbound token, and hides dismissed ones. Select an item to open its decision
on the run page, where you approve, reject, or answer with a token bound to
that run. Select **Dismiss** to hide an item from the bell and Overview. Flow
still lists it while the gate is pending. New task notifications open the project
Board and show the number of subtasks created.

## Deployment

Orchestrator migration 024 creates the Chief of Staff tables, and
`infra/bicep/postgres-runtime-grants.sql` grants the runtime role access. Apply
both through the reviewed PostgreSQL plan. For steps, see
[Deploy Agentweaver to Azure](./deploy-to-azure.md).

Core migration 033 adds nullable repository identity columns and two partial
unique indexes to existing Priority links. It preserves every existing row
and removes only the obsolete number-only index after its replacements exist.
It runs in the migrator transaction and can block link writes while it validates
and builds the indexes. Existing runtime table grants remain sufficient.
It does not apply or require optional PR-recording migration 032.

Deploy the owner built from this source before applying 033. Its startup and
readiness checks accept the complete schema through 031, with optional 032
and optional 033. On 031, existing operations work, while new issue/PR link
saves return `503 database_update_pending`. On 033, repository-aware links work.
Do not roll back to an older owner after 033: older images reject that unknown
schema version. Rolling back to this compatible owner works on either shape.

| Owner image family | Schema through 031 | Schema with 033 |
| --- | --- | --- |
| Before repository-identity support | Ready | Not supported; startup/readiness reject the unknown version |
| Repository-identity compatible source | Ready; repository link saves wait for the update | Ready; full repository link behavior |

The machine-readable contract is
`services/orchestrator/Agentweaver.Orchestrator/schema-compatibility.json`.
Its required, optional and default-apply version lists describe this image's
behavior. The P1 plan includes 033 but skips 017 and 032; regenerating the plan
changes its hash. Schema compatibility is not permission to apply the plan.

## Related content

- [API reference: Chief of Staff](../reference/api.md#chief-of-staff)
- [MCP tools](../reference/mcp-tools.md)
- [Gateway/BFF](./gateway.md)
