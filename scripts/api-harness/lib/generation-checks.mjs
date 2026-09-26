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
    if (type === 'serial') {
      errors.push(`node '${n.id}' uses unsupported node type 'serial'. Use ordinary workflow edges between nodes to express sequential execution.`);
      continue;
    }
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

const BROAD_OUTPUT_SCOPES = new Set([
  '.', 'repo', 'repository', 'workspace', 'source', 'src', 'docs', 'documentation',
  'apps', 'packages', 'tests',
]);

const SHARED_OUTPUT_NAMES = new Set([
  'package.json', 'package-lock.json', 'npm-shrinkwrap.json', 'pnpm-lock.yaml', 'yarn.lock',
  'packages.lock.json', 'nuget.config', 'requirements.txt', 'pyproject.toml', 'poetry.lock',
  'go.mod', 'go.sum', 'cargo.toml', 'cargo.lock', 'composer.json', 'composer.lock',
  'gemfile', 'gemfile.lock', 'global.json', 'directory.build.props',
  'directory.build.targets', 'directory.packages.props',
]);

const CONTENT_OUTPUT_EXTENSIONS = new Set(['.adoc', '.csv', '.markdown', '.md', '.rst', '.tsv', '.txt']);
const CODE_OUTPUT_EXTENSIONS = new Set([
  '.cs', '.fs', '.go', '.java', '.js', '.jsx', '.kt', '.kts', '.mjs', '.py', '.rb',
  '.rs', '.sql', '.ts', '.tsx', '.vb', '.vue',
]);
const STRONG_DEPENDENCY_LANGUAGE = /\b(after|before|then|once|based\s+on|depends?\s+on|wait(?:ing)?\s+for|builds?\s+on|requires?\s+(?:the\s+)?(?:output|result|artifact|report|analysis|findings)|from\s+(?:the\s+)?(?:other|previous|prior|sibling|branch))\b/i;
const CONSUMPTION_LANGUAGE = /\b(consume|consumes|use|uses|using|incorporate|incorporates|integrate|combine|merge|based\s+on|derive(?:d)?\s+from|findings|results?|outputs?|artifacts?|from\s+(?:the\s+)?branch)\b/i;
const SOURCE_MUTATION_LANGUAGE = /\b(?:implement|refactor|modify|update|edit|patch|rewrite|change|generate|build|compile)\b[\s\S]{0,80}\b(?:code|source|project|solution|package|manifest|migration|schema|api|class|module|component|service|repository|repo)\b/i;

function normalizeDeclaredOutputPath(rawPath) {
  const value = String(rawPath ?? '').trim().replaceAll('\\', '/').replace(/\/+/g, '/');
  if (!value || value.startsWith('/') || value.startsWith('~/') || value.includes(':')) return null;
  if (/[?*[\]{}$%<>]/.test(value)) return null;
  const segments = value.split('/');
  if (segments.some((segment) => !segment || segment === '.' || segment === '..')) return null;
  if (BROAD_OUTPUT_SCOPES.has(value.toLowerCase())) return null;
  if (value.endsWith('/')) return null;
  const fileName = segments.at(-1).toLowerCase();
  if (SHARED_OUTPUT_NAMES.has(fileName)) return null;
  const extensionIndex = fileName.lastIndexOf('.');
  const extension = extensionIndex >= 0 ? fileName.slice(extensionIndex) : '';
  if (segments.some((segment) => segment.startsWith('.'))) return null;
  if (CODE_OUTPUT_EXTENSIONS.has(extension) || /\.(sln|csproj|fsproj|vbproj|lock)$/i.test(fileName)) return null;
  if (segments.some((segment) => /^(src|source|app|apps|lib|libs|packages|migrations?|generated|dist|build|obj|bin)$/i.test(segment))) return null;
  if (!fileName.includes('.')) return null;
  if (!CONTENT_OUTPUT_EXTENSIONS.has(extension)) return null;
  return value;
}

function outputPathsOverlap(left, right) {
  const a = left.toLowerCase();
  const b = right.toLowerCase();
  return a === b || a.startsWith(`${b}/`) || b.startsWith(`${a}/`);
}

function promptPathReferences(prompt) {
  const matches = String(prompt).matchAll(
    /(?<![A-Za-z0-9_])(?:\.?[\\/])?[A-Za-z0-9_.-]+(?:[\\/][A-Za-z0-9_.-]+)*\.[A-Za-z0-9]{1,16}(?![A-Za-z0-9_])/g,
  );
  return [...matches].map((match) => String(match[0]).replaceAll('\\', '/').replace(/^\.\/+/, '').toLowerCase());
}

