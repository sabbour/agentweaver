#!/usr/bin/env node
// Converts Mermaid *flowchart* blocks into the docs/diagrams graph-spec JSON
// shape (see docs/diagrams/src/graph-spec.schema.json), so the deep-dive/etc.
// docs can render through the same Fluent-styled @xyflow/react + dagre pipeline
// as the AKS architecture diagrams instead of raw ```mermaid fences.
//
// Only `flowchart` / `graph` diagrams are convertible -- the graph-spec is a
// node/edge/group model. `sequenceDiagram`, `stateDiagram`, `classDiagram` and
// `erDiagram` blocks are NOT representable and are left untouched (the caller
// records them as skipped follow-ups).
//
// The Mermaid sources in this repo carry real semantics we can lift:
//   * `class <id> <category>` assignments (client/svc/core/data/ext/runtime/evt)
//     -> badge text/tone + icon
//   * node shapes ( [( )] cylinder, { } decision, ([ ]) terminal, ... ) -> icon
//   * `subgraph ... end` clusters (nestable) -> graph-spec groups w/ tiers
// so the generated cards are meaningfully iconed/badged, not generic boxes.

/** @typedef {{ id:string, label?:string, subLabel?:string, meta?:string, icon:string, badge:{text:string,tone:string}, group?:string }} SpecNode */

const ICON_ENUM = new Set(['globe', 'branch', 'route', 'window', 'server', 'bot', 'database', 'key', 'box']);

// Category (from Mermaid `class <id> <cat>`) -> icon + badge. Palette mirrors
// the classDef fills used across the repo's Mermaid sources and the existing
// hand-authored AKS specs' badge conventions.
const CATEGORY_STYLE = {
  client: { icon: 'globe', badge: { text: 'Client', tone: 'lavender' } },
  svc: { icon: 'window', badge: { text: 'Service', tone: 'teal' } },
  core: { icon: 'server', badge: { text: 'Compute', tone: 'green' } },
  data: { icon: 'database', badge: { text: 'Data', tone: 'teal' } },
  ext: { icon: 'branch', badge: { text: 'External', tone: 'lavender' } },
  runtime: { icon: 'bot', badge: { text: 'Runtime', tone: 'green' } },
  evt: { icon: 'box', badge: { text: 'Events', tone: 'teal' } },
};

// Shape -> default icon + badge, used when a node has no explicit class.
const SHAPE_STYLE = {
  cylinder: { icon: 'database', badge: { text: 'Data', tone: 'teal' } },
  stadium: { icon: 'box', badge: { text: 'Terminal', tone: 'neutral' } },
  rhombus: { icon: 'branch', badge: { text: 'Decision', tone: 'marigold' } },
  hexagon: { icon: 'route', badge: { text: 'Gateway', tone: 'teal' } },
  circle: { icon: 'box', badge: { text: 'State', tone: 'neutral' } },
};

// Keyword heuristics on id+label, lowest priority. First match wins.
const KEYWORD_RULES = [
  [/\b(git|github|repo|worktree|branch)\b/, { icon: 'branch', badge: { text: 'Git', tone: 'lavender' } }],
  [/\b(user|human|operator|reviewer|client|browser|internet|caller)\b/, { icon: 'globe', badge: { text: 'Client', tone: 'lavender' } }],
  [/\b(web|ui|frontend|spa|console|portal)\b/, { icon: 'window', badge: { text: 'UI', tone: 'teal' } }],
  [/\b(gateway|route|ingress|network|proxy|relay|sse)\b/, { icon: 'route', badge: { text: 'Network', tone: 'teal' } }],
  [/\b(key|secret|vault|token|oauth|auth|credential|sign)\b/, { icon: 'key', badge: { text: 'Security', tone: 'marigold' } }],
  [/\b(agent|bot|sandbox|model|copilot|llm|runtime|worker|pod)\b/, { icon: 'bot', badge: { text: 'Runtime', tone: 'green' } }],
  [/\b(db|database|postgres|sqlite|store|storage|pvc|volume|memory|persist)\b/, { icon: 'database', badge: { text: 'Data', tone: 'teal' } }],
  [/\b(event|stream|queue|log|telemetry|observ)\b/, { icon: 'box', badge: { text: 'Events', tone: 'teal' } }],
  [/\b(api|service|server|control|orchestrat|coordinator|engine)\b/, { icon: 'server', badge: { text: 'Service', tone: 'green' } }],
];

