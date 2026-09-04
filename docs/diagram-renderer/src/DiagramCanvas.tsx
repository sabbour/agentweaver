import { useEffect, useMemo, useState } from 'react';
import {
  Background,
  MarkerType,
  ReactFlow,
  type Edge,
  type Node,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { CardNode, GroupNode, GroupLabelNode, CARD_WIDTH, CARD_HEIGHT_2, CARD_HEIGHT_3 } from './nodes';
import { RoutedEdge, type Point } from './edges';
import { neutral, radius } from './theme';
import type { GraphSpec, GraphNode } from './types';

// Banded-lane layout, mirroring the deterministic column placement in
// apps/web/src/components/ClusterTopologyGraph.tsx rather than dagre's
// iterative ranking. Each group becomes a horizontal band; nodes inside a
// band sit on a fixed grid. Edges are routed orthogonally through the
// gutters BETWEEN bands, so a line never crosses a card and parallel runs
// never collapse onto each other. Deterministic geometry also means the same
// spec always renders identically, which dagre's ordering heuristics do not
// guarantee.

const CARD_H_2 = CARD_HEIGHT_2;
const CARD_H_3 = CARD_HEIGHT_3;
const COL_GAP = 56;
// Gaps are floors, not fixed sizes: the real height of a gutter comes from the
// lanes and labels it carries (see gapFor / rowGaps below). These floors only
// have to keep a connector from touching the cards at either end, so they are
// deliberately small -- an arrow between two steps of a flowchart needs far
// less room than the old fixed 104/130 reserved, which is what left every
// layered diagram looking stretched out.
const ROW_GAP = 62;
const BAND_GAP_MIN = 58;
const GAP_PAD = 28;
const GROUP_PAD_SIDE = 48;
const GROUP_PAD_TOP = 96;
const GROUP_PAD_BOTTOM = 44;
const CANVAS_MARGIN = 80;
const LANE_STEP = 34;

// Label metrics. These mirror the inline styles in edges.tsx (15px / 600 /
// 1.1 line-height, 9px horizontal and 5px vertical padding, 1px border) so
// the layout can reserve real space for a label instead of guessing. A label
// is always drawn centred on its edge's run, which means the run has to be
// long enough and the gutter tall enough to hold it -- both are sized here.
const LABEL_CHAR_W = 8.4;
const LABEL_PAD_X = 20;
const LABEL_LINE_H = 16.5;
const LABEL_PAD_Y = 12;
// Cap for labels on lateral (card-side-to-card-side) edges. Their run is the
// column gap, and that gap widens to fit the label, so an unwrapped long
// label would push the whole row apart. Wrapping trades width for height,
// which the row has to spare.
const LATERAL_LABEL_MAX_W = 150;

const nodeTypes = { card: CardNode, band: GroupNode, bandLabel: GroupLabelNode };
const edgeTypes = { routed: RoutedEdge };

interface LabelGeom {
  lines: string[];
  w: number;
  h: number;
}

function measureLines(lines: string[]): LabelGeom {
  const widest = Math.max(...lines.map((l) => l.length));
  return {
    lines,
    w: widest * LABEL_CHAR_W + LABEL_PAD_X,
    h: lines.length * LABEL_LINE_H + LABEL_PAD_Y,
  };
}

/** Greedy balanced wrap: pick the fewest lines that fit `maxW`, then fill each
 * line to roughly the same length so the block reads as a tidy stack rather
 * than a long line with an orphan. A single word longer than `maxW` is left
 * alone -- breaking mid-word would be worse than an over-wide label. */
function wrapLabel(text: string, maxW: number): string[] {
  if (measureLines([text]).w <= maxW) return [text];
  const words = text.split(/\s+/).filter(Boolean);
  if (words.length < 2) return [text];

  for (let n = 2; n <= Math.min(words.length, 3); n += 1) {
    const target = Math.ceil(text.length / n);
    const lines: string[] = [];
    let cur = '';
    for (const word of words) {
      const next = cur ? `${cur} ${word}` : word;
      if (cur && next.length > target && lines.length < n - 1) {
        lines.push(cur);
        cur = word;
      } else {
        cur = next;
      }
    }
    if (cur) lines.push(cur);
    if (measureLines(lines).w <= maxW) return lines;
  }
  // Nothing fit the cap; use the tightest split we can rather than one long line.
  const target = Math.ceil(text.length / 3);
  const lines: string[] = [];
  let cur = '';
  for (const word of words) {
    const next = cur ? `${cur} ${word}` : word;
    if (cur && next.length > target) {
      lines.push(cur);
      cur = word;
    } else {
      cur = next;
    }
  }
  if (cur) lines.push(cur);
  return lines;
}

function cardHeight(n: GraphNode): number {
  return n.meta ? CARD_H_3 : CARD_H_2;
}

interface Placed {
  node: GraphNode;
  x: number;
  y: number;
  w: number;
  h: number;
  band: number;
  row: number;
  rowBottom: number;
  rowTop: number;
}

interface Band {
  id: string | null;
  label: string | null;
  tier: number;
  nodes: GraphNode[];
  /** True when this band is a derived layer rather than an authored group. A
   * layer is one rank of the flow, so it is always drawn as a single row. */
  layer?: boolean;
  /** Column count for a serpentine (chain) band. */
  snakeCols?: number;
  /** Structural position used to order sections down the page. */
  order?: number;
  /** Declaration index, used to break ties in `order`. */
  seq?: number;
  x: number;
  y: number;
  w: number;
  h: number;
}

function columnsFor(count: number): number {
  if (count <= 4) return count;
  if (count <= 6) return 3;
  if (count <= 8) return 4;
  return 5;
}

/**
 * Ranks nodes by longest path from a source, ignoring edges that close a
 * cycle. This is the first phase of a Sugiyama layered drawing, and it is the
 * common foundation of every derived layout here: layer bands, the ORDER of
 * bands, and where ungrouped nodes sit among authored groups all read from it.
 *
 * Back edges (loops like "request changes" returning to an earlier step) are
 * what make a flowchart cyclic; ranking through them would push their target
 * below its own successors. They are excluded from ranking but still drawn.
 */
function rankNodes(nodes: GraphNode[], edges: GraphSpec['edges']): Map<string, number> {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const real = (edges ?? []).filter((e) => byId.has(e.from) && byId.has(e.to) && e.from !== e.to);

  const outgoing = new Map<string, string[]>();
  for (const e of real) {
    if (!outgoing.has(e.from)) outgoing.set(e.from, []);
    outgoing.get(e.from)!.push(e.to);
  }
  const back = new Set<string>();
  const state = new Map<string, 0 | 1 | 2>();
  const visit = (id: string) => {
    state.set(id, 1);
    for (const next of outgoing.get(id) ?? []) {
      const st = state.get(next) ?? 0;
      if (st === 1) back.add(`${id}->${next}`);
      else if (st === 0) visit(next);
    }
    state.set(id, 2);
  };
  for (const n of nodes) if ((state.get(n.id) ?? 0) === 0) visit(n.id);

  const preds = new Map<string, string[]>();
  for (const n of nodes) preds.set(n.id, []);
  for (const e of real) if (!back.has(`${e.from}->${e.to}`)) preds.get(e.to)!.push(e.from);

  // Longest path from any source. Memoised, and the DAG guarantees termination.
  const rank = new Map<string, number>();
  const rankOf = (id: string): number => {
    const seen = rank.get(id);
    if (seen !== undefined) return seen;
    rank.set(id, 0);
    const ps = preds.get(id) ?? [];
    const r = ps.length === 0 ? 0 : Math.max(...ps.map(rankOf)) + 1;
    rank.set(id, r);
    return r;
  };
  for (const n of nodes) rankOf(n.id);
  return rank;
}

/**
 * Splits nodes into ranked layers, ordering each layer by the mean position of
 * its predecessors in the layer above (the barycentre heuristic), which is what
 * stops parallel branches from crossing over each other.
 *
 * `outerRank` lets the caller supply ranks computed over the WHOLE graph. That
 * matters when these nodes are only part of a spec: ranking them against each
 * other alone would collapse nodes that sit at genuinely different depths into
 * one layer, because the path connecting them runs through nodes that were
 * filtered out.
 */
function layerNodes(
  nodes: GraphNode[],
  edges: GraphSpec['edges'],
  outerRank?: Map<string, number>,
): GraphNode[][] {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const rank = outerRank ?? rankNodes(nodes, edges);

  const preds = new Map<string, string[]>();
  for (const n of nodes) preds.set(n.id, []);
  for (const e of edges ?? []) {
    if (!byId.has(e.from) || !byId.has(e.to)) continue;
    if (rank.get(e.from)! < rank.get(e.to)!) preds.get(e.to)!.push(e.from);
  }

  // Ranks may be sparse when they came from outside, so index by sorted
  // distinct value rather than by the rank number itself.
  const levels = [...new Set(nodes.map((n) => rank.get(n.id) ?? 0))].sort((a, b) => a - b);
  const indexOf = new Map(levels.map((v, i) => [v, i]));
  const layers: GraphNode[][] = levels.map(() => []);
  for (const n of nodes) layers[indexOf.get(rank.get(n.id) ?? 0)!].push(n);

  const slot = new Map<string, number>();
  layers[0]?.forEach((n, i) => slot.set(n.id, i));
  for (let li = 1; li < layers.length; li += 1) {
    const scored = layers[li].map((n, i) => {
      const ps = (preds.get(n.id) ?? []).filter((p) => slot.has(p));
      const bary = ps.length === 0 ? i : ps.reduce((a, p) => a + slot.get(p)!, 0) / ps.length;
      return { n, bary, i };
    });
    scored.sort((a, b) => a.bary - b.bary || a.i - b.i);
    layers[li] = scored.map((s) => s.n);
    layers[li].forEach((n, i) => slot.set(n.id, i));
  }

  return layers;
}

/** Minimum number of consecutive one-node layers worth folding into a snake.
 *
 * Two. A pair of ranks stacked vertically costs a whole extra band plus its
 * gutter -- roughly 250px of height -- to say something a single left-to-right
 * arrow says in 56px of width that was empty anyway. Holding this at four is
 * what left landing-product-feature seven bands tall and half its width unused:
 * the only run long enough to qualify never appeared, because the branch in the
 * middle chopped the flow into runs of three and two.
 */
const SNAKE_MIN = 2;

/**
 * Column count for a snaked run.
 *
 * The fold is chosen for the SHAPE it produces, not by a fixed table. Each
 * candidate width is scored on the aspect ratio of the block it would make,
 * against a mildly landscape target -- a page reads better a little wider than
 * tall, and a docs image that is 4:1 is as awkward to place as one that is 1:4.
 * A fixed table is what turned every pure chain into a single 5-card ribbon.
 *
 * `budget` is the width, in cards, that the rest of the diagram already spends
 * -- the widest authored group or multi-node layer. It caps the choice, so a
 * fold fills width the page has already committed to but never pushes the
 * canvas wider than it already is. Without one (a spec that is nothing but
 * chain) the shape target alone decides.
 */
const SNAKE_ASPECT = 1.5;

function snakeColsFor(n: number, budget?: number): number {
  // Width the page is already committed to. Spending it costs nothing -- the
  // canvas is that wide whatever this run does -- so fill it and stop.
  if (budget !== undefined && budget > 0) return Math.min(n, budget);

  let best = 1;
  let bestScore = Infinity;
  for (let cols = 1; cols <= Math.min(n, 6); cols += 1) {
    const rows = Math.ceil(n / cols);
    const w = cols * CARD_WIDTH + (cols - 1) * COL_GAP;
    const h = rows * CARD_H_2 + (rows - 1) * ROW_GAP;
    // Compare in log space so "twice too wide" and "twice too tall" cost the
    // same; a plain difference would always favour the wider option.
    const score = Math.abs(Math.log(w / h / SNAKE_ASPECT));
    if (score < bestScore) {
      bestScore = score;
      best = cols;
    }
  }
  return best;
}

/**
 * Lays a chain out boustrophedon ("as the ox ploughs"): left to right across a
 * row, down, then right to left across the next. Consecutive steps stay
 * adjacent, so most links become the short straight side-to-side connections
 * the router already draws, and the row turns are single short drops. The same
 * sequence reads in a quarter of the height with no loss of order.
 */
function serpentine(chain: GraphNode[], cols: number): GraphNode[] {
  const out: GraphNode[] = [];
  for (let r = 0; r * cols < chain.length; r += 1) {
    const row = chain.slice(r * cols, (r + 1) * cols);
    out.push(...(r % 2 === 1 ? row.reverse() : row));
  }
  return out;
}

function layout(spec: GraphSpec): {
  nodes: Node[];
  edges: Edge[];
  canvasWidth: number;
  canvasHeight: number;
} {
  const groups = spec.groups ?? [];

  const byGroup = new Map<string, GraphNode[]>();
  const ungrouped: GraphNode[] = [];
  for (const n of spec.nodes) {
    if (n.group) {
      if (!byGroup.has(n.group)) byGroup.set(n.group, []);
      byGroup.get(n.group)!.push(n);
    } else {
      ungrouped.push(n);
    }
  }

  // ---------------------------------------------------------------------
  // Section assembly.
  //
  // One diagram can need more than one layout. A spec may open with a long
  // linear prologue, branch into a decision flow, and end in a set of
  // architectural groupings -- and each of those wants a different treatment.
  // So instead of choosing ONE layout per spec, the graph is cut into sections
  // and each section picks the layout that suits its own shape:
  //
  //   authored group  -> balanced grid   (the author stated the grouping)
  //   long linear run -> serpentine      (a sequence, not a flow)
  //   everything else -> ranked layers   (a branching flow)
  //
  // The sections are then ordered by where their members actually sit in the
  // graph, not by declaration order. That ordering is what keeps a section's
  // edges pointing downward; appending ungrouped nodes at the end (as this
  // used to) is what made auth-security-fig1 a tangle -- two gate nodes that
  // belong in the middle of the API flow were parked below every group they
  // feed, so five edges had to double back up the page.
  // ---------------------------------------------------------------------
  const rank = rankNodes(spec.nodes, spec.edges);
  const meanRank = (ns: GraphNode[]) =>
    ns.reduce((a, n) => a + (rank.get(n.id) ?? 0), 0) / Math.max(ns.length, 1);

  const bands: Band[] = [];
  for (const g of groups) {
    const members = byGroup.get(g.id) ?? [];
    if (members.length === 0) continue;
    bands.push({
      id: g.id,
      label: g.label,
      tier: g.tier ?? 1,
      nodes: members,
      order: meanRank(members),
      x: 0,
      y: 0,
      w: 0,
      h: 0,
    });
  }

  if (ungrouped.length > 0) {
    // When the spec also has authored groups, these nodes are interleaved
    // with them, so they must be ranked against the WHOLE graph. Ranking
    // them only against each other would collapse nodes that sit at
    // different depths into one band -- which is exactly what parked
    // auth-security-fig1's two gate nodes together at the bottom of the page
    // when they belong at two different points inside the API flow.
    const layers =
      bands.length > 0
        ? layerNodes(ungrouped, spec.edges, rank)
        : layerNodes(ungrouped, spec.edges);

    const linked = new Set((spec.edges ?? []).map((e) => `${e.from}->${e.to}`));
    const follows = (a: GraphNode, b: GraphNode) => linked.has(`${a.id}->${b.id}`);

    // Width, in cards, that the diagram is already committed to spending: the
    // widest authored group, or the widest branch in the derived flow. Chains
    // are folded to match it, so a run fills the width the page already has
    // instead of adding height to a page that is mostly empty margin. Left
    // undefined when nothing else establishes a width -- a spec that is pure
    // chain has no committed width, so the snake picks its own square shape.
    const otherCols = [
      ...bands.map((b) => columnsFor(b.nodes.length)),
      ...layers.filter((l) => l.length > 1).map((l) => Math.min(l.length, 6)),
    ];
    const widthBudget = otherCols.length > 0 ? Math.max(...otherCols) : undefined;

    // Fold runs of one-node layers into a serpentine.
    //
    // Ranking is correct but it is not a layout: a stretch of the flow where
    // nothing branches produces one card per rank, and the page then spends its
    // whole height on a single column while both margins stay empty. That is
    // the shape every long flowchart here degenerated into, and it is why
    // detecting whole-graph "pure chains" was not enough -- almost no real
    // diagram is a pure chain, but nearly all of them contain long unbranched
    // stretches between their decisions.
    //
    // A run is only folded when each step is joined to the next by a real
    // forward edge. That link is what proves the ranks are genuinely
    // sequential, and it is also what stops two unrelated single-node layers
    // (or nodes separated by an authored group) from being pulled into the
    // same block.
    let i = 0;
    while (i < layers.length) {
      if (layers[i].length === 0) {
        i += 1;
        continue;
      }

      let j = i;
      while (
        layers[j].length === 1 &&
        j + 1 < layers.length &&
        layers[j + 1].length === 1 &&
        follows(layers[j][0], layers[j + 1][0])
      ) {
        j += 1;
      }

      const span = j - i + 1;
      if (span >= SNAKE_MIN) {
        const run = layers.slice(i, j + 1).map((l) => l[0]);
        const cols = snakeColsFor(span, widthBudget);
        bands.push({
          id: null,
          label: null,
          tier: 1,
          nodes: serpentine(run, cols),
          snakeCols: cols,
          order: meanRank(run),
          x: 0,
          y: 0,
          w: 0,
          h: 0,
        });
        i = j + 1;
        continue;
      }

      bands.push({
        id: null,
        label: null,
        tier: 1,
        nodes: layers[i],
        layer: true,
        order: meanRank(layers[i]),
        x: 0,
        y: 0,
        w: 0,
        h: 0,
      });
      i += 1;
    }
  }

  // Stable sort by structural position. Ties keep declaration order, so an
  // author's sequencing still decides between two sections at the same depth.
  bands.forEach((b, i) => {
    b.seq = i;
  });
  bands.sort((a, b) => (a.order ?? 0) - (b.order ?? 0) || (a.seq ?? 0) - (b.seq ?? 0));

  const placed: Placed[] = [];

  // Grid shape (columns/rows) has to be known before edges are classified,
  // because in-band edges are counted per row gutter and those counts are what
  // decide how tall each row gutter must be.
  const grids = bands.map((band) => {
    // A derived layer is one rank of the flow, so it stays on one row: that
    // adjacency is the whole point of ranking it. Authored groups keep the
    // balanced grid, which is what an architecture band wants.
    const cols =
      band.snakeCols ??
      (band.layer ? Math.min(band.nodes.length, 6) : columnsFor(band.nodes.length));
    const rows = Math.ceil(band.nodes.length / cols);
    const rowHeights: number[] = [];
    for (let r = 0; r < rows; r += 1) {
      const slice = band.nodes.slice(r * cols, (r + 1) * cols);
      rowHeights.push(Math.max(...slice.map(cardHeight)));
    }
    return { cols, rows, rowHeights };
  });

  // --- Pre-classify edges to size every gutter to its actual traffic. ---
  const bandOf = new Map<string, number>();
  const rowOf = new Map<string, number>();
  const colOf = new Map<string, number>();
  bands.forEach((band, bi) => {
    band.nodes.forEach((n, i) => {
      bandOf.set(n.id, bi);
      rowOf.set(n.id, Math.floor(i / grids[bi].cols));
      colOf.set(n.id, i % grids[bi].cols);
    });
  });

  // Width reserved beside the content for edges that skip a band. Sized to the
  // number of edges that will actually use it, not a fixed slab: a diagram
  // with one long span used to reserve the same 230px as one with ten, which
  // pushed that single edge far out to the side and left a large empty
  // rectangle inside the detour.
  const longSpans = (spec.edges ?? []).filter((e) => {
    const sb = bandOf.get(e.from);
    const tb = bandOf.get(e.to);
    return sb !== undefined && tb !== undefined && Math.abs(tb - sb) > 1;
  }).length;
  const SIDE_CHANNEL = longSpans === 0 ? 40 : Math.min(230, 52 + longSpans * LANE_STEP);

  const gutterLanes = new Array(Math.max(bands.length - 1, 0)).fill(0);
  // Records only how tall a gutter's tallest label is. How many lanes it needs
  // is settled later, by packing the runs against their horizontal extent.
  const noteGutter = (i: number, labelH: number) => {
    if (i < 0 || i >= gutterLanes.length) return;
    gutterLabelH[i] = Math.max(gutterLabelH[i], labelH);
  };
  // In-band traffic, keyed by the row gutter it will travel through. A same-row
  // edge dips into the gap below its row, and a cross-row edge crosses that
  // same gap, so both share one counter -- otherwise the two families would be
  // laid out independently and land on top of each other.
  const rowLanes = new Map<string, number>();
  const rowKey = (bi: number, r: number) => `${bi}-${r}`;
  const laneCountRow = (bi: number, r: number) => rowLanes.get(rowKey(bi, r)) ?? 0;

  // Two cards side by side in the same row have a clear, card-free gap between
  // them, so the honest drawing is a straight line from one card's side to the
  // other's -- not a detour down into the row gutter and back up, which is
  // what a bottom-to-bottom route produces. Only the FIRST edge to claim a
  // given side of a card can be lateral; anything further would have to leave
  // from a different height and would stop being a straight line, so those
  // fall back to the gutter route.
  const lateral = new Set<number>();
  const lateralSideUsed = new Set<string>();
  (spec.edges ?? []).forEach((e, idx) => {
    const sb = bandOf.get(e.from);
    const tb = bandOf.get(e.to);
    if (sb === undefined || sb !== tb) return;
    if (rowOf.get(e.from) !== rowOf.get(e.to)) return;
    const sc = colOf.get(e.from)!;
    const tc = colOf.get(e.to)!;
    if (Math.abs(sc - tc) !== 1) return;
    const sSide = tc > sc ? 'r' : 'l';
    const tSide = tc > sc ? 'l' : 'r';
    const sKey = `${e.from}::${sSide}`;
    const tKey = `${e.to}::${tSide}`;
    if (lateralSideUsed.has(sKey) || lateralSideUsed.has(tKey)) return;
    lateralSideUsed.add(sKey);
    lateralSideUsed.add(tKey);
    lateral.add(idx);
  });

  // Measure every label now, because the space a label needs is what sizes the
  // column gap it sits in and the gutter lane it rides on.
  const labelGeom = new Map<number, LabelGeom>();
  (spec.edges ?? []).forEach((e, idx) => {
    if (!e.label) return;
    labelGeom.set(
      idx,
      measureLines(lateral.has(idx) ? wrapLabel(e.label, LATERAL_LABEL_MAX_W) : [e.label]),
    );
  });

  // A lateral edge's entire run is the gap between two cards, so the gap has
  // to be at least as wide as the label that must sit centred on it.
  const bandColGap = bands.map((_, bi) => {
    let widest = 0;
    (spec.edges ?? []).forEach((e, idx) => {
      if (!lateral.has(idx) || bandOf.get(e.from) !== bi) return;
      widest = Math.max(widest, labelGeom.get(idx)?.w ?? 0);
    });
    return Math.max(COL_GAP, widest + 28);
  });

  // Tallest label riding each gutter, so a wrapped multi-line label gets a
  // lane tall enough to hold it without touching its neighbours.
  const gutterLabelH = new Array(Math.max(bands.length - 1, 0)).fill(0);
  const rowLabelH = new Map<string, number>();
  const stepGutter = (i: number) => Math.max(LANE_STEP, (gutterLabelH[i] ?? 0) + 10);
  const stepRow = (bi: number, r: number) =>
    Math.max(LANE_STEP, (rowLabelH.get(rowKey(bi, r)) ?? 0) + 10);

  (spec.edges ?? []).forEach((e, idx) => {
    const sb = bandOf.get(e.from);
    const tb = bandOf.get(e.to);
    if (sb === undefined || tb === undefined) return;
    const lh = labelGeom.get(idx)?.h ?? 0;
    if (sb === tb) {
      // Lateral edges never enter a gutter, so they must not reserve a lane.
      if (lateral.has(idx)) return;
      const k = rowKey(sb, Math.min(rowOf.get(e.from)!, rowOf.get(e.to)!));
      rowLanes.set(k, (rowLanes.get(k) ?? 0) + 1);
      rowLabelH.set(k, Math.max(rowLabelH.get(k) ?? 0, lh));
      return;
    }
    if (Math.abs(tb - sb) === 1) {
      // Adjacent hop: one run, in the gutter between the two bands.
      noteGutter(Math.min(sb, tb), lh);
    } else {
      // Long span: one run leaving the source, one entering the target.
      noteGutter(tb > sb ? sb : sb - 1, lh);
      noteGutter(tb > sb ? tb - 1 : tb, lh);
    }
  });

  // Row gutters and the band's bottom padding grow with the number of in-band
  // runs they have to carry, exactly like the inter-band gutters do.
  const inners = grids.map((g, bi) => {
    const colGap = bandColGap[bi];
    const rowGaps: number[] = [];
    for (let r = 0; r < g.rows - 1; r += 1) {
      rowGaps.push(Math.max(ROW_GAP, laneCountRow(bi, r) * stepRow(bi, r) + GAP_PAD));
    }
    const height = g.rowHeights.reduce((a, b) => a + b, 0) + rowGaps.reduce((a, b) => a + b, 0);
    const padBottom = bands[bi].id
      ? Math.max(
          GROUP_PAD_BOTTOM,
          laneCountRow(bi, g.rows - 1) * stepRow(bi, g.rows - 1) + GAP_PAD,
        )
      : 0;
    return {
      ...g,
      colGap,
      width: g.cols * CARD_WIDTH + (g.cols - 1) * colGap,
      rowGaps,
      height,
      padBottom,
    };
  });

  const contentWidth = Math.max(...inners.map((b) => b.width));

  // ---------------------------------------------------------------------
  // Horizontal geometry, resolved before the vertical stack.
  //
  // Where a card sits across the page depends only on its band's grid and the
  // common content width -- never on how tall any gutter turns out to be. So
  // x can be settled first, and that is what lets the gutters below be sized
  // from the actual horizontal extent of the runs they carry.
  // ---------------------------------------------------------------------
  const bandInnerX = inners.map((inner) => CANVAS_MARGIN + SIDE_CHANNEL + (contentWidth - inner.width) / 2);

  const rowOriginX = (bi: number, r: number) => {
    const inner = inners[bi];
    const len = bands[bi].nodes.slice(r * inner.cols, (r + 1) * inner.cols).length;
    return bands[bi].snakeCols !== undefined && r % 2 === 1
      ? bandInnerX[bi] + (inner.cols - len) * (CARD_WIDTH + inner.colGap)
      : bandInnerX[bi];
  };

  const cardSpan = new Map<string, [number, number]>();
  bands.forEach((band, bi) => {
    const inner = inners[bi];
    band.nodes.forEach((n, i) => {
      const x =
        rowOriginX(bi, Math.floor(i / inner.cols)) +
        (i % inner.cols) * (CARD_WIDTH + inner.colGap);
      cardSpan.set(n.id, [x, x + CARD_WIDTH]);
    });
  });

  // ---------------------------------------------------------------------
  // Gutter lane packing.
  //
  // A run in a gutter needs its own y only if it would otherwise sit on top of
  // another run in that same gutter. Handing every edge an unconditional lane
  // is what made auth-security-fig1 half empty gap by height: seven edges
  // crossing one gutter reserved seven 34px lanes even though most of them
  // never pass over the same x as each other.
  //
  // Runs are coloured greedily in left-edge order, which is exact for interval
  // graphs, so a gutter ends up exactly as tall as the deepest genuine pile-up
  // rather than as tall as its total traffic. Extents are taken as the whole
  // card, not the port, so the packing stays conservative once ports fan out.
  // ---------------------------------------------------------------------
  const LANE_CLEAR = 26;
  const channelLeft = CANVAS_MARGIN;
  const channelRight = CANVAS_MARGIN + SIDE_CHANNEL * 2 + contentWidth;
  const midX = CANVAS_MARGIN + SIDE_CHANNEL + contentWidth / 2;

  const gutterRuns: { lo: number; hi: number; idx: number }[][] = gutterLanes.map(() => []);
  const addRun = (g: number, idx: number, a: number, b: number) => {
    if (g < 0 || g >= gutterRuns.length) return;
    gutterRuns[g].push({ lo: Math.min(a, b), hi: Math.max(a, b), idx });
  };

  (spec.edges ?? []).forEach((e, idx) => {
    const sb = bandOf.get(e.from);
    const tb = bandOf.get(e.to);
    if (sb === undefined || tb === undefined || sb === tb) return;
    const ss = cardSpan.get(e.from)!;
    const ts = cardSpan.get(e.to)!;
    if (Math.abs(tb - sb) === 1) {
      addRun(Math.min(sb, tb), idx, Math.min(ss[0], ts[0]), Math.max(ss[1], ts[1]));
      return;
    }
    // A long span leaves to a side channel and comes back, so it lays down one
    // run in the gutter it exits and another in the gutter it enters. Which
    // side it takes is decided by the same midpoint test the router uses.
    const side = (ss[0] + ss[1] + ts[0] + ts[1]) / 4 >= midX ? channelRight : channelLeft;
    addRun(tb > sb ? sb : sb - 1, idx, Math.min(ss[0], side), Math.max(ss[1], side));
    addRun(tb > sb ? tb - 1 : tb, idx, Math.min(ts[0], side), Math.max(ts[1], side));
  });

  const gutterLane = new Map<string, number>();
  gutterRuns.forEach((runs, g) => {
    runs.sort((a, b) => a.lo - b.lo || a.hi - b.hi);
    const laneEnd: number[] = [];
    for (const run of runs) {
      let lane = laneEnd.findIndex((end) => end + LANE_CLEAR <= run.lo);
      if (lane === -1) {
        lane = laneEnd.length;
        laneEnd.push(0);
      }
      laneEnd[lane] = run.hi;
      gutterLane.set(`${run.idx}-${g}`, lane);
    }
    gutterLanes[g] = laneEnd.length;
  });

  // A gutter is exactly as tall as the traffic it carries plus a little
  // clearance -- previously a floor of 130 was ADDED to the lane height, so
  // even a gutter holding one plain arrow was 160px+ tall.
  const gapFor = (i: number) =>
    Math.max(BAND_GAP_MIN, (gutterLanes[i] ?? 0) * stepGutter(i) + GAP_PAD);

  // Vertical span available to in-band runs, per row gutter.
  const rowGutter = new Map<string, { top: number; bottom: number }>();

  let cursorY = CANVAS_MARGIN;
  bands.forEach((band, bi) => {
    const inner = inners[bi];
    const innerX = bandInnerX[bi];
    const innerY = cursorY + (band.id ? GROUP_PAD_TOP : 0);

    band.x = innerX - GROUP_PAD_SIDE;
    band.y = cursorY;
    band.w = inner.width + GROUP_PAD_SIDE * 2;
    band.h = (band.id ? GROUP_PAD_TOP : 0) + inner.height + inner.padBottom;

    let rowY = innerY;
    inner.rowHeights.forEach((rh, r) => {
      const slice = band.nodes.slice(r * inner.cols, (r + 1) * inner.cols);
      // Partial rows stay on the column grid instead of being centred under a
      // wider row. Centring shifts every card in the short row by half a
      // column, which turns what should be straight vertical drops between
      // rows into staggered jogs -- that is the whole reason the execution
      // plane (4 + 3 cards) read as messier than the control plane (3 + 3).
      //
      // A snake's odd rows are the exception: they run right to left, so their
      // first card is the RIGHTmost slot. Left-aligning a short one puts it
      // under the wrong column and the turn from the row above has to travel
      // the whole band to reach it.
      const rowX = rowOriginX(bi, r);
      slice.forEach((n, c) => {
        placed.push({
          node: n,
          x: rowX + c * (CARD_WIDTH + inner.colGap),
          y: rowY + (rh - cardHeight(n)) / 2,
          w: CARD_WIDTH,
          h: cardHeight(n),
          band: bi,
          row: r,
          rowTop: rowY,
          rowBottom: rowY + rh,
        });
      });
      const gap = inner.rowGaps[r];
      rowGutter.set(rowKey(bi, r), {
        top: rowY + rh,
        bottom: gap === undefined ? band.y + band.h : rowY + rh + gap,
      });
      rowY += rh + (gap ?? 0);
    });

    cursorY = band.y + band.h + gapFor(bi);
  });

  const canvasWidth = contentWidth + CANVAS_MARGIN * 2 + SIDE_CHANNEL * 2;
  const canvasHeight = cursorY - gapFor(bands.length - 1) + CANVAS_MARGIN;

  const posById = new Map<string, Placed>();
  for (const p of placed) posById.set(p.node.id, p);

  // ---------------------------------------------------------------------
  // Orthogonal router.
  //
  // Two rules keep the picture readable:
  //   1. No two edges share a horizontal run. Every edge gets its own lane
  //      (a distinct y) inside whichever gutter it travels through.
  //   2. No two edges share a vertical run. Each node fans its connections
  //      out across the card's edge, so lines only ever touch at the port
  //      itself, never along a shared column.
  // Perpendicular crossings are fine and unavoidable; collinear overlaps are
  // what make a diagram impossible to follow, and those are eliminated.
  // ---------------------------------------------------------------------

  type Kind = 'same' | 'lateral' | 'down' | 'up' | 'sideDown' | 'sideUp';

  interface Routed {
    idx: number;
    e: (typeof spec.edges)[number];
    s: Placed;
    t: Placed;
    kind: Kind;
    sSide: 'top' | 'bottom';
    tSide: 'top' | 'bottom';
    sx: number;
    tx: number;
  }

  const routed: Routed[] = [];
  (spec.edges ?? []).forEach((e, idx) => {
    const s = posById.get(e.from);
    const t = posById.get(e.to);
    if (!s || !t) return;

    let kind: Kind;
    if (s.band === t.band) kind = lateral.has(idx) ? 'lateral' : 'same';
    else if (Math.abs(t.band - s.band) > 1) kind = t.band > s.band ? 'sideDown' : 'sideUp';
    else kind = t.band > s.band ? 'down' : 'up';

    const sSide: 'top' | 'bottom' = kind === 'up' || kind === 'sideUp' ? 'top' : 'bottom';
    const tSide: 'top' | 'bottom' =
      kind === 'same' || kind === 'lateral'
        ? 'bottom'
        : kind === 'up' || kind === 'sideUp'
          ? 'bottom'
          : 'top';

    routed.push({ idx, e, s, t, kind, sSide, tSide, sx: 0, tx: 0 });
  });

  // --- Port fan-out: spread each node's connections across its card edge. ---
  interface PortReq {
    r: Routed;
    isSource: boolean;
    toward: number;
  }
  const ports = new Map<string, PortReq[]>();
  const portKey = (nodeId: string, side: string) => `${nodeId}::${side}`;

  for (const r of routed) {
    // Lateral edges attach to the card's left/right side at the row's centre
    // line, not to the top/bottom edge, so they take no slot in the horizontal
    // fan-out. Leaving them in would push the vertical connectors off-centre
    // to make room for a port that is never used.
    if (r.kind === 'lateral') continue;
    const sKey = portKey(r.s.node.id, r.sSide);
    const tKey = portKey(r.t.node.id, r.tSide);
    if (!ports.has(sKey)) ports.set(sKey, []);
    if (!ports.has(tKey)) ports.set(tKey, []);
    ports.get(sKey)!.push({ r, isSource: true, toward: r.t.x + r.t.w / 2 });
    ports.get(tKey)!.push({ r, isSource: false, toward: r.s.x + r.s.w / 2 });
  }

  for (const [key, reqs] of ports) {
    const nodeId = key.split('::')[0];
    const placedNode = posById.get(nodeId)!;
    const outs = reqs.filter((q) => q.isSource);
    const ins = reqs.filter((q) => !q.isSource);

    // Trunk style. Several edges leaving one side of a card are one decision
    // fanning out, and several arriving are one confluence -- so each group
    // shares a single port and leaves/enters as one trunk that splits or
    // merges out in the gutter. Fanning them across the card edge instead
    // produces a row of near-parallel stubs that reads as separate,
    // independent connections and makes the card look like a pin header.
    // This is the deliberate exception to the no-shared-vertical rule: the
    // overlap at a trunk is exactly what carries the meaning.
    //
    // When a side has both trunks they need distinct ports, otherwise the
    // outbound and inbound trunks would sit on the same column and an
    // arrowhead would land on top of a departing line.
    const both = outs.length > 0 && ins.length > 0;
    const trunkX = (which: 'out' | 'in') => {
      if (!both) return placedNode.x + placedNode.w / 2;
      const frac = which === 'in' ? 0.32 : 0.68;
      return placedNode.x + placedNode.w * frac;
    };

    const fanOut = (group: PortReq[], isSource: boolean) => {
      // Order by where the other end sits, so lines leave in the same
      // left-to-right order they arrive -- that alone removes most crossings.
      group.sort((a, b) => a.toward - b.toward);
      const usable = placedNode.w - 36;
      group.forEach((req, i) => {
        const frac = group.length === 1 ? 0.5 : (i + 1) / (group.length + 1);
        const x = placedNode.x + 18 + usable * frac;
        if (isSource) req.r.sx = x;
        else req.r.tx = x;
      });
    };

    if (outs.length > 1) {
      const x = trunkX('out');
      for (const req of outs) req.r.sx = x;
    } else {
      fanOut(outs, true);
    }

    if (ins.length > 1) {
      const x = trunkX('in');
      for (const req of ins) req.r.tx = x;
    } else {
      fanOut(ins, false);
    }
  }

  // --- Lane allocation: one distinct horizontal run per edge, per gutter. ---
  const laneUse = new Map<string, number>();
  const takeLane = (key: string) => {
    const lane = laneUse.get(key) ?? 0;
    laneUse.set(key, lane + 1);
    return lane;
  };

  // One counter per PHYSICAL gutter, shared by adjacent hops and side-channel
  // runs alike. The lane index comes from the packing pass, so two runs that
  // never pass over the same x share a y instead of each claiming their own.
  // Lanes are spread evenly across the gutter's measured span: with n lanes,
  // lane k sits at (k+1)/(n+1) of the way down, which keeps clearance at both
  // bands.
  const laneY = (gutter: number, idx: number) => {
    const top = bands[gutter].y + bands[gutter].h;
    const bottom = bands[gutter + 1].y;
    const span = bottom - top;
    const total = Math.max(gutterLanes[gutter] ?? 1, 1);
    const lane = gutterLane.get(`${idx}-${gutter}`) ?? 0;
    return top + (span * (Math.min(lane, total - 1) + 1)) / (total + 1);
  };

  const rfEdges: Edge[] = [];
  interface LabelBox {
    edgeId: string;
    x: number;
    ideal: number;
    y: number;
    w: number;
    h: number;
    xmin: number;
    xmax: number;
  }
  const labelBoxes: LabelBox[] = [];
  // Every vertical run in the picture. A label centred on its own run must not
  // also sit on a connector crossing that run, which would read as a junction
  // label; placement below steers around them.
  const verticals: { edgeId: string; x: number; y0: number; y1: number }[] = [];

  for (const r of routed) {
    const { s, t, sx, tx, kind } = r;
    const sy = r.sSide === 'bottom' ? s.y + s.h : s.y;
    const ty = r.tSide === 'top' ? t.y : t.y + t.h;

    let points: Point[];
    let runY: number;

    if (kind === 'lateral') {
      // Straight side-to-side line across the column gap. Both cards are
      // vertically centred in the row, so the row's centre line hits both card
      // sides at the same height and the segment is exactly horizontal.
      const goingRight = t.x > s.x;
      runY = (s.rowTop + s.rowBottom) / 2;
      points = [
        { x: goingRight ? s.x + s.w : s.x, y: runY },
        { x: goingRight ? t.x : t.x + t.w, y: runY },
      ];
    } else if (kind === 'same') {
      // Route inside the band, through the gutter below the upper of the two
      // rows. Same-row and cross-row edges share that gutter and therefore
      // share one lane counter, and lanes are spread across the gutter's
      // measured span the same way inter-band lanes are -- previously these
      // runs were stacked from the top of the gap with a fixed step and then
      // clamped to the band floor, which is why several of them collapsed onto
      // the same y while the outer gutters looked evenly distributed.
      const gi = Math.min(s.row, t.row);
      const span = rowGutter.get(`${s.band}-${gi}`)!;
      const total = Math.max(laneCountRow(s.band, gi), 1);
      const lane = takeLane(`same-${s.band}-${gi}`);
      const top = span.top + 14;
      const bottom = Math.max(span.bottom - 14, top + 1);
      runY = top + ((bottom - top) * (Math.min(lane, total - 1) + 1)) / (total + 1);

      if (s.row === t.row) {
        points = [
          { x: sx, y: s.y + s.h },
          { x: sx, y: runY },
          { x: tx, y: runY },
          { x: tx, y: t.y + t.h },
        ];
      } else {
        const goingDown = t.rowTop > s.rowTop;
        points = [
          { x: sx, y: goingDown ? s.y + s.h : s.y },
          { x: sx, y: runY },
          { x: tx, y: runY },
          { x: tx, y: goingDown ? t.y : t.y + t.h },
        ];
      }
    } else if (kind === 'down' || kind === 'up') {
      // Adjacent bands: one horizontal run, spread across the gutter between
      // them rather than pinned near the upper band.
      const gutter = Math.min(s.band, t.band);
      runY = laneY(gutter, r.idx);
      points = [
        { x: sx, y: sy },
        { x: sx, y: runY },
        { x: tx, y: runY },
        { x: tx, y: ty },
      ];
    } else {
      // Spans an intermediate band: run out to a side channel so the line
      // never crosses a band it has nothing to do with. The exit run and the
      // entry run each live in their own gutter and are spread there too.
      const down = kind === 'sideDown';
      const goRight = (sx + tx) / 2 >= CANVAS_MARGIN + SIDE_CHANNEL + contentWidth / 2;
      const chanLane = takeLane(`chan-${goRight ? 'r' : 'l'}`);
      const sideX = goRight
        ? CANVAS_MARGIN + SIDE_CHANNEL + contentWidth + 24 + chanLane * LANE_STEP
        : CANVAS_MARGIN + SIDE_CHANNEL - 24 - chanLane * LANE_STEP;

      const exitGutter = down ? s.band : s.band - 1;
      const enterGutter = down ? t.band - 1 : t.band;
      runY = laneY(exitGutter, r.idx);
      const enterY = laneY(enterGutter, r.idx);

      points = [
        { x: sx, y: sy },
        { x: sx, y: runY },
        { x: sideX, y: runY },
        { x: sideX, y: enterY },
        { x: tx, y: enterY },
        { x: tx, y: ty },
      ];
    }

    // Anchor the label on the edge's own horizontal run, always centred on the
    // line. The run's extent is recorded so the collision pass can slide the
    // label along its own line -- the only degree of freedom it gets, since
    // moving it off the run would break that centring.
    const runStartX = kind === 'lateral' ? points[0].x : sx;
    const runEndX =
      kind === 'lateral' ? points[1].x : kind === 'sideDown' || kind === 'sideUp' ? points[2].x : tx;
    const labelX = (runStartX + runEndX) / 2;
    const geom = labelGeom.get(r.idx);
    const halfLabel = (geom?.w ?? 0) / 2 + 12;
    const labelPos = {
      x: Math.min(Math.max(labelX, halfLabel), canvasWidth - halfLabel),
      y: runY,
    };

    const edgeId = `e${r.idx}`;
    for (let i = 0; i < points.length - 1; i += 1) {
      const a = points[i];
      const b = points[i + 1];
      if (Math.abs(a.x - b.x) < 0.5 && Math.abs(a.y - b.y) > 1) {
        verticals.push({
          edgeId,
          x: a.x,
          y0: Math.min(a.y, b.y),
          y1: Math.max(a.y, b.y),
        });
      }
    }

    if (geom) {
      labelBoxes.push({
        edgeId,
        x: labelPos.x,
        ideal: labelPos.x,
        y: labelPos.y,
        w: geom.w,
        h: geom.h,
        xmin: Math.min(runStartX, runEndX),
        xmax: Math.max(runStartX, runEndX),
      });
    }

    const stroke = neutral.foreground4;
    rfEdges.push({
      id: `e${r.idx}`,
      source: r.e.from,
      target: r.e.to,
      type: 'routed',
      // Pre-wrapped so the rendered box matches the size layout reserved.
      label: geom?.lines.join('\n'),
      zIndex: 2,
      data: { points, labelPos, labelOffset: { dx: 0, dy: 0 } },
      style: {
        stroke,
        strokeWidth: 1.8,
        strokeDasharray: r.e.dashed ? '6 5' : undefined,
      },
      markerEnd: r.e.undirected
        ? undefined
        : { type: MarkerType.ArrowClosed, color: stroke, width: 16, height: 16 },
    });
  }

  // A label is always drawn centred on its edge's run -- that is what makes it
  // unambiguous which connector it belongs to -- so the only freedom left is
  // sliding it along that run. The space it needs was already reserved during
  // layout (column gaps widen to fit lateral labels, gutter lanes are spaced
  // by the tallest label they carry), so a clear position almost always
  // exists. Each label takes the cheapest x on its own run, penalising
  // positions that land on a crossing connector or on a label already placed.
  const CLEAR_X = 10;
  const settled: LabelBox[] = [];
  const order = [...labelBoxes].sort((a, b) => b.w - a.w);
  for (const box of order) {
    const half = box.w / 2;
    const top = box.y - box.h / 2;
    const bottom = box.y + box.h / 2;

    let lo = box.xmin + half;
    let hi = box.xmax - half;
    if (hi - lo < 40) {
      const mid = (box.xmin + box.xmax) / 2;
      lo = mid - 60;
      hi = mid + 60;
    }
    lo = Math.max(lo, half + 4);
    hi = Math.min(hi, canvasWidth - half - 4);
    if (hi < lo) hi = lo;

    const candidates: number[] = [];
    for (let x = lo; x <= hi; x += 3) candidates.push(x);
    candidates.push(hi);
    if (box.ideal >= lo && box.ideal <= hi) candidates.push(box.ideal);

    let bestX = box.ideal;
    let bestCost = Infinity;
    for (const x of candidates) {
      const left = x - half - CLEAR_X;
      const right = x + half + CLEAR_X;

      let crossings = 0;
      for (const v of verticals) {
        if (v.edgeId === box.edgeId) continue;
        if (v.x < left || v.x > right) continue;
        if (v.y1 < top || v.y0 > bottom) continue;
        crossings += 1;
      }
      let collisions = 0;
      for (const p of settled) {
        if (Math.abs(p.y - box.y) >= (p.h + box.h) / 2 + 4) continue;
        if (Math.abs(p.x - x) >= (p.w + box.w) / 2 + 10) continue;
        collisions += 1;
      }
      const cost = collisions * 4000 + crossings * 1200 + Math.abs(x - box.ideal) * 0.35;
      if (cost < bestCost) {
        bestCost = cost;
        bestX = x;
      }
    }
    box.x = bestX;
    settled.push(box);
  }
  const resolvedLabel = new Map(labelBoxes.map((l) => [l.edgeId, l]));
  for (const edge of rfEdges) {
    const fixed = resolvedLabel.get(edge.id);
    if (!fixed) continue;
    const data = edge.data as { labelPos?: { x: number; y: number } };
    if (data?.labelPos) data.labelPos = { x: fixed.x, y: fixed.y };
  }

  const rfNodes: Node[] = [];
  bands.forEach((band) => {
    if (!band.id) return;
    rfNodes.push({
      id: `group-${band.id}`,
      type: 'band',
      position: { x: band.x, y: band.y },
      data: { label: band.label, tier: band.tier },
      style: { width: band.w, height: band.h },
      draggable: false,
      selectable: false,
      zIndex: 0,
    });
  });
  bands.forEach((band) => {
    if (!band.id || !band.label) return;
    // Separate node so the title can outrank the edge layer (see nodes.tsx).
    rfNodes.push({
      id: `group-label-${band.id}`,
      type: 'bandLabel',
      position: { x: band.x + 40, y: band.y + 30 },
      data: { label: band.label, tier: band.tier },
      draggable: false,
      selectable: false,
      zIndex: 4,
    });
  });
  placed.forEach((p) => {
    rfNodes.push({
      id: p.node.id,
      type: 'card',
      position: { x: p.x, y: p.y },
      data: p.node as unknown as Record<string, unknown>,
      draggable: false,
      selectable: false,
      zIndex: 3,
    });
  });

  return { nodes: rfNodes, edges: rfEdges, canvasWidth, canvasHeight };
}

export interface DiagramCanvasProps {
  spec: GraphSpec;
  onReady?: () => void;
}

export function DiagramCanvas({ spec, onReady }: DiagramCanvasProps) {
  const { nodes, edges, canvasWidth, canvasHeight } = useMemo(() => layout(spec), [spec]);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    const id = requestAnimationFrame(() =>
      requestAnimationFrame(() => {
        setReady(true);
        onReady?.();
      }),
    );
    return () => cancelAnimationFrame(id);
  }, [nodes, edges, onReady]);

  return (
    <div
      id="diagram-root"
      data-diagram-ready={ready ? 'true' : 'false'}
      style={{
        width: canvasWidth,
        height: canvasHeight,
        backgroundColor: neutral.background3,
        border: `1px solid ${neutral.stroke2}`,
        borderRadius: radius.card,
      }}
    >
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        panOnDrag={false}
        zoomOnScroll={false}
        zoomOnPinch={false}
        zoomOnDoubleClick={false}
        preventScrolling={false}
        proOptions={{ hideAttribution: true }}
        defaultViewport={{ x: 0, y: 0, zoom: 1 }}
      >
        <Background gap={28} size={1.4} color={neutral.stroke2} />
      </ReactFlow>
    </div>
  );
}
