// lib/approvals.mjs — DETERMINISTIC approval-gate DETECTION for the persona harness.
//
// This is DRIVER code: it does ZERO subjective reasoning. It only parses the real
// run events feed (GET /api/runs/{id}/events) and reports, structurally, which
// human/tool/shell approval gates are currently PENDING (raised but not yet
// resolved). Whether any of those gated actions SHOULD proceed is NOT decided
// here — that judgment is packaged and handed to the separate judge
// (lib/approval-judge.mjs). Keeping detection and judgment in different modules is
// how the driver-only architecture is preserved (see JUDGE.md, decisions.md
// "Persona harness DRIVER/JUDGE separation").
//
// Why the events feed and not /api/notifications:
//   * GET /api/notifications only surfaces `human_review` today; its `tool_approval`
//     type is explicitly RESERVED and not emitted yet (NotificationsService.cs /
//     NotificationsDtos.cs — deferred fast-follow of #247). So it cannot detect the
//     in-the-loop tool/shell action gates at all.
//   * The run events feed is the authoritative, per-run signal AND it carries the
//     exact identifiers needed to actually resolve a gate: the `requestId` for a
//     tool-approval and the `command_hash` for a shell-approval. Detecting from the
//     same payload we must POST back is the most reliable, race-free signal.
//
// Event vocabulary (packages/Agentweaver.Domain/EventTypes.cs):
//   REQUIRED (a gate is raised):
//     - tool.approval_required             { requestId, displayId?, toolName, url?, intention?, message }
//     - coordinator.child_approval_required{ childRunId, subtaskId, requestId, toolName, url?, message? }
//     - shell.approval_required            { requestId, commandHash, command, commandLength, message }
//   RESOLVED (a gate is closed — granted, denied, or timed out):
//     - tool.approval_resolved             { requestId, runId, approved, expired }
//     - coordinator.child_approval_resolved{ childRunId, subtaskId, requestId, approved, expired }
//   (Shell decisions are recorded on a separate durable control log, NOT the run
//    events feed, so shell gates are reconciled via `alreadyResolvedKeys` — the
//    harness remembers which shell command_hashes it already drove this session.)

/** Event type -> gate kind. */
export const APPROVAL_REQUIRED_TYPES = Object.freeze({
  'tool.approval_required': 'tool',
  'coordinator.child_approval_required': 'coordinator-child',
  'shell.approval_required': 'shell',
});

/** Event types that resolve a tool/coordinator-child gate (carry a requestId). */
export const APPROVAL_RESOLVED_TYPES = Object.freeze([
  'tool.approval_resolved',
  'coordinator.child_approval_resolved',
]);

function payloadOf(evt) {
  // The events feed shape is { sequence, type, payload }. Be tolerant of a couple
  // of alternate shapes seen across transcript captures (payload inlined, or
  // snake_case type) so detection never silently misses a gate.
  if (evt == null || typeof evt !== 'object') return {};
  if (evt.payload && typeof evt.payload === 'object') return evt.payload;
  return evt;
}

function firstString(payload, ...names) {
  for (const n of names) {
    const v = payload?.[n];
    if (typeof v === 'string' && v.length > 0) return v;
  }
  return null;
}

/**
 * Stable de-dup / correlation key for a gate. Shell gates key on the command hash
 * (what shell-approvals wants); tool/coordinator gates key on the requestId (what
 * tool-approvals wants). A gate with neither is unaddressable and is dropped.
 * @param {{kind:string, requestId?:string|null, commandHash?:string|null}} gate
 * @returns {string|null}
 */
export function approvalKey(gate) {
  if (!gate) return null;
  if (gate.kind === 'shell') return gate.commandHash ? `shell:${gate.commandHash}` : null;
  return gate.requestId ? `request:${gate.requestId}` : null;
}

/**
 * Build a normalized gate descriptor from a REQUIRED event. Pure structural
 * extraction — no judgment.
 * @param {any} evt one entry from GET /api/runs/{id}/events
 * @returns {object|null}
 */
