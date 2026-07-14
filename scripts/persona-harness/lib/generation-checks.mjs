// Structural validators for GENERATED artifacts (issue #1 expansion, requirement 2).
//
// The harness must verify that artifacts produced by the LLM-backed generators
// (blueprint rosters, workflow YAML, team casts) are STRUCTURALLY CORRECT — not
// merely non-empty — before a human ever looks at them. These are the seams where
// real bugs slip through (e.g. issue #311: a generated roster that includes a
// reserved system role a human had to catch by hand).
//
// Two mirrors of backend truth live here so the harness fails on exactly what the
// backend would reject (or should have):
//   1. reserved-role denylist  — mirrors packages/Agentweaver.Squad/Catalog/ReservedRoles.cs
//   2. workflow YAML validation — mirrors apps/Agentweaver.Api/Workflows/WorkflowDefinitionLoader.cs
//
// Pure functions only (no network) so they are unit-testable with adversarial
// fixtures; see test/generation-checks.test.mjs.

import { parse as parseYaml } from 'yaml';

// ── Reserved orchestration roles (mirror of ReservedRoles.cs) ───────────────────
//
// Scribe, Work Monitor ("Ralph"), Rai (responsible-AI review), and Coordinator are
// provisioned automatically for EVERY team by CastingService. A blueprint- or
// workflow-generated roster must NEVER offer them as a domain team member — that is
// the class of bug issue #311 was.

/** Cast/agent display names reserved for built-in orchestration agents. */
export const RESERVED_NAMES = ['Scribe', 'Ralph', 'Rai', 'Coordinator'];

/** Catalog/role ids reserved for built-in orchestration agents. */
export const RESERVED_IDENTIFIERS = new Set([
  'scribe',
  'work-monitor',
  'ralph',
  'rai',
  'rai-reviewer',
  'coordinator',
]);

/**
 * Whether a role id, bespoke role id, or agent/role display name refers to a
 * reserved orchestration role. Faithful port of ReservedRoles.IsReserved: exact
 * (case-insensitive) match, plus a normalized "Work Monitor"/"work_monitor" → kebab
 * variant so a spaced/underscored display name is caught too.
 * @param {string|null|undefined} roleIdOrName
 * @returns {boolean}
 */
export function isReservedRole(roleIdOrName) {
  if (!roleIdOrName || !String(roleIdOrName).trim()) return false;
  const trimmed = String(roleIdOrName).trim().toLowerCase();
  if (RESERVED_IDENTIFIERS.has(trimmed)) return true;
  const normalized = trimmed.replace(/[ _]/g, '-');
  return RESERVED_IDENTIFIERS.has(normalized);
}

/**
 * Inspect a generated blueprint's roster + bespoke roles for reserved-role leakage
 * (issue #311). Returns the offending values so a finding can name them.
 * @param {{ roster?: string[], bespoke_roles?: {id?:string,title?:string}[], workflowRoles?: string[] }} blueprint
 * @returns {{ offenders: string[] }}
 */
export function findReservedRoleLeaks({ roster = [], bespoke_roles = [], workflowRoles = [] } = {}) {
  const offenders = [];
  for (const r of roster ?? []) if (isReservedRole(r)) offenders.push(String(r));
  for (const b of bespoke_roles ?? []) {
    if (isReservedRole(b?.id)) offenders.push(String(b.id));
    if (isReservedRole(b?.title)) offenders.push(String(b.title));
  }
  for (const r of workflowRoles ?? []) if (isReservedRole(r)) offenders.push(String(r));
  return { offenders: [...new Set(offenders)] };
}

// ── Workflow YAML structural validation (mirror of WorkflowDefinitionLoader) ────

const KNOWN_NODE_TYPES = new Set([
  'prompt',
  'peer_review',
  'build_test',
  'check',
  'fan_out',
  'fan_in',
  'coordinator_composed',
  'serial',
  'merge',
  'scribe',
  'terminal',
]);

/** Mirror of WorkflowDefinitionLoader.Normalize for node types. */
function normalizeType(raw) {
  return String(raw).trim().replace(/[-\s]/g, '_').toLowerCase();
}

/** Mirror of the branch/when normalization (trim + lowercase). */
function normalizeToken(raw) {
  return String(raw).trim().toLowerCase();
}

/**
 * Validate a workflow YAML string against the SAME structural rules the backend
 * enforces in WorkflowDefinitionLoader.Load. Unlike the backend (which fails fast on
 * the first error), this collects ALL violations so a finding is actionable — but the
 * pass/fail contract is identical: `valid` is true iff the backend would accept it.
 *
 * Rules mirrored:
 *   - required id, name, start
 *   - at least one node; each node has id (unique) + known type
 *   - start references an existing node
 *   - every edge from/to references an existing node (no dangling edges)
 *   - check node: has ≥1 outgoing edge, declares ≥1 branch, every branch has a
 *     matching outgoing edge `when`
 *   - serial node: every step references an existing node
 *   - fan_in / peer_review / build_test: target (if present) references an existing node
 *   - stages (if present): every stage has required id + label
 *
 * Explicit non-rules (because the backend currently does NOT reject them in
 * WorkflowDefinitionLoader.cs):
 *   - duplicate stage ids
 *   - stage order collisions / gaps
 *
 * @param {string} yamlText
 * @returns {{ valid: boolean, errors: string[], warnings: string[], nodeCount: number, documentId: string|null, stages: {id:string|null,label:string|null,order:number|null}[] }}
 */