function hasExplicitContentOutputContract(prompt, declaredPaths) {
  const normalizedPrompt = String(prompt).replaceAll('\\', '/');
  return declaredPaths.every((path) => {
    const escaped = path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(
      `\\b(?:write|save|create|produce|publish|export|record|capture)\\s+only\\s+(?:to\\s+)?[\\\`'"]?${escaped}[\\\`'"]?\\b`,
      'i',
    ).test(normalizedPrompt);
  });
}

function graphHasCycle(nodes, edges) {
  const nodeIds = new Set(nodes.map((node) => node?.id).filter(Boolean));
  const outgoing = new Map();
  for (const edge of edges) {
    if (!nodeIds.has(edge?.from) || !nodeIds.has(edge?.to)) continue;
    outgoing.set(edge.from, [...(outgoing.get(edge.from) ?? []), edge.to]);
  }
  const visiting = new Set();
  const visited = new Set();
  function visit(nodeId) {
    if (visiting.has(nodeId)) return true;
    if (visited.has(nodeId)) return false;
    visiting.add(nodeId);
    if ((outgoing.get(nodeId) ?? []).some(visit)) return true;
    visiting.delete(nodeId);
    visited.add(nodeId);
    return false;
  }
  return [...nodeIds].some(visit);
}

/**
 * Classify generated workflow fan topology using the same fail-closed contract as
 * ConservativeWorkflowFanPolicy. This is an acceptance assertion, not a provider
 * substitute: provider-backed generation must still produce the artifact under test.
 *
 * @param {string} yamlText
 * @returns {{ mode: 'fan'|'sequential'|'invalid', safe: boolean, errors: string[], branchIds: string[], outputPaths: string[] }}
 */