export function gateFromEvent(evt) {
  const type = evt?.type ?? evt?.eventType ?? null;
  const kind = type ? APPROVAL_REQUIRED_TYPES[type] : undefined;
  if (!kind) return null;
  const payload = payloadOf(evt);

  const requestId = firstString(payload, 'requestId', 'request_id');
  const commandHash = firstString(payload, 'commandHash', 'command_hash');
  const gate = {
    kind,
    type,
    sequence: typeof evt?.sequence === 'number' ? evt.sequence : null,
    requestId: requestId ?? null,
    displayId: firstString(payload, 'displayId', 'display_id'),
    commandHash: commandHash ?? null,
    toolName: firstString(payload, 'toolName', 'tool_name'),
    url: firstString(payload, 'url'),
    intention: firstString(payload, 'intention'),
    command: firstString(payload, 'command'),
    commandLength: typeof payload?.commandLength === 'number' ? payload.commandLength : null,
    childRunId: firstString(payload, 'childRunId', 'child_run_id'),
    subtaskId: firstString(payload, 'subtaskId', 'subtask_id'),
    message: firstString(payload, 'message'),
    // The verbatim event so the judge sees exactly what the backend emitted.
    evidenceEvent: evt,
  };
  gate.key = approvalKey(gate);
  return gate;
}

/**
 * Scan a run's event feed and return the gates that are currently PENDING — raised
 * by a `*_required` event and NOT closed by a matching `*_resolved` event (nor
 * already driven by this harness session, per `alreadyResolvedKeys`).
 *
 * Deterministic and side-effect free. Later duplicate required events for the same
 * key win (they carry the freshest evidence), matching how the backend re-emits a
 * pending gate.
 *
 * @param {any[]} events events from GET /api/runs/{id}/events (ordered by sequence)
 * @param {Object} [opts]
 * @param {Iterable<string>} [opts.alreadyResolvedKeys] gate keys the harness has already
 *        driven this session (chiefly shell gates, whose resolution is not on this feed).
 * @returns {{pending: object[], resolvedRequestIds: string[], allGates: object[]}}
 */
export function detectPendingApprovals(events, opts = {}) {
  const list = Array.isArray(events) ? events : [];
  const alreadyResolved = new Set(opts.alreadyResolvedKeys ?? []);

  // requestIds closed by a resolved event on this feed.
  const resolvedRequestIds = new Set();
  for (const evt of list) {
    const type = evt?.type ?? evt?.eventType ?? null;
    if (type && APPROVAL_RESOLVED_TYPES.includes(type)) {
      const rid = firstString(payloadOf(evt), 'requestId', 'request_id');
      if (rid) resolvedRequestIds.add(rid);
    }
  }

  /** @type {Map<string, object>} keyed by gate.key, latest-wins. */
  const byKey = new Map();
  const allGates = [];
  for (const evt of list) {
    const gate = gateFromEvent(evt);
    if (!gate || !gate.key) continue;
    allGates.push(gate);
    byKey.set(gate.key, gate);
  }

  const pending = [];
  for (const gate of byKey.values()) {
    if (gate.requestId && resolvedRequestIds.has(gate.requestId)) continue;
    if (alreadyResolved.has(gate.key)) continue;
    pending.push(gate);
  }
  // Deterministic order: by sequence, then key.
  pending.sort((a, b) => (a.sequence ?? 0) - (b.sequence ?? 0) || String(a.key).localeCompare(String(b.key)));

  return { pending, resolvedRequestIds: [...resolvedRequestIds], allGates };
}

/**
 * A compact, human-readable one-liner describing WHAT is being gated — for logs and
 * transcript notes. Objective description only.
 * @param {object} gate
 */
export function describeGate(gate) {
  if (!gate) return '(no gate)';
  if (gate.kind === 'shell') {
    const cmd = gate.command ? ` \`${String(gate.command).slice(0, 120)}\`` : '';
    return `shell command gate (requestId=${gate.requestId ?? '?'}, commandHash=${gate.commandHash ?? '?'})${cmd}`;
  }
  const where = gate.childRunId ? ` on child run ${gate.childRunId}` : '';
  const url = gate.url ? ` url=${gate.url}` : '';
  return `${gate.kind} gate (requestId=${gate.requestId ?? '?'}, tool=${gate.toolName ?? '?'})${url}${where}`;
}