export function validateWorkflowYaml(yamlText) {
  const errors = [];
  const warnings = [];

  let dto;
  try {
    dto = parseYaml(yamlText);
  } catch (ex) {
    return { valid: false, errors: [`malformed YAML — ${ex.message}`], warnings, nodeCount: 0, documentId: null, stages: [] };
  }
  if (dto === null || dto === undefined || typeof dto !== 'object') {
    return { valid: false, errors: ['empty or null workflow document.'], warnings, nodeCount: 0, documentId: null, stages: [] };
  }

  const blank = (v) => v === null || v === undefined || String(v).trim() === '';
  const documentId = blank(dto.id) ? null : String(dto.id);

  if (blank(dto.id)) errors.push("missing required field 'id'.");
  if (blank(dto.name)) errors.push("missing required field 'name'.");

  const rawNodes = Array.isArray(dto.nodes) ? dto.nodes : [];
  if (rawNodes.length === 0) {
    errors.push('a workflow must declare at least one node.');
    return { valid: errors.length === 0, errors, warnings, nodeCount: 0, documentId, stages: [] };
  }

  const nodeIds = new Set();
  const nodes = [];
  for (const n of rawNodes) {
    if (blank(n?.id)) {
      errors.push("a node is missing its required 'id'.");
      continue;
    }
    if (nodeIds.has(n.id)) {
      errors.push(`duplicate node id '${n.id}'.`);
      continue;
    }
    nodeIds.add(n.id);
    if (blank(n?.type)) {
      errors.push(`node '${n.id}' is missing its required 'type'.`);
      continue;
    }
    const type = normalizeType(n.type);
    if (!KNOWN_NODE_TYPES.has(type)) {
      errors.push(`node '${n.id}' has unknown type '${n.type}'.`);
      continue;
    }
    nodes.push({
      id: n.id,
      type,
      role: n.role ?? null,
      agent: n.agent ?? null,
      target: blank(n?.target) ? null : n.target,
      steps: Array.isArray(n.steps) ? n.steps : [],
      branches: (Array.isArray(n.branches) ? n.branches : [])
        .filter((b) => !blank(b))
        .map(normalizeToken),
    });
  }

  if (blank(dto.start)) {
    errors.push("missing required field 'start' (the entry node id).");
  } else if (!nodeIds.has(dto.start)) {
    errors.push(`'start' references unknown node '${dto.start}'.`);
  }

  const edges = [];
  for (const e of Array.isArray(dto.edges) ? dto.edges : []) {
    if (blank(e?.from) || blank(e?.to)) {
      errors.push("an edge is missing its required 'from'/'to'.");
      continue;
    }
    if (!nodeIds.has(e.from)) errors.push(`edge references unknown source node '${e.from}'.`);
    if (!nodeIds.has(e.to)) errors.push(`edge references unknown target node '${e.to}'.`);
    edges.push({ from: e.from, to: e.to, when: blank(e?.when) ? null : normalizeToken(e.when) });
  }

  for (const node of nodes) {
    if (node.type === 'check') {
      const outgoing = edges.filter((x) => x.from === node.id);
      if (outgoing.length === 0) {
        errors.push(`check node '${node.id}' has no outgoing edges to route verdicts.`);
      }
      if (node.branches.length === 0) {
        errors.push(`check node '${node.id}' must declare the verdicts ('branches') it routes on.`);
      }
      for (const verdict of node.branches) {
        if (!outgoing.some((x) => x.when === verdict)) {
          errors.push(`check node '${node.id}' declares verdict '${verdict}' but has no outgoing edge for it.`);
        }
      }
    } else if (node.type === 'serial') {
      for (const step of node.steps) {
        if (!nodeIds.has(step)) {
          errors.push(`serial node '${node.id}' references unknown step '${step}'.`);
        }
      }
    } else if (node.type === 'fan_in' || node.type === 'peer_review' || node.type === 'build_test') {
      if ((node.type === 'peer_review' || node.type === 'build_test') && node.target) {
        warnings.push(`${node.type} node '${node.id}' declares target '${node.target}', but the runtime currently ignores target.`);
      }
      if (node.target !== null && !nodeIds.has(node.target)) {
        errors.push(`node '${node.id}' references unknown target '${node.target}'.`);
      }
    }
  }

  const stages = [];
  for (const s of Array.isArray(dto.stages) ? dto.stages : []) {
    const id = blank(s?.id) ? null : String(s.id);
    const label = blank(s?.label) ? null : String(s.label);
    const order = Number.isFinite(s?.order) ? Number(s.order) : null;
    if (id === null) {
      errors.push("a stage is missing its required 'id'.");
      continue;
    }
    if (label === null) {
      errors.push(`stage '${id}' is missing its required 'label'.`);
      continue;
    }
    stages.push({ id, label, order });
  }

  return { valid: errors.length === 0, errors, warnings, nodeCount: nodes.length, documentId, stages };
}

/**
 * Collect every role/agent id referenced by a workflow's nodes, for a reserved-role
 * cross-check on GENERATED workflows (a generated workflow should not assign work to
 * a reserved orchestration role either).
 * @param {string} yamlText
 * @returns {string[]}
 */
export function workflowNodeRoles(yamlText) {
  let dto;
  try {
    dto = parseYaml(yamlText);
  } catch {
    return [];
  }
  const nodes = Array.isArray(dto?.nodes) ? dto.nodes : [];
  const roles = [];
  for (const n of nodes) {
    if (n?.role) roles.push(String(n.role));
    if (n?.agent) roles.push(String(n.agent));
  }
  return [...new Set(roles)];
}