export function analyzeConservativeFan(yamlText) {
  let dto;
  try {
    dto = parseYaml(yamlText);
  } catch (ex) {
    return { mode: 'invalid', safe: false, errors: [`malformed YAML — ${ex.message}`], branchIds: [], outputPaths: [] };
  }

  const nodes = Array.isArray(dto?.nodes) ? dto.nodes : [];
  const edges = Array.isArray(dto?.edges) ? dto.edges : [];
  const fanOuts = nodes.filter((node) => normalizeType(node?.type ?? '') === 'fan_out');
  const fanIns = nodes.filter((node) => normalizeType(node?.type ?? '') === 'fan_in');
  if (nodes.some((node) => ['serial', 'coordinator_composed'].includes(normalizeType(node?.type ?? '')))) {
    return { mode: 'invalid', safe: false, errors: ['generated workflow contains a prohibited orchestration node type.'], branchIds: [], outputPaths: [] };
  }
  if (fanOuts.length === 0 && fanIns.length === 0) {
    return { mode: 'sequential', safe: true, errors: [], branchIds: [], outputPaths: [] };
  }
  if (fanOuts.length !== 1 || fanIns.length !== 1) {
    return { mode: 'invalid', safe: false, errors: ['generated fan topology must contain exactly one fan_out and one fan_in.'], branchIds: [], outputPaths: [] };
  }

  const fanOut = fanOuts[0];
  const fanIn = fanIns[0];
  const nodeById = new Map(nodes.map((node) => [node?.id, node]));
  const fanOutIncoming = edges.filter((edge) => edge?.to === fanOut.id);
  const fanOutOutgoing = edges.filter((edge) => edge?.from === fanOut.id);
  if (fanOutOutgoing.length < 2 || fanOutOutgoing.some((edge) => edge?.when)) {
    return { mode: 'invalid', safe: false, errors: ['fan_out must declare at least two distinct unconditional branches.'], branchIds: [], outputPaths: [] };
  }
  if ((dto?.start === fanOut.id && fanOutIncoming.length !== 0)
    || (dto?.start !== fanOut.id
      && (fanOutIncoming.length !== 1
        || fanOutIncoming[0]?.when
        || normalizeType(nodeById.get(fanOutIncoming[0]?.from)?.type ?? '') !== 'prompt'))) {
    return { mode: 'invalid', safe: false, errors: ['fan_out must be the start or have exactly one unconditional prompt input.'], branchIds: [], outputPaths: [] };
  }
  const branchIds = fanOutOutgoing.map((edge) => edge.to);
  if (branchIds.length < 2 || new Set(branchIds).size !== branchIds.length) {
    return { mode: 'invalid', safe: false, errors: ['fan_out must declare at least two distinct unconditional branches.'], branchIds, outputPaths: [] };
  }
  if (fanIn.target !== fanOut.id) {
    return { mode: 'invalid', safe: false, errors: ['fan_in target must reference the fan_out node.'], branchIds, outputPaths: [] };
  }

  const errors = [];
  const normalizedByBranch = [];
  for (const branchId of branchIds) {
    const branch = nodeById.get(branchId);
    if (!branch || normalizeType(branch.type ?? '') !== 'prompt') {
      return { mode: 'invalid', safe: false, errors: [`fan branch '${branchId}' must be a prompt node.`], branchIds, outputPaths: [] };
    }
    const incoming = edges.filter((edge) => edge?.to === branchId);
    const outgoing = edges.filter((edge) => edge?.from === branchId);
    if (incoming.length !== 1 || incoming[0].from !== fanOut.id || incoming[0].when
      || outgoing.length !== 1 || outgoing[0].to !== fanIn.id || outgoing[0].when) {
      return { mode: 'invalid', safe: false, errors: [`fan branch '${branchId}' must connect only from fan_out to fan_in.`], branchIds, outputPaths: [] };
    }
    if (branch.independent !== true) errors.push(`branch '${branchId}' does not declare independent: true.`);
    const rawPaths = Array.isArray(branch.declared_output_paths) ? branch.declared_output_paths : [];
    if (rawPaths.length === 0) errors.push(`branch '${branchId}' has no declared_output_paths.`);
    const normalizedPaths = rawPaths.map(normalizeDeclaredOutputPath);
    if (normalizedPaths.some((path) => path === null)) {
      errors.push(`branch '${branchId}' declares an unknown, dynamic, broad, or shared output scope.`);
    }
    const prompt = String(branch.prompt ?? '');
    const validPaths = normalizedPaths.filter(Boolean);
    const undeclaredReferences = promptPathReferences(prompt)
      .filter((path) => !validPaths.some((declaredPath) => declaredPath.toLowerCase() === path));
    if (undeclaredReferences.length > 0) {
      errors.push(`branch '${branchId}' references undeclared output paths: ${undeclaredReferences.join(', ')}.`);
    }
    if (!hasExplicitContentOutputContract(prompt, validPaths)) {
      errors.push(`branch '${branchId}' lacks an explicit content-only output contract.`);
    }
    if (SOURCE_MUTATION_LANGUAGE.test(prompt)) {
      errors.push(`branch '${branchId}' contains source mutation language.`);
    }
    if (STRONG_DEPENDENCY_LANGUAGE.test(prompt)) {
      errors.push(`branch '${branchId}' contains dependency language.`);
    }
    normalizedByBranch.push({
      id: branchId,
      label: String(branch.label ?? ''),
      prompt: prompt.toLowerCase(),
      paths: validPaths,
    });
  }

  const fanInIncoming = edges.filter((edge) => edge?.to === fanIn.id);
  if (fanInIncoming.length !== branchIds.length
    || fanInIncoming.some((edge) => edge?.when)
    || new Set(fanInIncoming.map((edge) => edge?.from)).size !== branchIds.length
    || fanInIncoming.some((edge) => !branchIds.includes(edge?.from))) {
    return { mode: 'invalid', safe: false, errors: ['fan_in inputs must exactly match the fan branch set.'], branchIds, outputPaths: [] };
  }
  const continuationEdges = edges.filter((edge) => edge?.from === fanIn.id);
  const continuation = continuationEdges[0];
  const continuationNode = continuation ? nodeById.get(continuation.to) : null;
  if (continuationEdges.length !== 1 || continuation?.when || !continuationNode
    || !['prompt', 'terminal'].includes(normalizeType(continuationNode.type ?? ''))
    || [fanOut.id, fanIn.id, ...branchIds].includes(continuation?.to)) {
    return { mode: 'invalid', safe: false, errors: ['fan_in must have exactly one unconditional continuation outside the fan region.'], branchIds, outputPaths: [] };
  }
  if (graphHasCycle(nodes, edges)) {
    return { mode: 'invalid', safe: false, errors: ['workflow graph contains a cycle.'], branchIds, outputPaths: [] };
  }

  for (let i = 0; i < normalizedByBranch.length; i += 1) {
    const left = normalizedByBranch[i];
    const siblingIdentifiers = normalizedByBranch
      .filter((_, siblingIndex) => siblingIndex !== i)
      .flatMap((branch) => [
        branch.id,
        branch.label,
        ...branch.paths.flatMap((path) => [path, path.split('/').at(-1)]),
      ])
      .filter(Boolean)
      .map((identifier) => identifier.toLowerCase());
    if (CONSUMPTION_LANGUAGE.test(left.prompt)
      && siblingIdentifiers.some((identifier) => left.prompt.includes(identifier))) {
      errors.push(`branch '${left.id}' appears to depend on a sibling branch.`);
    }
    for (let j = i + 1; j < normalizedByBranch.length; j += 1) {
      const right = normalizedByBranch[j];
      if (left.paths.some((a) => right.paths.some((b) => outputPathsOverlap(a, b)))) {
        errors.push(`branches '${left.id}' and '${right.id}' declare overlapping output paths.`);
      }
    }
  }

  const outputPaths = normalizedByBranch.flatMap((branch) => branch.paths);
  return errors.length === 0
    ? { mode: 'fan', safe: true, errors: [], branchIds, outputPaths }
    : { mode: 'fan', safe: false, errors, branchIds, outputPaths };
}