const DEFAULT_STYLE = { icon: 'box', badge: { text: 'Step', tone: 'neutral' } };

function decodeEntities(s) {
  return s
    .replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&');
}

function cleanLabel(raw) {
  return decodeEntities(raw)
    .replace(/^["'`](.*)["'`]$/s, '$1')
    .trim();
}

/** Split a node label on <br/> into up to label/subLabel/meta lines. */
function splitLines(raw) {
  const parts = cleanLabel(raw)
    .split(/<br\s*\/?>|\\n/i)
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  return { label: parts[0] ?? '', subLabel: parts[1], meta: parts.slice(2).join(' · ') || undefined };
}

function cleanEdgeLabel(raw) {
  if (raw == null) return undefined;
  const s = decodeEntities(raw)
    .replace(/<br\s*\/?>|\\n/gi, ' ')
    .replace(/^["'`](.*)["'`]$/s, '$1')
    .replace(/\s+/g, ' ')
    .trim();
  return s.length ? s : undefined;
}

const SHAPES = [
  ['[(', ')]', 'cylinder'],
  ['([', '])', 'stadium'],
  ['[[', ']]', 'subroutine'],
  ['[/', '/]', 'parallelogram'],
  ['[\\', '\\]', 'parallelogram'],
  ['{{', '}}', 'hexagon'],
  ['((', '))', 'circle'],
  ['{', '}', 'rhombus'],
  ['(', ')', 'rounded'],
  ['[', ']', 'rect'],
  ['>', ']', 'flag'],
];

/** Parse one node reference chunk, e.g. `A["Label<br/>x"]`, `B{dec}`, `C[(db)]`, or bare `A`. */
function parseNodeRef(chunk) {
  const trimmed = chunk.trim();
  const idMatch = trimmed.match(/^([A-Za-z0-9_.-]+)/);
  if (!idMatch) return null;
  const id = idMatch[1];
  const rest = trimmed.slice(id.length).trim();
  if (rest === '') return { id };
  for (const [open, close, shape] of SHAPES) {
    if (rest.startsWith(open) && rest.endsWith(close) && rest.length >= open.length + close.length) {
      const inner = rest.slice(open.length, rest.length - close.length);
      return { id, shape, rawLabel: inner };
    }
  }
  return { id };
}

function styleFor(node) {
  if (node.category && CATEGORY_STYLE[node.category]) return CATEGORY_STYLE[node.category];
  if (node.shape && SHAPE_STYLE[node.shape]) return SHAPE_STYLE[node.shape];
  const hay = `${node.id} ${node.label ?? ''} ${node.subLabel ?? ''}`.toLowerCase();
  for (const [re, style] of KEYWORD_RULES) if (re.test(hay)) return style;
  return DEFAULT_STYLE;
}

// Normalise Mermaid's middle-label edge forms (`A -- text --> B`,
// `A -. text .-> B`, `A == text ==> B`) into the pipe form (`A -->|text| B`)
// so a single connector regex can tokenise the statement.
function normaliseMiddleLabels(stmt) {
  return stmt
    .replace(/-\.\s+([^|]+?)\s+\.->/g, '-.->|$1|')
    .replace(/-\.\s+([^|]+?)\s+\.-/g, '-.-|$1|')
    .replace(/==\s+([^|]+?)\s+==>/g, '==>|$1|')
    .replace(/==\s+([^|]+?)\s+===/g, '===|$1|')
    .replace(/--\s+([^|]+?)\s+-->/g, '-->|$1|')
    .replace(/--\s+([^|]+?)\s+---/g, '---|$1|');
}

const CONNECTOR_RE = /(-\.->|-\.-|-->|---|==>|===)\s*(?:\|([^|]*)\|)?\s*/g;

// Split a chunk on Mermaid's `A & B` multi-node separator, but NOT on `&`
// that begins an HTML entity (&gt; &amp; &#39; ...) inside a label.
function splitNodeList(chunk) {
  return chunk.split(/&(?!#?[a-zA-Z0-9]+;)/);
}

/**
 * Convert a single Mermaid flowchart string to a graph-spec object.
 * @returns {{ spec: object, warnings: string[] } | null} null if not a flowchart.
 */
export function convertFlowchart(src, { title, name } = {}) {
  const rawLines = src.split(/\r?\n/);
  const lines = [];
  for (let line of rawLines) {
    line = line.replace(/%%\{[\s\S]*?\}%%/g, '').trim();
    if (line === '') continue;
    if (line.startsWith('%%')) continue; // comment
    lines.push(line);
  }
  if (lines.length === 0) return null;

  const header = lines[0].match(/^(flowchart|graph)\s+(TB|TD|BT|RL|LR)/i);
  if (!header) return null;
  const dir = header[2].toUpperCase();
  const direction = dir === 'LR' || dir === 'RL' ? 'LR' : 'TB';

  const warnings = [];
  /** @type {Map<string, any>} */
  const nodes = new Map();
  const groupIds = new Set();
  const groups = [];
  /** @type {{from:string,to:string,label?:string,dashed?:boolean,undirected?:boolean}[]} */
  const edges = [];
  const stack = []; // subgraph id stack
  let genGroup = 0;

  function ensureNode(ref) {
    if (!ref) return;
    if (!nodes.has(ref.id)) {
      nodes.set(ref.id, { id: ref.id, refOnly: true });
    }
    const n = nodes.get(ref.id);
    if (ref.shape || ref.rawLabel != null) {
      n.refOnly = false;
      if (ref.shape) n.shape = ref.shape;
      if (ref.rawLabel != null) {
        const { label, subLabel, meta } = splitLines(ref.rawLabel);
        n.label = label;
        if (subLabel) n.subLabel = subLabel;
        if (meta) n.meta = meta;
      }
      if (stack.length && !n.group) n.group = stack[stack.length - 1];
    } else if (stack.length && !n.group && n.refOnly) {
      // a bare reference inside a subgraph still belongs to it
      n.group = stack[stack.length - 1];
    }
  }

  for (let i = 1; i < lines.length; i += 1) {
    const line = lines[i];

    if (/^end\b/.test(line)) {
      stack.pop();
      continue;
    }
    const sg = line.match(/^subgraph\s+(.+)$/);
    if (sg) {
      const body = sg[1].trim();
      let id;
      let label;
      let m;
      if ((m = body.match(/^([A-Za-z0-9_.-]+)\s*\[(.+)\]\s*$/))) {
        id = m[1];
        label = cleanLabel(m[2]);
      } else if ((m = body.match(/^([A-Za-z0-9_.-]+)\s*$/))) {
        id = m[1];
        label = m[1];
      } else if ((m = body.match(/^["'`](.+)["'`]\s*$/))) {
        id = `grp${genGroup++}`;
        label = m[1];
      } else {
        id = `grp${genGroup++}`;
        label = cleanLabel(body);
      }
      groupIds.add(id);
      groups.push({ id, label, tier: stack.length + 1, parent: stack.length ? stack[stack.length - 1] : undefined });
      stack.push(id);
      continue;
    }

    // skip styling / directives that carry no topology
    if (/^(classDef|class|style|linkStyle|direction|click|%%)/.test(line)) {
      const cls = line.match(/^class\s+([^\s]+)\s+(\w+)\s*;?\s*$/);
      if (cls) {
        const ids = cls[1].split(',').map((s) => s.trim());
        for (const id of ids) {
          if (!nodes.has(id)) nodes.set(id, { id, refOnly: true });
          nodes.get(id).category = cls[2];
        }
      }
      continue;
    }

    // edge / node statement
    const stmt = normaliseMiddleLabels(line);
    CONNECTOR_RE.lastIndex = 0;
    if (!CONNECTOR_RE.test(stmt)) {
      // standalone node definition (possibly `A & B`)
      for (const part of splitNodeList(stmt)) {
        const ref = parseNodeRef(part);
        if (ref) ensureNode(ref);
      }
      continue;
    }

    CONNECTOR_RE.lastIndex = 0;
    const chainNodes = [];
    const conns = [];
    let last = 0;
    let m;
    while ((m = CONNECTOR_RE.exec(stmt))) {
      chainNodes.push(stmt.slice(last, m.index).trim());
      conns.push({ op: m[1], label: m[2] });
      last = CONNECTOR_RE.lastIndex;
    }
    chainNodes.push(stmt.slice(last).trim());

    const parsedGroups = chainNodes.map((c) => splitNodeList(c).map((p) => parseNodeRef(p)).filter(Boolean));
    for (const grp of parsedGroups) for (const ref of grp) ensureNode(ref);

    for (let c = 0; c < conns.length; c += 1) {
      const { op, label } = conns[c];
      const dashed = op.includes('.');
      const undirected = !op.includes('>');
      const froms = parsedGroups[c];
      const tos = parsedGroups[c + 1];
      for (const f of froms) {
        for (const t of tos) {
          edges.push({
            from: f.id,
            to: t.id,
            label: cleanEdgeLabel(label),
            dashed: dashed || undefined,
            undirected: undirected || undefined,
          });
        }
      }
    }
  }

  // Graph-spec edges connect leaf cards, not clusters. When a Mermaid edge
  // touches a subgraph container, remap that endpoint to the cluster's first
  // member card so the relationship is preserved (approximated) rather than
  // lost; only drop it if the cluster has no members.
  const firstMemberOf = new Map();
  for (const [, n] of nodes) {
    if (n.group && !groupIds.has(n.id) && !firstMemberOf.has(n.group)) {
      firstMemberOf.set(n.group, n.id);
    }
  }
  function resolveEndpoint(id, warnLabel) {
    if (!groupIds.has(id)) return id;
    const member = firstMemberOf.get(id);
    if (member) {
      warnings.push(`${warnLabel}: remapped cluster endpoint '${id}' to member '${member}' (edges to subgraphs approximated)`);
      return member;
    }
    warnings.push(`${warnLabel}: dropped edge touching empty cluster '${id}'`);
    return null;
  }
  const keptEdges = [];
  for (const e of edges) {
    const from = resolveEndpoint(e.from, `${e.from}->${e.to}`);
    const to = resolveEndpoint(e.to, `${e.from}->${e.to}`);
    if (!from || !to || from === to) continue;
    const clean = { from, to };
    if (e.label) clean.label = e.label;
    if (e.dashed) clean.dashed = true;
    if (e.undirected) clean.undirected = true;
    keptEdges.push(clean);
  }

  const specNodes = [];
  for (const n of nodes.values()) {
    if (groupIds.has(n.id)) continue; // it's a cluster, not a card
    const style = styleFor(n);
    /** @type {SpecNode} */
    const sn = {
      id: n.id,
      label: n.label ?? n.id,
      icon: ICON_ENUM.has(style.icon) ? style.icon : 'box',
      badge: { text: style.badge.text, tone: style.badge.tone },
    };
    if (n.subLabel) sn.subLabel = n.subLabel;
    if (n.meta) sn.meta = n.meta;
    if (n.group) sn.group = n.group;
    specNodes.push(sn);
  }

  if (specNodes.length === 0) return null;

  // Prune empty groups (no member node references them).
  const usedGroups = new Set(specNodes.map((n) => n.group).filter(Boolean));
  // keep a group if it (transitively) contains a used group, too
  let changed = true;
  while (changed) {
    changed = false;
    for (const g of groups) {
      if (usedGroups.has(g.id) && g.parent && !usedGroups.has(g.parent)) {
        usedGroups.add(g.parent);
        changed = true;
      }
    }
  }
  const specGroups = groups
    .filter((g) => usedGroups.has(g.id))
    .map((g) => (g.parent && usedGroups.has(g.parent) ? { id: g.id, label: g.label, tier: g.tier, parent: g.parent } : { id: g.id, label: g.label, tier: g.tier }));

  const altNames = specNodes.map((n) => n.label).slice(0, 12);
  const alt = `${title ?? name ?? 'Diagram'}: ${altNames.join(', ')}${specNodes.length > 12 ? ', …' : ''}`;

  const spec = {
    $schema: './graph-spec.schema.json',
    title: title ?? name ?? 'Diagram',
    alt,
    direction,
    ...(specGroups.length ? { groups: specGroups } : {}),
    nodes: specNodes,
    edges: keptEdges,
  };

  return { spec, warnings };
}

/** Detect the Mermaid diagram type keyword of a fence body. */
export function mermaidType(body) {
  for (let line of body.split(/\r?\n/)) {
    line = line.replace(/%%\{[\s\S]*?\}%%/g, '').trim();
    if (line === '' || line.startsWith('%%')) continue;
    const m = line.match(/^(flowchart|graph|sequenceDiagram|stateDiagram-v2|stateDiagram|classDiagram|erDiagram|journey|gantt|pie|gitGraph|mindmap|timeline|quadrantChart)\b/);
    return m ? m[1] : 'unknown';
  }
  return 'unknown';
}
