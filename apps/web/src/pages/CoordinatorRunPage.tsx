import '@xyflow/react/dist/style.css';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { formatApiError, formatApiErrorMessage } from '../api/errors';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Text,
  Tooltip,
  Tree,
  TreeItem,
  TreeItemLayout,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { Display, EmptyState, TitleText } from '../components/ui';
import { AgentStepList } from '../components/ui/agentic';
import type { AgentStep } from '../components/ui/agentic';
import { AgentAvatar } from '../components/AgentAvatar';
import { AgentSessionPanel } from '../components/AgentSessionPanel';
import { CoordinatorArtifactsPanel } from '../components/CoordinatorArtifactsPanel';
import { AiCredits } from '../components/AiCredits';
import { OutcomePlanPanel } from '../components/OutcomePlanPanel';
import { AgentTokenBreakdown } from '../components/runs/AgentTokenBreakdown';
import { SlidePanel } from '../components/SlidePanel';
import {
  accentClass,
  ActiveEdgeContext,
  BrowseFilesContext,
  coordinatorLoopbackLabel,
  CoordinatorSessionContext,
  ElapsedTimer,
  ExecutionModalContext,
  forwardEdge,
  iconForRole,
  loopbackEdge,
  NodeDetailPopover,
  roleDescForRole,
  useNodeStyles,
  workflowEdgeTypes,
  workflowNodeTypes,
} from '../components/WorkflowGraphPanel';
import { useSeededRunStream } from '../hooks/useSeededRunStream';
import { buildTopologyState, initialTopologyState, seedTopologyFromWorkPlan } from '../state/topologyReducer';
import { formatModelLabel } from '../utils/agentIdentity';
import { layoutDagStaircase, layoutBBox, COMPACT_NODE_H, COMPACT_NODE_W, FIXED_NODE_W, FIXED_NODE_H, FIXED_NODE_WITH_CAPTION_H, REVIEW_EXPANDED_NODE_H } from '../utils/dagLayout';
import {
  ArrowAutofitHeightRegular,
  ArrowAutofitWidthRegular,
  ArrowRepeatAllRegular,
  BotRegular,
  BroomRegular,
  CheckmarkRegular,
  CircleRegular,
  ClockRegular,
  DismissRegular,
  DocumentRegular,
  FlowchartRegular,
  InfoRegular,
  OpenRegular,
  PanelLeftContractRegular,
  PanelLeftExpandRegular,
  ScaleFitRegular,
  ZoomInRegular,
  ZoomOutRegular,
} from '@fluentui/react-icons';
import { Handle, MiniMap, Position, ReactFlow, ReactFlowProvider, useReactFlow, useStore } from '@xyflow/react';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode, RefObject } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import type { FormattedApiError } from '../api/errors';
import type { RunStreamEvent } from '../api/sse';
import type {
  GraphDescriptor,
  PortForwardSessionDto,
  RunAgentTokenBreakdownDto,
  RunStatus,
  WorkPlanResponse,
} from '../api/types';
import type { RunSessionTree } from '../components/AgentSessionPanel';
import type { ExecutorDef, ExecutorState, NodeDetailRow, StepStatus, WorkflowNodeData } from '../components/WorkflowGraphPanel';
import type { ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
import type { CoordinatorTopologyState, TopologyNodeState } from '../state/topologyReducer';
import type { NodeSizeHint } from '../utils/dagLayout';
import { isTerminalRunStatus } from '../utils/runStatus';
import type { Edge, Node, NodeProps } from '@xyflow/react';
// ---------------------------------------------------------------------------
// Subtask-card clicks open the docked agent-session panel instead of navigating away.
const CoordPanelContext = createContext<((nodeId: string, opts?: { closeTopology?: boolean }) => void) | undefined>(undefined);

// ---------------------------------------------------------------------------
// Topology status helpers
// ---------------------------------------------------------------------------

function pendingApprovalsByRun(events: RunStreamEvent[], coordinatorRunId: string): Map<string, number> {
  const pending = new Map<string, string>();
  for (const event of events) {
    const requestId = String(event.payload.requestId ?? event.payload.request_id ?? event.payload.commandHash ?? event.payload.command_hash ?? '');
    if (!requestId) continue;
    const childRunId = String(event.payload.childRunId ?? event.payload.child_run_id ?? '');
    const targetRunId = childRunId || coordinatorRunId;
    const key = `${targetRunId}:${requestId}`;
    if (event.type === 'tool.approval_required' || event.type === 'shell.approval_required' || event.type === 'coordinator.child_approval_required') {
      pending.set(key, targetRunId);
    } else if (event.type === 'tool.approval_resolved' || event.type === 'coordinator.child_approval_resolved') {
      pending.delete(key);
    }
  }
  const counts = new Map<string, number>();
  for (const targetRunId of pending.values()) counts.set(targetRunId, (counts.get(targetRunId) ?? 0) + 1);
  return counts;
}

function topoStatusToStepStatus(status: string): StepStatus {
  switch (status) {
    case 'revising':
    case 'dispatching':
    case 'assembling':
    case 'in_review':
    case 'dispatched':     return 'started';
    case 'running':        return 'started';
    case 'pending_capacity': return 'started';
    case 'awaiting_assembly': return 'started';
    case 'assemble_ready': return 'completed';
    case 'rai_flagged':    return 'revise';
    case 'completed':      return 'completed';
    case 'complete':       return 'completed';
    case 'blocked':
    case 'assembly_blocked':
    case 'assembly_failed':
    case 'assembly_declined':
    case 'needs_resolution':
    case 'rai_blocked':
    case 'failed':         return 'failed';
    default:               return 'pending';
  }
}

function graphNodeSize(node: Node): { width: number; height: number } {
  return {
    width: node.measured?.width ?? node.initialWidth ?? COMPACT_NODE_W,
    height: node.measured?.height ?? node.initialHeight ?? COMPACT_NODE_H,
  };
}

function routeGridEdges(edges: Edge[], nodes: Node[]): Edge[] {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const center = (node: Node) => {
    const size = graphNodeSize(node);
    return {
      x: node.position.x + size.width / 2,
      y: node.position.y + size.height / 2,
    };
  };
  return edges.map((edge) => {
    const source = byId.get(edge.source);
    const target = byId.get(edge.target);
    if (!source || !target) return edge;
    const sourceCenter = center(source);
    const targetCenter = center(target);
    if (edge.type === 'loopback') {
      const rowPeers = (node: Node, nodeCenter: { x: number; y: number }) =>
        nodes
          .filter((peer) => peer.id !== node.id)
          .map((peer) => center(peer))
          .filter((peerCenter) => Math.abs(peerCenter.y - nodeCenter.y) <= 1);
      const rightCrossings = [
        ...rowPeers(source, sourceCenter).filter((peer) => peer.x > sourceCenter.x),
        ...rowPeers(target, targetCenter).filter((peer) => peer.x > targetCenter.x),
      ].length;
      const leftCrossings = [
        ...rowPeers(source, sourceCenter).filter((peer) => peer.x < sourceCenter.x),
        ...rowPeers(target, targetCenter).filter((peer) => peer.x < targetCenter.x),
      ].length;
      const side = leftCrossings < rightCrossings ? 'left' : 'right';
      return {
        ...edge,
        sourceHandle: `source-${side}`,
        targetHandle: `target-${side}`,
        data: { ...(edge.data ?? {}), returnSide: side },
      };
    }
    if (edge.type !== 'spine') return edge;
    // Pick the dominant axis so the connector leaves/enters on the correct side in BOTH the
    // horizontal (LR) and vertical (TB) layouts. Horizontal-dominant → left/right handles;
    // vertical-dominant → top/bottom handles.
    const dx = targetCenter.x - sourceCenter.x;
    const dy = targetCenter.y - sourceCenter.y;

    // A spine edge that skips over a rank (e.g. an upper sibling → a shared fan-in target two rows
    // below) is normally drawn as a straight bottom→top (or right→left) segment. When another,
    // UNRELATED node happens to sit in that straight corridor — as when same-rank siblings are
    // stacked in one column above their common downstream target — the segment is drawn directly
    // through that intermediate card, making a real edge look like a dependency on the occluded
    // node (see GH: misleading Skyler→RAI edge drawn through Hank). Detect that occlusion and route
    // the edge out to a perpendicular side handle so React Flow bows it AROUND the stack instead of
    // through it. Non-occluded edges (the overwhelming majority) keep their original handles.
    const corridorObstacles = (axis: 'vertical' | 'horizontal') => {
      const result: Array<{ cx: number; cy: number }> = [];
      for (const peer of nodes) {
        if (peer.id === edge.source || peer.id === edge.target) continue;
        const size = graphNodeSize(peer);
        const x0 = peer.position.x;
        const x1 = peer.position.x + size.width;
        const y0 = peer.position.y;
        const y1 = peer.position.y + size.height;
        if (axis === 'vertical') {
          const loY = Math.min(sourceCenter.y, targetCenter.y);
          const hiY = Math.max(sourceCenter.y, targetCenter.y);
          const corridorX = (sourceCenter.x + targetCenter.x) / 2;
          const peerCy = (y0 + y1) / 2;
          if (corridorX >= x0 && corridorX <= x1 && peerCy > loY && peerCy < hiY) {
            result.push({ cx: (x0 + x1) / 2, cy: peerCy });
          }
        } else {
          const loX = Math.min(sourceCenter.x, targetCenter.x);
          const hiX = Math.max(sourceCenter.x, targetCenter.x);
          const corridorY = (sourceCenter.y + targetCenter.y) / 2;
          const peerCx = (x0 + x1) / 2;
          if (corridorY >= y0 && corridorY <= y1 && peerCx > loX && peerCx < hiX) {
            result.push({ cx: peerCx, cy: (y0 + y1) / 2 });
          }
        }
      }
      return result;
    };

    if (Math.abs(dx) >= Math.abs(dy)) {
      const forward = dx >= 0;
      // Horizontal-dominant edge blocked by a node in the horizontal corridor → bow vertically.
      const blockers = corridorObstacles('horizontal');
      if (blockers.length > 0) {
        const corridorY = (sourceCenter.y + targetCenter.y) / 2;
        const above = blockers.filter((b) => b.cy < corridorY).length;
        const below = blockers.length - above;
        const side = below <= above ? 'bottom' : 'top';
        return {
          ...edge,
          sourceHandle: `source-${side}`,
          targetHandle: `target-${side}`,
          data: { ...(edge.data ?? {}), flowDirection: 'horizontal', reroutedAround: side },
        };
      }
      return {
        ...edge,
        sourceHandle: forward ? 'source-right' : 'source-left',
        targetHandle: forward ? 'target-left' : 'target-right',
        data: { ...(edge.data ?? {}), flowDirection: 'horizontal' },
      };
    }
    const down = dy >= 0;
    // Vertical-dominant edge blocked by a node in the vertical corridor → bow horizontally.
    const blockers = corridorObstacles('vertical');
    if (blockers.length > 0) {
      const corridorX = (sourceCenter.x + targetCenter.x) / 2;
      const left = blockers.filter((b) => b.cx < corridorX).length;
      const right = blockers.length - left;
      const side = right <= left ? 'right' : 'left';
      return {
        ...edge,
        sourceHandle: `source-${side}`,
        targetHandle: `target-${side}`,
        data: { ...(edge.data ?? {}), flowDirection: 'vertical', reroutedAround: side },
      };
    }
    return {
      ...edge,
      sourceHandle: down ? 'source-bottom' : 'source-top',
      targetHandle: down ? 'target-top' : 'target-bottom',
      data: { ...(edge.data ?? {}), flowDirection: 'vertical' },
    };
  });
}

function topoStatusToLabel(status: string): string {
  switch (status) {
    case 'revising':        return 'Changes requested — revising';
    case 'dispatching':     return 'Dispatching';
    case 'assembling':      return 'Assembling';
    case 'in_review':       return 'In review';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'dispatched':     return 'Dispatched';
    case 'running':        return 'Running';
    case 'pending_capacity': return 'Waiting for capacity';
    case 'assemble_ready': return 'Ready for assembly';
    case 'rai_flagged':    return 'RAI flagged';
    case 'completed':      return 'Completed';
    case 'complete':       return 'Complete';
    case 'blocked':        return 'Blocked';
    case 'assembly_blocked': return 'Assembly blocked';
    case 'assembly_failed': return 'Assembly failed';
    case 'assembly_declined': return 'Assembly declined';
    case 'needs_resolution': return 'Needs resolution';
    case 'rai_blocked':    return 'RAI blocked';
    case 'failed':         return 'Failed';
    case 'unknown':        return 'Unknown';
    default:               return 'Pending';
  }
}

/** Map a coordinator graph node id (e.g. 'plan:subtask-1') to its topology node. */
function resolveSubtaskTopoNode(
  graphNodeId: string,
  topology: CoordinatorTopologyState,
): TopologyNodeState | undefined {
  if (topology.nodes[graphNodeId]) return topology.nodes[graphNodeId];
  // Strip 'plan:' prefix: 'plan:subtask-1' -> 'subtask-1'
  const stripped = graphNodeId.replace(/^plan:/, '');
  return topology.nodes[stripped];
}

// ---------------------------------------------------------------------------
// Orchestration lifecycle derivation (issues 3 & 4)
// ---------------------------------------------------------------------------

// Canonical orchestration phases. Backend strings (coordinator_status / work-plan
// status) and assembly_* events are normalized into these so the UI degrades
// gracefully whether or not Tank's in-flight fields are present.
type OrchPhase =
  | 'drafting_outcome'
  | 'dispatching'
  | 'awaiting_assembly'
  | 'assembling'
  | 'build_test'
  | 'rai'
  | 'in_review'
  | 'merge'
  | 'scribe'
  | 'complete'
  | 'failed'
  | 'blocked'
  | 'needs_resolution'
  | 'declined'
  | 'unknown';

interface OrchState {
  phase: OrchPhase;
  reason?: string;
  diff?: string;
  conflictFiles?: string[];
  conflictBranch?: string;
  revisionGateLabel?: string;
  revisionSubtaskCount?: number;
  ineligibleSubtasks?: IneligibleSubtaskInfo[];
  sourceLabel: string;
  updatedAt?: string;
}

type CoordinatorRunBucket =
  | 'pending'
  | 'running'
  | 'waiting'
  | 'blocked'
  | 'failed'
  | 'completed'
  | 'unknown';

interface CoordinatorRunViewState {
  bucket: CoordinatorRunBucket;
  label: string;
  reason?: string;
  sourceLabel: string;
  terminal: boolean;
  canRetry: boolean;
  canStop: boolean;
  canToggleAutomation: boolean;
}

const RUN_LEVEL_RETRYABLE = new Set<string>(['failed', 'merge_failed']);

// coordinator.assembly_* event type -> phase. These event types may not be emitted
// yet; absence simply means we fall through to the status field / work-plan status.
const ASSEMBLY_EVENT_PHASE: Record<string, { phase: OrchPhase; priority?: number }> = {
  'coordinator.assembly_started': { phase: 'assembling' },
  'coordinator.assembly_rai_started': { phase: 'rai' },
  'coordinator.assembly_rai_completed': { phase: 'assembling' },
  'coordinator.assembly_review_requested': { phase: 'in_review' },
  'coordinator.assembly_review_approved': { phase: 'merge' },
  // The run failed while the review gate was still open, but the gate was DELIBERATELY preserved so
  // the human can still view the changes. Keep the orchestration in the review phase (emitted after
  // assembly_failed) so the UI shows the "review still available" message instead of kicking the
  // operator out. Combined with a terminal run status this drives the preserved-review branch.
  'coordinator.assembly_review_preserved': { phase: 'in_review', priority: 4 },
  'coordinator.assembly_changes_requested': { phase: 'dispatching', priority: 3 }, // re-dispatch resets the phase
  'coordinator.assembly_merge_started': { phase: 'merge' },
  'coordinator.assembly_merge_completed': { phase: 'scribe' },
  'coordinator.assembly_merge_failed': { phase: 'failed', priority: 3 },
  'merge.conflicted': { phase: 'needs_resolution', priority: 3 },
  'coordinator.assembly_scribe_started': { phase: 'scribe' },
  'coordinator.assembly_scribe_completed': { phase: 'scribe' },
  'coordinator.assembly_completed': { phase: 'complete', priority: 3 },
  'coordinator.assembly_failed': { phase: 'failed', priority: 3 },
  'coordinator.assembly_blocked': { phase: 'blocked', priority: 3 },
  'coordinator.assembly_declined': { phase: 'declined', priority: 3 },
};

function normalizePhase(raw: string | undefined | null): OrchPhase {
  if (!raw) return 'unknown';
  const k = raw.toLowerCase().replace(/[^a-z]/g, '');
  if (k.includes('outcomespecdraft') || k.includes('draftingoutcome') || k.includes('defineoutcome') || k.includes('planning') || k === 'drafting') return 'drafting_outcome';
  if (k.includes('awaitingassembly')) return 'awaiting_assembly';
  if (k.includes('needsresolution')) return 'needs_resolution';
  if (k.includes('reviewpreserved')) return 'in_review';
  if (k.includes('reviewapproved')) return 'merge';
  if (k.includes('assembling')) return 'assembling';
  if (k.includes('inreview')) return 'in_review';
  if (k.includes('drafting') || k.includes('awaitingconfirmation')) return 'drafting_outcome';
  if (k.includes('complete')) return 'complete';
  if (k.includes('fail')) return 'failed';
  if (k.includes('block')) return 'blocked';
  if (k.includes('pendingcapacity')) return 'blocked';
  if (k.includes('declin')) return 'declined';
  if (k.includes('dispatch')) return 'dispatching';
  return 'unknown';
}

function readStr(p: Record<string, unknown>, keys: string[]): string | undefined {
  for (const k of keys) {
    const v = p[k];
    if (v != null && String(v).trim() !== '') return String(v);
  }
  return undefined;
}

function readEventTimestamp(p: Record<string, unknown>): string | undefined {
  return readStr(p, ['timestamp_utc', 'timestampUtc', 'updated_at', 'updatedAt', 'timestamp']);
}

function readChildRunId(node: GraphDescriptor['nodes'][number]): string | undefined {
  return node.child_run_id
    ?? readStr(node.data ?? {}, ['child_run_id', 'childRunId'])
    ?? (node.child_graph_ref?.startsWith('run:') ? node.child_graph_ref.slice(4) : undefined);
}

function readGateKind(payload: Record<string, unknown>): string | undefined {
  const gateKind = payload['gateKind'] ?? payload['gate_kind'];
  return gateKind == null ? undefined : String(gateKind).toLowerCase();
}

function gateLabelForKind(gateKind: string | undefined): string {
  switch (gateKind) {
    case 'build-test': return 'Build & Test';
    case 'rubberduck': return 'Rubberduck';
    case 'human-review':
    case undefined: return 'Human Review';
    default: return gateKind.replace(/[-_]/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
  }
}

function readSubtaskIdsFromPayload(payload: Record<string, unknown>): string[] {
  const raw = payload['redispatchedSubtaskIds'] ?? payload['redispatchSubtaskIds'];
  return Array.isArray(raw)
    ? raw.map((id) => String(id).trim()).filter(Boolean)
    : [];
}

function normalizeSubtaskId(value: string): string {
  return value.trim().replace(/^plan:/i, '').replace(/^subtask-/i, '');
}

// #97 — a single ineligible (assembly-blocking) subtask surfaced from the enriched
// coordinator.assembly_blocked payload (id + title + status + agent), so the UI can name WHICH
// subtasks blocked assembly and WHY (their actual status) instead of an opaque error code.
export interface IneligibleSubtaskInfo {
  id: string;
  title?: string;
  status?: string;
  agent?: string;
}

// #97 — parse the enriched `ineligibleSubtasks` array (fallback: the id-only `ineligibleSubtaskIds`,
// or the `[59,60,61,62]` ids embedded in the reason string) out of an assembly-blocked payload.
// Returns [] when the payload carries no ineligible-subtask hint at all.
export function readIneligibleSubtasks(payload: Record<string, unknown>): IneligibleSubtaskInfo[] {
  const enriched = payload['ineligibleSubtasks'] ?? payload['ineligible_subtasks'];
  if (Array.isArray(enriched)) {
    const parsed = enriched
      .map((raw): IneligibleSubtaskInfo | null => {
        if (raw == null || typeof raw !== 'object') {
          const id = String(raw ?? '').trim();
          return id ? { id } : null;
        }
        const obj = raw as Record<string, unknown>;
        const id = readStr(obj, ['id', 'subtaskId', 'subtask_id']);
        if (!id) return null;
        return {
          id,
          title: readStr(obj, ['title', 'name']),
          status: readStr(obj, ['status']),
          agent: readStr(obj, ['agent', 'assignedAgent', 'assigned_agent']),
        };
      })
      .filter((x): x is IneligibleSubtaskInfo => x !== null);
    if (parsed.length > 0) return parsed;
  }
  const ids = payload['ineligibleSubtaskIds'] ?? payload['ineligible_subtask_ids'];
  if (Array.isArray(ids)) {
    return ids.map((id) => ({ id: String(id).trim() })).filter((x) => x.id !== '');
  }
  // Last resort: recover the ids from the `ineligible_subtasks [59,60,61,62]` reason text.
  const reason = readStr(payload, ['reason']);
  return reason ? parseIneligibleIdsFromReason(reason).map((id) => ({ id })) : [];
}

// #97 — pull the bracketed subtask ids out of an `ineligible_subtasks [59,60,61,62]` reason string
// (tolerating an `assembly_blocked: ` prefix). Returns [] when the reason is not that class.
export function parseIneligibleIdsFromReason(reason: string | undefined | null): string[] {
  if (!reason) return [];
  const match = /ineligible_subtasks\s*\[([^\]]*)\]/i.exec(reason);
  if (!match) return [];
  return match[1]
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s !== '');
}

// #97 — turn a raw assembly-blocked reason code into readable prose instead of surfacing the opaque
// `ineligible_subtasks [59,60,61,62]` (or the generic "could not complete" fallback). Non-ineligible
// reasons are humanized (strip the `assembly_blocked:` prefix, underscores → spaces).
export function normalizeAssemblyBlockedReason(reason: string | undefined | null): string | undefined {
  if (!reason || reason.trim() === '') return undefined;
  const stripped = reason.replace(/^assembly_blocked:\s*/i, '').trim();
  const ids = parseIneligibleIdsFromReason(stripped);
  if (ids.length > 0) {
    const list = ids.map((id) => `#${id}`).join(', ');
    return ids.length === 1
      ? `Waiting on 1 subtask that isn't ready to assemble (${list}).`
      : `Waiting on ${ids.length} subtasks that aren't ready to assemble (${list}).`;
  }
  return stripped.replace(/_/g, ' ');
}


function isHumanReviewGateEvent(evt: RunStreamEvent): boolean {
  const gateKind = readGateKind(evt.payload);
  return gateKind === undefined || gateKind === 'human-review';
}

function assemblyReviewPhaseForEvent(evt: RunStreamEvent, fallback: OrchPhase): OrchPhase {
  const gateKind = readGateKind(evt.payload);
  if (evt.type === 'coordinator.assembly_review_requested') {
    return isHumanReviewGateEvent(evt)
      ? 'in_review'
      : gateKind === 'build-test'
        ? 'build_test'
        : 'assembling';
  }
  if (
    evt.type === 'coordinator.assembly_review_approved'
    || evt.type === 'coordinator.assembly_review_preserved'
    || evt.type === 'coordinator.assembly_declined'
  ) {
    return isHumanReviewGateEvent(evt) ? fallback : 'assembling';
  }
  return fallback;
}

function isBuildTestNodeIdOrLabel(id: string | undefined, label: string | undefined): boolean {
  const key = `${id ?? ''} ${label ?? ''}`.toLowerCase().replace(/[^a-z0-9]+/g, ' ');
  return key.includes('build test') || key.includes('buildtest');
}

function effectiveGraphRole(node: GraphDescriptor['nodes'][number]): string {
  const explicitType = readStr(node.data ?? {}, ['type', 'nodeType', 'node_type', 'gate', 'kind']);
  if (node.role === 'build_test' || explicitType === 'build_test' || isBuildTestNodeIdOrLabel(node.id, node.label)) {
    return 'build_test';
  }
  return node.role;
}

function isLiveStatus(status: string | undefined): boolean {
  return status === 'running'
    || status === 'dispatched'
    || status === 'dispatching'
    || status === 'in_progress'
    || status === 'awaiting_assembly'
    || status === 'assembling'
    || status === 'merging'
    || status === 'in_review'
    || status === 'awaiting_review';
}

function terminalizedStatus(status: string | undefined, terminal: boolean, terminalStatus: string): string {
  if (!terminal || !isLiveStatus(status)) return status ?? 'pending';
  return terminalStatus;
}

function previewUrlFromSession(session: PortForwardSessionDto | undefined): string | null {
  return session?.preview_url ?? session?.previewUrl ?? null;
}

type RunPreviewState =
  | { status: 'none' }
  | { status: 'ready'; previewUrl: string; targetPort?: string }
  | { status: 'pending'; targetPort?: string }
  | { status: 'failed'; reason: string; message?: string };

function latestPreviewStateFromEvents(events: RunStreamEvent[]): RunPreviewState {
  for (let i = events.length - 1; i >= 0; i -= 1) {
    const evt = events[i];
    if (evt.type === 'sandbox.preview_ready' || evt.type === 'coordinator.preview_ready') {
      const preview = evt.payload['preview_url'] ?? evt.payload['previewUrl'];
      if (preview != null && String(preview).trim() !== '') {
        const targetPort = evt.payload['target_port'] ?? evt.payload['targetPort'];
        return {
          status: 'ready',
          previewUrl: String(preview),
          targetPort: targetPort == null ? undefined : String(targetPort),
        };
      }
    }
    if (evt.type === 'sandbox.preview_pending') {
      const targetPort = evt.payload['target_port'] ?? evt.payload['targetPort'];
      return {
        status: 'pending',
        targetPort: targetPort == null ? undefined : String(targetPort),
      };
    }
    if (evt.type === 'sandbox.preview_failed') {
      const reason = readStr(evt.payload, ['reason']) ?? 'unknown';
      const message = readStr(evt.payload, ['message']);
      return { status: 'failed', reason, message };
    }
  }
  return { status: 'none' };
}

function previewFailureCopy(state: Extract<RunPreviewState, { status: 'failed' }>): string {
  const reason = state.reason.replace(/_/g, ' ');
  return state.message ? `${reason}: ${state.message}` : reason;
}

// Priority: live assembly_* events (last wins) > coordinator_status field > work-plan status.
function deriveOrchState(
  events: RunStreamEvent[],
  statusField: string | undefined,
  reasonField: string | undefined,
  workPlanStatus: string | undefined,
): OrchState {
  let winner: { phase: OrchPhase; payload: Record<string, unknown>; type: string; sequence: number; priority: number; gateLabel?: string } | undefined;
  let latestOutcomeDrafting: RunStreamEvent | undefined;
  let latestOutcomeSupersedingSeq = -1;
  let latestAssemblyGateLabel = gateLabelForKind(undefined);
  for (const evt of events) {
    if (evt.type === 'coordinator.outcome_spec.drafting') {
      if (!latestOutcomeDrafting || evt.sequence >= latestOutcomeDrafting.sequence) latestOutcomeDrafting = evt;
    } else if (
      evt.type === 'coordinator.outcome_spec'
      || evt.type === 'coordinator.outcome_spec.confirmed'
      || evt.type === 'coordinator.work_plan'
      || evt.type === 'subtask.dispatched'
      || evt.type === 'subtask.running'
    ) {
      latestOutcomeSupersedingSeq = Math.max(latestOutcomeSupersedingSeq, evt.sequence ?? -1);
    }
    if (evt.type === 'coordinator.assembly_review_requested') {
      latestAssemblyGateLabel = gateLabelForKind(readGateKind(evt.payload));
    }
    const mapped = ASSEMBLY_EVENT_PHASE[evt.type as string];
    if (!mapped) continue;
    const priority = mapped.priority ?? 1;
    const phase = assemblyReviewPhaseForEvent(evt, mapped.phase);
    if (!winner || priority > winner.priority || (priority === winner.priority && evt.sequence >= winner.sequence)) {
      winner = {
        phase,
        payload: evt.payload,
        type: evt.type,
        sequence: evt.sequence,
        priority,
        gateLabel: evt.type === 'coordinator.assembly_changes_requested' ? latestAssemblyGateLabel : undefined,
      };
    }
  }
  if (winner) {
    const rawFiles = winner.payload['conflictingFiles'] ?? winner.payload['conflicting_files'];
    const conflictFiles = Array.isArray(rawFiles)
      ? rawFiles.map((f) => String(f)).filter((f) => f.trim() !== '')
      : undefined;
    const isBlocked = winner.type === 'coordinator.assembly_blocked';
    // #97: name WHICH subtasks blocked assembly (id/title/status/agent) and normalize the opaque
    // `ineligible_subtasks [ids]` reason into readable prose instead of a bare code / generic fallback.
    // The live blocked event's payload.reason is just "ineligible_subtasks" (the ids live in the
    // structured array), so synthesize a bracketed reason from those ids to drive the readable copy.
    const ineligibleSubtasks = isBlocked ? readIneligibleSubtasks(winner.payload) : [];
    const rawReason = readStr(winner.payload, ['reason', 'message', 'error', 'detail', 'feedback']);
    const blockedReasonSource = isBlocked
      && rawReason
      && parseIneligibleIdsFromReason(rawReason).length === 0
      && ineligibleSubtasks.length > 0
        ? `ineligible_subtasks [${ineligibleSubtasks.map((s) => s.id).join(',')}]`
        : rawReason;
    return {
      phase: winner.phase,
      reason: isBlocked ? normalizeAssemblyBlockedReason(blockedReasonSource) : rawReason,
      diff: readStr(winner.payload, ['diff', 'summary', 'integrationDiff', 'integration_diff', 'treeHash', 'tree_hash']),
      conflictFiles: conflictFiles && conflictFiles.length > 0 ? conflictFiles : undefined,
      conflictBranch: readStr(winner.payload, ['conflictingBranch', 'conflicting_branch']),
      revisionGateLabel: winner.type === 'coordinator.assembly_changes_requested' ? winner.gateLabel : undefined,
      revisionSubtaskCount: winner.type === 'coordinator.assembly_changes_requested' ? readSubtaskIdsFromPayload(winner.payload).length : undefined,
      ineligibleSubtasks: ineligibleSubtasks.length > 0 ? ineligibleSubtasks : undefined,
      sourceLabel: `${winner.type} event #${winner.sequence}`,
      updatedAt: readEventTimestamp(winner.payload),
    };
  }
  if (latestOutcomeDrafting && (latestOutcomeDrafting.sequence ?? -1) > latestOutcomeSupersedingSeq) {
    return {
      phase: 'drafting_outcome',
      reason: readStr(latestOutcomeDrafting.payload, ['message', 'reason', 'detail']),
      sourceLabel: `${latestOutcomeDrafting.type} event #${latestOutcomeDrafting.sequence}`,
      updatedAt: readEventTimestamp(latestOutcomeDrafting.payload),
    };
  }
  const fieldPhase = normalizePhase(statusField);
  if (fieldPhase !== 'unknown') {
    // #97: when the live blocked stream event was evicted (reload/reconnect) the only surviving signal
    // is the persisted status/reason field — normalize the `ineligible_subtasks [ids]` code here too so
    // the reason line never degrades back to the opaque raw code.
    const normalizedFieldReason = fieldPhase === 'blocked'
      ? normalizeAssemblyBlockedReason(reasonField)
      : reasonField ?? undefined;
    return {
      phase: fieldPhase,
      reason: normalizedFieldReason,
      ineligibleSubtasks: fieldPhase === 'blocked' && reasonField
        ? (parseIneligibleIdsFromReason(reasonField).map((id) => ({ id })) || undefined)
        : undefined,
      sourceLabel: 'run status field',
    };
  }
  const wpPhase = normalizePhase(workPlanStatus);
  if (wpPhase !== 'unknown') return { phase: wpPhase, sourceLabel: 'work plan status' };
  return { phase: 'unknown', sourceLabel: 'no phase source yet' };
}

// Coordinator graph node status (so it never shows a stale "Pending").
function orchPhaseToTopoStatus(phase: OrchPhase): string | undefined {
  switch (phase) {
    case 'complete': return 'completed';
    case 'failed':
    case 'declined': return 'failed';
    case 'blocked': return 'blocked';
    case 'unknown': return undefined;
    default: return 'running';
  }
}

// Collective-assembly stage node status, derived from the orchestration phase. Assembly is
// automated EXCEPT the Human Review gate, which waits on the user: during `in_review` the review
// node becomes 'started' so WorkflowNode renders it action-required ("Awaiting your review").
// Returns undefined for stages not yet reached so the backend planned/live kind is preserved.
// role ∈ {rai, review, merge, scribe}.
function assemblyTerminalStageMatchesRole(role: string, terminalStage: string | undefined): boolean {
  if (!terminalStage) return false;
  const stage = terminalStage.toLowerCase().replace(/[^a-z0-9]/g, '');
  const normalizedRole = role.toLowerCase().replace(/[^a-z0-9]/g, '');
  if (stage === normalizedRole) return true;
  if (normalizedRole === 'review') return stage.includes('review');
  if (normalizedRole === 'rai') return stage.includes('rai');
  if (normalizedRole === 'merge') return stage.includes('merge');
  if (normalizedRole === 'scribe') return stage.includes('scribe');
  return false;
}

function assemblyNodeStatus(role: string, phase: OrchPhase, terminalStage?: string): StepStatus | undefined {
  switch (phase) {
    case 'assembling':
    case 'build_test':
    case 'rai':
      if (role === 'build_test') return phase === 'build_test' ? 'started' : undefined;
      return role === 'rai' ? 'started' : undefined;
    case 'in_review':
      if (role === 'build_test') return 'completed';
      if (role === 'rai')    return 'completed';
      if (role === 'review') return 'started';
      return undefined;
    case 'merge':
      if (role === 'build_test' || role === 'rai' || role === 'review') return 'completed';
      if (role === 'merge') return 'started';
      return undefined;
    case 'scribe':
      if (role === 'build_test' || role === 'rai' || role === 'review' || role === 'merge') return 'completed';
      if (role === 'scribe') return 'started';
      return undefined;
    case 'complete':
      return 'completed';
    case 'needs_resolution':
      if (role === 'build_test' || role === 'rai' || role === 'review') return 'completed';
      if (terminalStage) return assemblyTerminalStageMatchesRole(role, terminalStage) ? 'failed' : undefined;
      if (role === 'merge') return 'failed';
      return undefined;
    case 'failed':
      if (terminalStage) return assemblyTerminalStageMatchesRole(role, terminalStage) ? 'failed' : undefined;
      return role === 'merge' ? 'failed' : undefined;
    case 'declined':
      if (terminalStage) return assemblyTerminalStageMatchesRole(role, terminalStage) ? 'failed' : undefined;
      if (role === 'review') return 'failed';
      if (role === 'build_test' || role === 'rai') return 'completed';
      return undefined;
    default:
      return undefined;
  }
}

function orchPhaseLabel(phase: OrchPhase): string {
  switch (phase) {
    case 'drafting_outcome':    return 'Drafting outcome plan';
    case 'dispatching':       return 'Dispatching';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'assembling':        return 'Assembling';
    case 'build_test':        return 'Build & Test';
    case 'rai':               return 'RAI review';
    case 'in_review':         return 'In review';
    case 'merge':             return 'Merging';
    case 'scribe':            return 'Scribing';
    case 'complete':          return 'Complete';
    case 'failed':            return 'Failed';
    case 'blocked':           return 'Blocked';
    case 'needs_resolution':  return 'Needs resolution';
    case 'declined':          return 'Declined';
    default:                  return 'Unknown';
  }
}

function runStatusLabel(status: string | undefined): string {
  switch (status) {
    case 'pending': return 'Pending';
    case 'in_progress': return 'In progress';
    case 'completed': return 'Completed';
    case 'failed': return 'Failed';
    case 'blocked': return 'Blocked';
    case 'awaiting_review': return 'Awaiting review';
    case 'merging': return 'Merging';
    case 'merged': return 'Merged';
    case 'declined': return 'Declined';
    case 'merge_failed': return 'Merge failed';
    case 'needs_resolution': return 'Needs resolution';
    case 'assemble_ready': return 'Ready for assembly';
    default: return 'Unknown';
  }
}

function bucketForRunStatus(status: string | undefined): CoordinatorRunBucket {
  switch (status) {
    case 'pending': return 'pending';
    case 'in_progress':
    case 'merging': return 'running';
    case 'awaiting_review': return 'waiting';
    case 'assemble_ready': return 'completed';
    case 'blocked': return 'blocked';
    case 'failed':
    case 'declined':
    case 'merge_failed': return 'failed';
    case 'completed':
    case 'merged': return 'completed';
    default: return 'unknown';
  }
}

function bucketForOrchPhase(phase: OrchPhase): CoordinatorRunBucket {
  switch (phase) {
    case 'drafting_outcome':
    case 'dispatching':
    case 'awaiting_assembly':
    case 'assembling':
    case 'build_test':
    case 'rai':
    case 'merge':
    case 'scribe': return 'running';
    case 'in_review': return 'waiting';
    case 'complete': return 'completed';
    case 'failed':
    case 'declined': return 'failed';
    case 'needs_resolution':
    case 'blocked': return 'blocked';
    default: return 'unknown';
  }
}

function deriveCoordinatorRunViewState(
  runLevelStatus: RunStatus | undefined,
  orch: OrchState,
  loadError: FormattedApiError | null,
): CoordinatorRunViewState {
  if (loadError && runLevelStatus === undefined) {
    const label =
      loadError.kind === 'not-found' ? 'Run not found'
      : loadError.kind === 'unauthorized' ? 'Authentication required'
      : loadError.kind === 'forbidden' ? 'Permission required'
      : 'Run unavailable';
    return {
      bucket: 'unknown',
      label,
      reason: loadError.detail ?? loadError.message,
      sourceLabel: `GET /runs failed${loadError.status ? ` (${loadError.status})` : ''}`,
      terminal: true,
      canRetry: false,
      canStop: false,
      canToggleAutomation: false,
    };
  }

  const status = runLevelStatus ? String(runLevelStatus) : undefined;
  if (isTerminalRunStatus(status)) {
    const terminalStatus = status ?? 'failed';
    const reviewPreserved = orch.sourceLabel.includes('coordinator.assembly_review_preserved');
    return {
      bucket: bucketForRunStatus(terminalStatus),
      label: reviewPreserved ? 'Review preserved' : runStatusLabel(terminalStatus),
      reason: orch.reason ?? (reviewPreserved ? 'The run ended, but the assembly review artifact is still available to inspect.' : undefined),
      sourceLabel: reviewPreserved ? orch.sourceLabel : 'run status field',
      terminal: true,
      canRetry: RUN_LEVEL_RETRYABLE.has(terminalStatus),
      canStop: false,
      canToggleAutomation: false,
    };
  }

  const orchBucket = bucketForOrchPhase(orch.phase);
  if (orchBucket !== 'unknown') {
    return {
      bucket: orchBucket,
      label: orch.phase === 'dispatching' && orch.revisionGateLabel
        ? `Revising after ${orch.revisionGateLabel} feedback`
        : orchPhaseLabel(orch.phase),
      reason: orch.reason,
      sourceLabel: orch.sourceLabel,
      terminal: orchBucket === 'completed' || orchBucket === 'failed' || orchBucket === 'blocked',
      canRetry: false,
      canStop: status === 'in_progress',
      canToggleAutomation: status === 'in_progress',
    };
  }

  const runBucket = bucketForRunStatus(status);
  return {
    bucket: runBucket,
    label: runStatusLabel(status),
    reason: orch.reason,
    sourceLabel: status ? 'run status field' : orch.sourceLabel,
    terminal: false,
    canRetry: false,
    canStop: status === 'in_progress',
    canToggleAutomation: status === 'in_progress',
  };
}

// ---------------------------------------------------------------------------
// Session timeline derivation (issue 6)
// ---------------------------------------------------------------------------

function fmtTotal(ms: number): string {
  const secs = Math.floor(ms / 1000);
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  const s = secs % 60;
  if (mins < 60) return `${mins}m ${s}s`;
  const hrs = Math.floor(mins / 60);
  return `${hrs}h ${mins % 60}m`;
}

// Parent subtask elapsed = sum of the child pipeline steps' durations (issue 2).
// Ticks live while any child step is still running.
function useTickingNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [active]);
  return now;
}

// ---------------------------------------------------------------------------
// Subtask node data + custom React Flow node
// ---------------------------------------------------------------------------

interface SubtaskNodeData extends Record<string, unknown> {
  graphNodeId: string;
  label: string;
  topoStatus: string;
  topoNode: TopologyNodeState | undefined;
  childGraphRef: string | undefined;
  childRunId: string | undefined;
  agent: string | undefined;
  agentRole: string | undefined;
  model: string | undefined;
  phase: string | undefined;
  projectId: string;
  startedAt?: number;
  completedAt?: number;
  totalNanoAiu?: number | null;
  totalTokens?: number | null;
  executionPodName?: string | null;
  /** Layout direction for handle placement. 'LR' = left/right; 'TB' = top/bottom; 'GRID' exposes all sides for routed grid edges. */
  dir?: 'LR' | 'TB' | 'GRID';
}

// Dagre separations for the compact coordinator DAG. Small pill nodes pack tightly: ranks
// (columns in the LR layout) sit COORD_GRAPH_RANK_SEP apart, and parallel siblings within a
// rank stack COORD_GRAPH_NODE_SEP apart. Independent subtasks land at the same dagre rank
// (parallel); dependents chain into later ranks — all derived from the real graph edges.
const COORD_GRAPH_RANK_SEP = 40;
const COORD_GRAPH_NODE_SEP = 20;

function SubtaskNode({ id, data, selected }: NodeProps) {
  const s = useNodeStyles();
  const d = data as SubtaskNodeData;
  const openPanel = useContext(CoordPanelContext);
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  const stepStatus  = topoStatusToStepStatus(d.topoStatus as string);
  const statusLbl   = topoStatusToLabel(d.topoStatus as string);
  const podName     = d.executionPodName as string | null | undefined;
  const roleTitle   = (d.agentRole as string | undefined) ?? 'Subtask Agent';
  const label       = d.label as string;
  const agentName   = d.agent as string | undefined;
  const modelCaption = d.model ? formatModelLabel(d.model as string) : undefined;
  const hasCredits  = d.totalNanoAiu != null || d.totalTokens != null;
  // Subtask (agent) nodes are the ONLY tall/rich pills — avatar + 2-line title + Name(Role) line +
  // model caption below + AI credits on the face. Every other node uses the compact WorkflowNode.
  const nameRoleText = agentName ? `${agentName} (${roleTitle})` : roleTitle;
  const showNameRole = Boolean(nameRoleText) && nameRoleText !== label;

  const handleSelectNode = useCallback(() => {
    // Clicking/selecting the node face itself (to zoom onto it in the topology) must NOT close
    // the topology overlay — only the explicit "View session" action does that. This handler
    // backs both the pill's own click/keyboard activation and bubbles up into the graph's
    // onNodeClick (which drives the cinematic pan+zoom), so it stays a no-close open.
    openPanel?.(id);
  }, [id, openPanel]);

  const handleViewSessionClick = useCallback((event: React.MouseEvent) => {
    // Stop the click from also reaching the pill's own onClick / the graph's onNodeClick —
    // "View session" closes the topology overlay and surfaces this session in the run tree,
    // instead of leaving the topology stacked on top of the panel it just opened.
    event.stopPropagation();
    openPanel?.(id, { closeTopology: true });
  }, [id, openPanel]);

  const avatar = agentName
    ? <AgentAvatar name={agentName} size={26} circle badgeIcon={BotRegular} badgeTitle={roleTitle} />
    : <BotRegular fontSize={20} />;

  const rows: NodeDetailRow[] = [
    { label: 'Status', value: statusLbl },
    { label: 'Role', value: roleTitle },
    ...(agentName ? [{ label: 'Agent', value: agentName }] : []),
    ...(d.model ? [{ label: 'Model', value: formatModelLabel(d.model as string), mono: true }] : []),
    ...(d.phase ? [{ label: 'Phase', value: d.phase as string }] : []),
    ...(d.startedAt !== undefined
      ? [{ label: 'Duration', value: <ElapsedTimer startedAt={d.startedAt as number} completedAt={d.completedAt as number | undefined} /> }]
      : []),
    ...(podName ? [{ label: 'Pod', value: podName, mono: true }] : []),
    ...((d.totalNanoAiu != null || d.totalTokens != null)
      ? [{ label: 'Credits', value: <AiCredits totalNanoAiu={d.totalNanoAiu as number | null | undefined} totalTokens={d.totalTokens as number | null | undefined} /> }]
      : []),
  ];

  const actions = d.childRunId
    ? <Button appearance="outline" size="small" onClick={handleViewSessionClick}>View session</Button>
    : undefined;

  const face = (
    <div className={s.pillWrap}>
      <div
        className={mergeClasses(
          s.pill,
          s.pillTall,
          stepStatus === 'started' ? s.cardActive : undefined,
          selected ? s.pillSelected : undefined,
        )}
        data-node-type="subtask"
        role="article"
        aria-label={`${label}: ${statusLbl}`}
        aria-current={selected ? 'true' : undefined}
        onClick={handleSelectNode}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            handleSelectNode();
          }
        }}
        tabIndex={0}
      >
        {d.dir === 'GRID' ? (
          <>
            <Handle id="target-left" type="target" position={Position.Left} style={handleStyle} />
            <Handle id="target-right" type="target" position={Position.Right} style={handleStyle} />
            <Handle id="target-top" type="target" position={Position.Top} style={handleStyle} />
            <Handle id="target-bottom" type="target" position={Position.Bottom} style={handleStyle} />
            <Handle id="source-left" type="source" position={Position.Left} style={handleStyle} />
            <Handle id="source-right" type="source" position={Position.Right} style={handleStyle} />
            <Handle id="source-top" type="source" position={Position.Top} style={handleStyle} />
            <Handle id="source-bottom" type="source" position={Position.Bottom} style={handleStyle} />
          </>
        ) : (
          <>
            <Handle type="target" position={d.dir === 'TB' ? Position.Top : Position.Left} style={handleStyle} />
            <Handle type="source" position={d.dir === 'TB' ? Position.Bottom : Position.Right} style={handleStyle} />
          </>
        )}

        <span className={`${s.accentBar} ${accentClass(s, stepStatus)}`} aria-hidden="true" />

        <span className={s.pillIcon} aria-hidden="true">{avatar}</span>
        <div className={s.pillBody}>
          <div className={s.pillTitleRow}>
            <span className={s.pillTitle}>{label}</span>
            {hasCredits && (
              <span className={s.pillCredits}>
                <AiCredits totalNanoAiu={d.totalNanoAiu as number | null | undefined} totalTokens={d.totalTokens as number | null | undefined} />
              </span>
            )}
          </div>
          {showNameRole && <span className={s.pillNameRole}>{nameRoleText}</span>}
        </div>
      </div>
      {modelCaption && <span className={s.pillModelCaption} title={modelCaption}>{modelCaption}</span>}
    </div>
  );

  return (
    <NodeDetailPopover title={label} roleText={roleTitle} Icon={BotRegular} avatar={avatar} rows={rows} actions={actions}>
      {face}
    </NodeDetailPopover>
  );
}

/** Combined node types: generic workflow nodes + subtask expandable node. */
const coordinatorNodeTypes = { ...workflowNodeTypes, subtask: SubtaskNode };

// ---------------------------------------------------------------------------
// Page styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    width: '100%',
    minWidth: 0,
    height: '100%',
    minHeight: 0,
    overflow: 'hidden',
  },
  breadcrumb: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecorationLine: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  statusBannerStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  console: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
    flex: 1,
    minHeight: 0,
  },
  // ---- Run header (identity / actions grid) --------------------------------
  runHeader: {
    display: 'grid',
    gridTemplateColumns: '1fr',
    gridTemplateAreas: '"identity" "actions"',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  identityArea: {
    gridArea: 'identity',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: 0,
  },
  actionsArea: {
    gridArea: 'actions',
    paddingTop: tokens.spacingVerticalM,
    borderTopWidth: tokens.strokeWidthThin,
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    minWidth: 0,
  },
  topTitleRow: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  identityLead: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
    flex: '1 1 auto',
  },
  titleText: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    margin: 0,
    maxWidth: '100%',
    minWidth: 0,
  },
  liveDot: {
    display: 'inline-flex',
    alignItems: 'center',
  },
  statusChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    paddingLeft: tokens.spacingHorizontalS,
    paddingRight: tokens.spacingHorizontalS,
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'nowrap',
  },
  statusChipStrong: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  statsStrip: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  compactChromeActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexShrink: 0,
  },
  metaRail: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  metaItem: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
  },
  metaItemStrong: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  metaValue: {
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    maxWidth: '240px',
  },
  metaSeparator: {
    color: tokens.colorNeutralForeground4,
  },
  executionContext: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    paddingTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  executionKicker: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  executionSeparator: {
    color: tokens.colorNeutralForeground4,
  },
  executionValue: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
    maxWidth: '100%',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  executionReason: {
    color: tokens.colorNeutralForeground3,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  statusDetails: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  statusDetailsSummary: {
    cursor: 'pointer',
    color: tokens.colorNeutralForeground2,
    fontWeight: tokens.fontWeightMedium,
  },
  statusDetailsBody: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingTop: tokens.spacingVerticalXS,
  },
  // ---- Run actions toolbar --------------------------------------------------
  runToolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalS,
    minWidth: 0,
  },
  toolbarSection: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  toolbarLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightMedium,
  },
  riskToggleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  toolbarDivider: {
    width: tokens.strokeWidthThin,
    alignSelf: 'stretch',
    minHeight: '20px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  phaseSource: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  stateReason: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  creditsSurface: {
    width: '360px',
    maxHeight: '420px',
    overflowY: 'auto',
    padding: tokens.spacingVerticalM,
  },
  // ---- Error / not-found ----------------------------------------------------
  pageError: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalXXL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  pageErrorActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    paddingTop: tokens.spacingVerticalS,
  },
  // ---- Body: tree | center --------------------------------------------------
  bodyGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 340px) minmax(0, 1fr)',
    gridTemplateRows: 'minmax(0, 1fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    minWidth: 0,
    flex: 1,
    minHeight: 0,
    '@media (max-width: 960px)': {
      gridTemplateColumns: '1fr',
      gridTemplateRows: 'auto minmax(0, 1fr)',
    },
  },
  // Collapsed rail: shrink the first column to a thin strip so the center Messages surface reflows
  // to fill the freed width.
  bodyGridCollapsed: {
    gridTemplateColumns: 'min-content minmax(0, 1fr)',
    '@media (max-width: 960px)': {
      gridTemplateColumns: '1fr',
    },
  },
  treeRail: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: 0,
    minHeight: 0,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 960px)': {
      maxHeight: '320px',
    },
  },
  // Collapsed rail strip: just the expand affordance pinned to the top.
  treeRailCollapsed: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    flexShrink: 0,
    paddingTop: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalXXS,
    paddingRight: tokens.spacingHorizontalXXS,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  treeRailHeader: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  treeRailHeaderRight: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
  treeScroll: {
    flexGrow: 1,
    minHeight: 0,
    overflowY: 'auto',
    overflowX: 'hidden',
    // No per-level indentation — every row (Coordinator + children) aligns at the same left
    // edge; the tree hierarchy reads from the status icon + "· Agent (Role)" secondary, not
    // from indentation. Flat, consistent left gutter across all levels.
    '& .fui-TreeItemLayout': {
      paddingLeft: tokens.spacingHorizontalXS,
    },
  },
  treeRailFooter: {
    flexShrink: 0,
    marginTop: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalS,
    borderTop: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  railStatusBlock: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    marginTop: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalS,
    borderTop: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  railStatusCaption: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  railStatusWorkflow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  railStatusReason: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
  },
  railStatusState: {
    fontWeight: tokens.fontWeightSemibold,
  },
  railStatusInfoTrigger: {
    display: 'inline-flex',
    alignItems: 'center',
    cursor: 'help',
    color: tokens.colorNeutralForeground3,
  },
  railStatusInfoGlyph: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  railStatusReasonShort: {
    marginLeft: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground2,
  },
  railStatusIneligible: {
    marginTop: tokens.spacingVerticalXS,
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalXXS,
  },
  railStatusIneligibleCaption: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground2,
    overflowWrap: 'anywhere',
  },
  railStatusIneligibleList: {
    margin: 0,
    paddingLeft: 0,
    listStyleType: 'none',
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalXXS,
  },
  railStatusIneligibleId: {
    fontWeight: tokens.fontWeightSemibold,
    marginRight: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground1,
  },
  railStatusIneligibleTitle: {
    marginRight: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground2,
  },
  railStatusIneligibleState: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    textTransform: 'capitalize',
  },
  treeEmpty: {
    padding: tokens.spacingVerticalS,
  },
  runTreeItem: {
    minWidth: 0,
  },
  treeItemLayout: {
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
    minHeight: '40px',
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid transparent`,
  },
  treeItemLayoutSelected: {
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1Selected,
  },
  runTreeStatusIcon: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '18px',
    height: '18px',
    flexShrink: 0,
  },
  runTreeStatusRunning: { color: tokens.colorBrandForeground1 },
  runTreeStatusSuccess: { color: tokens.colorStatusSuccessForeground1 },
  runTreeStatusDanger: { color: tokens.colorStatusDangerForeground1 },
  runTreeStatusInput: { color: tokens.colorStatusWarningForeground1 },
  runTreeStatusQueued: { color: tokens.colorNeutralForeground3 },
  treeText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '3px',
    lineHeight: tokens.lineHeightBase300,
    minWidth: 0,
  },
  treeNode: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalSNudge,
    marginLeft: '-4px',
    minWidth: 0,
  },
  // Task-first tree row: bold PRIMARY = task title (full width, ellipsis); one muted
  // SECONDARY line = [dot] statusLabel · agentName (role). No pills, no side-stripes.
  treePrimary: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    minWidth: 0,
  },
  treeMetaRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
    overflow: 'hidden',
    whiteSpace: 'nowrap',
  },
  treeStatusText: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightMedium,
    flexShrink: 0,
  },
  treeStatusDot: {
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    backgroundColor: 'currentColor',
    flexShrink: 0,
  },
  treeIdentity: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    minWidth: 0,
  },
  stateTextRunning: { color: tokens.colorBrandForeground1 },
  stateTextSuccess: { color: tokens.colorStatusSuccessForeground1 },
  stateTextDanger: { color: tokens.colorStatusDangerForeground1 },
  stateTextInput: { color: tokens.colorStatusWarningForeground1 },
  stateTextQueued: { color: tokens.colorNeutralForeground3 },
  // ---- Center: tabs + minimap ----------------------------------------------
  centerZone: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
    minHeight: 0,
  },
  minimapButton: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingHorizontalXS,
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    flexShrink: 0,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  minimapCanvas: {
    position: 'relative',
    width: '100%',
    height: '104px',
    overflow: 'hidden',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    pointerEvents: 'none',
    '& .react-flow__minimap': {
      position: 'absolute',
      top: 0,
      left: 0,
      right: 0,
      bottom: 0,
      width: '100%',
      height: '100%',
      margin: 0,
    },
    '& .react-flow__minimap-svg': {
      width: '100%',
      height: '100%',
    },
  },
  minimapCaption: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    textAlign: 'center',
  },
  minimapEmpty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '100%',
    height: '100%',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  workPlanTopologyThumbnail: {
    maxWidth: '260px',
    marginTop: tokens.spacingVerticalM,
  },
  centerTabBody: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    minHeight: 0,
    flex: 1,
  },
  readoutBody: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minHeight: 0,
    minWidth: 0,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    overflow: 'hidden',
  },
  tabPanelCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minHeight: '520px',
    minWidth: 0,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  approvalGateWrap: {
    minWidth: 0,
  },
  runChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalSNudge,
    minHeight: '32px',
    padding: `${tokens.spacingVerticalSNudge} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusCircular,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  runChipLabel: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  runChipCount: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground3,
  },
  runChipAdded: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorPaletteGreenForeground1,
  },
  runChipRemoved: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorPaletteRedForeground1,
  },
  // Disabled/empty-state chip (e.g. "Changes · None"): non-interactive, muted, no hover.
  runChipDisabled: {
    cursor: 'default',
    color: tokens.colorNeutralForeground3,
    opacity: 0.8,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1 },
  },
  runChipDot: {
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    backgroundColor: tokens.colorPaletteGreenForeground1,
    flexShrink: 0,
  },
  scopeHint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  // ---- Topology (preserved graph plumbing) ---------------------------------
  dagContainer: {
    minHeight: '200px',
    width: '100%',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
    position: 'relative',
    display: 'flex',
    '& .react-flow__renderer': { borderRadius: '8px' },
    '& .react-flow__minimap': {
      opacity: 0,
      transform: 'scale(0.92)',
      transformOrigin: 'bottom right',
      transition: 'opacity 120ms ease, transform 120ms ease',
      pointerEvents: 'none',
    },
    '&:hover .react-flow__minimap': {
      opacity: 1,
      transform: 'scale(1)',
      pointerEvents: 'auto',
    },
  },
  selectedTaskPreviewCta: {
    margin: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM} 0`,
    padding: tokens.spacingVerticalS,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke1}`,
  },
  selectedTaskPreviewPending: {
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    border: `1px solid ${tokens.colorPaletteMarigoldBorderActive}`,
  },
  selectedTaskPreviewUnavailable: {
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  previewStatusStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  previewStatusReason: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  runTreePreviewPill: {
    display: 'inline-flex',
    alignItems: 'center',
    width: 'fit-content',
    marginLeft: tokens.spacingHorizontalXS,
    padding: '1px 7px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundInverted,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
  },
  runTreePreviewPillPending: {
    backgroundColor: tokens.colorPaletteMarigoldBorderActive,
  },
  runTreePreviewPillUnavailable: {
    backgroundColor: tokens.colorNeutralForeground3,
  },
  topologyDag: {
    position: 'relative',
    flex: 1,
    height: 'auto',
    minHeight: '480px',
    overscrollBehavior: 'contain',
    '@media (max-width: 640px)': {
      minHeight: '360px',
    },
  },
  topologyPanelBody: {
    minHeight: 0,
    overflowY: 'hidden',
  },
  topologyInspector: {
    flex: 1,
    minHeight: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  topologyInspectorSummary: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
});

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

// Flatten the run tree into display rows. Depth is derived from the ACTUAL parent/child
// nesting level (recursion depth), NOT from graph column position. This prevents the
// cascading-staircase regression where sequential sibling rows each indented one level
// deeper than the last.
function flattenRunTree(nodes: RunSessionTree[], depth = 0): RunSessionTree[] {
  return nodes.flatMap((node) => [
    { ...node, depth },
    ...flattenRunTree(node.children, depth + 1),
  ]);
}

const SESSION_TREE_STAGE_RANK: Record<string, number> = {
  outcome_plan: 10,
  work_plan: 20,
  subtask: 30,
  rai: 40,
  build_test: 50,
  review: 60,
  merge: 70,
  scribe: 80,
  rubberduck: 55,
};

function subtaskSortKey(nodeId: string): string {
  const match = /(?:^|:)subtask-(\d+)\b/i.exec(nodeId);
  if (!match) return nodeId;
  return Number(match[1]).toString().padStart(12, '0');
}

function sessionTreeRoleRank(meta: { nodeId: string; label: string; roleKey?: string; isSubtask: boolean; y: number; x: number }): number {
  if (meta.isSubtask) return SESSION_TREE_STAGE_RANK.subtask;
  const role = meta.roleKey?.toLowerCase();
  if (role && SESSION_TREE_STAGE_RANK[role] != null) return SESSION_TREE_STAGE_RANK[role];
  const key = `${meta.nodeId} ${meta.label}`.toLowerCase();
  if (key.includes('work-plan') || key.includes('work plan')) return SESSION_TREE_STAGE_RANK.work_plan;
  if (key.includes('outcome-plan') || key.includes('outcome plan')) return SESSION_TREE_STAGE_RANK.outcome_plan;
  if (key.includes('build') && key.includes('test')) return SESSION_TREE_STAGE_RANK.build_test;
  if (/\brai\b/.test(key)) return SESSION_TREE_STAGE_RANK.rai;
  if (key.includes('human review') || key.includes('review')) return SESSION_TREE_STAGE_RANK.review;
  if (key.includes('merge')) return SESSION_TREE_STAGE_RANK.merge;
  if (key.includes('scribe')) return SESSION_TREE_STAGE_RANK.scribe;
  return 100;
}

export interface RunTreeSiblingMeta {
  nodeId: string;
  label: string;
  roleKey?: string;
  isSubtask: boolean;
  startedAt?: number;
  order: number;
  x: number;
  y: number;
}

// Sibling ordering for the run/session tree. The tree order is DECOUPLED from both wall-clock
// timestamps and graph layout so it never reshuffles when work starts at different times or when
// the graph orientation/tidy/fit changes (Ahmed: "the run tree is all over the place, it is not
// sorted properly at all"). PRIMARY key is the canonical pipeline stage rank
// (Outcome plan → Work plan → subtasks → RAI → Build & Test → Human Review → Merge → Scribe) so the
// tree always reads in dependency order regardless of the order events/descriptor nodes arrive in.
// `order` (descriptor emission index) is a stable tiebreaker for same-rank nodes; numeric subtask
// key keeps subtasks in ascending order; label/nodeId make remaining ties deterministic. NOTE: we
// intentionally do NOT sort by startedAt or by layout x/y — both caused the run tree to jump around.
export function compareRunTreeSiblings(a: RunTreeSiblingMeta, b: RunTreeSiblingMeta): number {
  return sessionTreeRoleRank(a) - sessionTreeRoleRank(b)
    || subtaskSortKey(a.nodeId).localeCompare(subtaskSortKey(b.nodeId), undefined, { numeric: true })
    || (a.order - b.order)
    || a.label.localeCompare(b.label)
    || a.nodeId.localeCompare(b.nodeId);
}

function runTreeStatusIcon(status: string) {
  const color = semanticStateColorForStatus(status);
  if (color === 'success') return <CheckmarkRegular aria-hidden="true" />;
  if (color === 'danger') return <DismissRegular aria-hidden="true" />;
  if (color === 'running') return <Spinner size="extra-tiny" aria-label="Running" />;
  if (color === 'input') return <ClockRegular aria-hidden="true" />;
  return <CircleRegular aria-hidden="true" />;
}

type SemanticStateColor = 'running' | 'success' | 'danger' | 'input' | 'queued';

function semanticStateColorForStatus(status: string | undefined): SemanticStateColor {
  switch (status) {
    case 'drafting_outcome':
    case 'planning':
    case 'running':
    case 'dispatched':
    case 'dispatching':
    case 'in_progress':
    case 'awaiting_assembly':
    case 'assembling':
    case 'merging':
      return 'running';
    case 'completed':
    case 'complete':
    case 'merged':
    case 'assemble_ready':
    case 'confirmed':
    case 'done':
    case 'success':
      return 'success';
    case 'failed':
    case 'merge_failed':
    case 'declined':
    case 'error':
      return 'danger';
    case 'awaiting_confirmation':
    case 'awaiting_review':
    case 'in_review':
    case 'revising':
    case 'needs_clarification':
    case 'needs_resolution':
    case 'rai_flagged':
    case 'blocked':
    case 'manual_gate':
    case 'approval_required':
      return 'input';
    default:
      return 'queued';
  }
}

function semanticStateColorForBucket(bucket: CoordinatorRunBucket): SemanticStateColor {
  switch (bucket) {
    case 'running': return 'running';
    case 'completed': return 'success';
    case 'failed': return 'danger';
    case 'waiting':
    case 'blocked': return 'input';
    default: return 'queued';
  }
}

function runTreeStatusLabel(status: string, confirmedBy?: string): string {
  switch (status) {
    case 'drafting_outcome': return 'Drafting outcome plan';
    case 'planning': return 'Planning';
    case 'awaiting_confirmation': return 'Awaiting confirmation';
    case 'confirmed': return confirmedBy ? `Confirmed by ${confirmedBy}` : 'Confirmed';
    case 'needs_clarification': return 'Needs clarification';
    case 'needs_resolution': return 'Needs resolution';
    case 'pending_capacity': return 'Waiting for capacity';
    case 'blocked': return 'Blocked';
    case 'completed': return 'Completed';
    case 'assemble_ready': return 'Ready for assembly';
    case 'awaiting_review': return 'Action needed';
    case 'revising': return 'Changes requested — revising';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'failed': return 'Failed';
    case 'merge_failed': return 'Merge failed';
    case 'running': return 'Running';
    case 'pending': return 'Pending';
    default: return status ? status.replace(/_/g, ' ') : 'Pending';
  }
}

const FAILED_TASK_STATUSES = new Set(['failed', 'merge_failed', 'declined']);
const BLOCKED_TASK_STATUSES = new Set(['blocked', 'rai_flagged', 'needs_clarification', 'pending_capacity', 'needs_resolution']);
const WAITING_TASK_STATUSES = new Set(['waiting', 'awaiting_confirmation', 'revising']);
const PENDING_TASK_STATUSES = new Set(['pending']);
const EXECUTING_TASK_STATUSES = new Set(['drafting_outcome', 'planning', 'running', 'dispatched', 'dispatching', 'in_progress', 'awaiting_assembly', 'assembling']);

function summarizeCoordinatorChildren(nodes: RunSessionTree[]): string | null {
  if (nodes.length === 0) return null;
  const counts = nodes.reduce(
    (acc, node) => {
      if (FAILED_TASK_STATUSES.has(node.status)) acc.failed += 1;
      else if (node.status === 'assemble_ready') acc.ready += 1;
      else if (node.status === 'completed' || node.status === 'merged') acc.done += 1;
      else if (BLOCKED_TASK_STATUSES.has(node.status)) acc.blocked += 1;
      else if (WAITING_TASK_STATUSES.has(node.status)) acc.waiting += 1;
      else if (PENDING_TASK_STATUSES.has(node.status)) acc.pending += 1;
      else if (EXECUTING_TASK_STATUSES.has(node.status)) acc.running += 1;
      else acc.pending += 1;
      return acc;
    },
    { running: 0, ready: 0, blocked: 0, waiting: 0, failed: 0, done: 0, pending: 0 },
  );
  const parts = [
    counts.running > 0 ? `${counts.running} running` : null,
    counts.ready > 0 ? `${counts.ready} ready` : null,
    counts.blocked > 0 ? `${counts.blocked} blocked` : null,
    counts.waiting > 0 ? `${counts.waiting} waiting` : null,
    counts.failed > 0 ? `${counts.failed} failed` : null,
    counts.done > 0 ? `${counts.done} done` : null,
    counts.pending > 0 ? `${counts.pending} pending` : null,
  ].filter((part): part is string => part !== null);
  return parts.length > 0 ? parts.join(', ') : `${nodes.length} agents`;
}

function graphEmptyCopy(
  isConnecting: boolean,
  noWorkPlan: boolean,
  graphError: FormattedApiError | null,
  viewState: CoordinatorRunViewState,
) {
  if (graphError) {
    return {
      title: graphError.kind === 'not-found' ? 'Graph has not been emitted yet' : 'Run graph could not be loaded',
      body: graphError.kind === 'not-found'
        ? 'The coordinator run exists, but the saved graph endpoint has not produced a descriptor yet. Keep the stream open or refresh after the coordinator emits the graph.'
        : `${graphError.message}${graphError.detail ? ` ${graphError.detail}` : ''}`,
    };
  }
  if (isConnecting) {
    return {
      title: 'Connecting to the coordinator stream',
      body: 'Agentweaver is opening the live event feed. The graph will populate from the stream or the saved run graph as soon as either source responds.',
    };
  }
  if (noWorkPlan) {
    return {
      title: viewState.terminal ? 'No saved work plan for this run' : 'Work plan is not available yet',
      body: viewState.terminal
        ? 'The run is no longer active and no work plan was saved. Review the run status and coordinator messages for the failure reason.'
        : 'The coordinator has not produced a saved work plan for this run yet. The page will keep retrying while the run is in progress.',
    };
  }
  return {
    title: 'Waiting for the run graph',
    body: 'The page is listening for coordinator graph events and checking the saved graph. If this does not resolve, refresh the run or inspect the coordinator messages for an error.',
  };
}

const useTopologyToolbarStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    alignSelf: 'flex-start',
    padding: tokens.spacingHorizontalXXS,
    marginBottom: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
  },
  readout: {
    minWidth: '44px',
    textAlign: 'center',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
  },
  divider: {
    width: '1px',
    alignSelf: 'stretch',
    marginTop: '3px',
    marginBottom: '3px',
    marginLeft: tokens.spacingHorizontalXXS,
    marginRight: tokens.spacingHorizontalXXS,
    backgroundColor: tokens.colorNeutralStroke2,
  },
});

interface TopologyToolbarProps {
  orientation: 'LR' | 'TB';
  onToggleOrientation: () => void;
  onTidy: () => void;
  fitPadding: number;
}

// Copilot Studio-style control bar for the topology overlay. Lives inside a ReactFlowProvider so it
// can drive the shared viewport (zoom in/out, fit) natively. Tidy re-runs the dagre layout and
// re-fits; Switch orientation toggles LR/TB rank direction (both re-fit on next render via the
// keyed ReactFlow remount + its fitView prop).
function TopologyToolbar({ orientation, onToggleOrientation, onTidy, fitPadding }: TopologyToolbarProps) {
  const toolbarStyles = useTopologyToolbarStyles();
  const { zoomIn, zoomOut, fitView } = useReactFlow();
  const zoom = useStore((s) => s.transform[2]);
  const zoomPct = Math.round((zoom ?? 1) * 100);
  return (
    <div className={toolbarStyles.bar} role="toolbar" aria-label="Topology graph controls" data-testid="topology-toolbar">
      <Tooltip content="Zoom out" relationship="label" withArrow>
        <Button appearance="subtle" size="small" icon={<ZoomOutRegular />} onClick={() => zoomOut({ duration: 200 })} />
      </Tooltip>
      <Text className={toolbarStyles.readout} aria-hidden>{zoomPct}%</Text>
      <Tooltip content="Zoom in" relationship="label" withArrow>
        <Button appearance="subtle" size="small" icon={<ZoomInRegular />} onClick={() => zoomIn({ duration: 200 })} />
      </Tooltip>
      <span className={toolbarStyles.divider} aria-hidden />
      <Tooltip content="Fit to view" relationship="label" withArrow>
        <Button appearance="subtle" size="small" icon={<ScaleFitRegular />} onClick={() => fitView({ padding: fitPadding, duration: 200 })} />
      </Tooltip>
      <Tooltip content="Tidy" relationship="label" withArrow>
        <Button appearance="subtle" size="small" icon={<BroomRegular />} onClick={onTidy} />
      </Tooltip>
      <Tooltip content="Switch orientation" relationship="label" withArrow>
        <Button
          appearance="subtle"
          size="small"
          icon={orientation === 'LR' ? <ArrowAutofitHeightRegular /> : <ArrowAutofitWidthRegular />}
          onClick={onToggleOrientation}
        />
      </Tooltip>
    </div>
  );
}

// Cinematic zoom-to-node: registers imperative viewport helpers on the shared ref so the parent's
// onNodeClick can glide onto a clicked node (in addition to selecting it) and onPaneClick can glide
// back out to the whole graph. Lives inside the ReactFlowProvider so it can reach the native viewport
// API (`setCenter` / `fitView`).
type TopologyViewportApi = { centerOnNode: (node: Node) => void; fitAll: () => void };

function TopologyViewportController({
  apiRef,
  fitPadding,
}: {
  apiRef: RefObject<TopologyViewportApi | null>;
  fitPadding: number;
}) {
  const { setCenter, fitView } = useReactFlow();
  useEffect(() => {
    const prefersReducedMotion = () =>
      window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false;
    apiRef.current = {
      centerOnNode: (node: Node) => {
        const w = node.measured?.width ?? node.initialWidth ?? COMPACT_NODE_W;
        const h = node.measured?.height ?? node.initialHeight ?? COMPACT_NODE_H;
        const cx = node.position.x + w / 2;
        const cy = node.position.y + h / 2;
        // Comfortable zoom-in that still reveals neighbours/edges; instant if reduced motion is set.
        setCenter(cx, cy, { zoom: 1.3, duration: prefersReducedMotion() ? 0 : 600 });
      },
      // Cinematic reverse of centerOnNode: glide back out to the whole graph (same ease/duration).
      fitAll: () => {
        fitView({ padding: fitPadding, duration: prefersReducedMotion() ? 0 : 600 });
      },
    };
    return () => {
      apiRef.current = null;
    };
  }, [setCenter, fitView, apiRef, fitPadding]);
  return null;
}

export function CoordinatorRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();
  const navigate = useNavigate();
  // Actual run-level RunStatus (distinct from the WorkPlan/orchestration phase). A run can be
  // terminally Failed/Declined at the run level while its WorkPlan.Status is still `in_review`
  // (e.g. a run interrupted by an old build before the durability fix): the in-memory assembly
  // gate is NOT armed, so showing an actionable review bar would 409. We use this to suppress the
  // review affordance for a terminal run and show its failure reason instead.
  const [runLevelStatusState, setRunLevelStatusState] = useState<{ runId: string; status: RunStatus | undefined }>({
    runId: '',
    status: undefined,
  });
  const runLevelStatus = runLevelStatusState.runId === (runId ?? '') ? runLevelStatusState.status : undefined;
  const setRunLevelStatus = useCallback((status: RunStatus | undefined) => {
    setRunLevelStatusState({ runId: runId ?? '', status });
  }, [runId]);

  const {
    events,
    liveEvents,
    droppedEventCount,
    status: streamStatus,
    error: streamError,
    reconnect: reconnectStream,
  } = useSeededRunStream(runId ?? '', runLevelStatus);
  const artifactsLiveUpdateKey = liveEvents[liveEvents.length - 1]?.sequence ?? liveEvents.length;

  // Topology graph orientation (dagre rank direction). LR = horizontal (default), TB = vertical.
  // The toolbar's "Switch orientation" toggles this and re-fits the view.
  const [graphOrientation, setGraphOrientation] = useState<'LR' | 'TB'>('LR');
  // True once the user manually toggles orientation via the toolbar — suppresses the auto-pick so
  // their explicit choice sticks. Reset when the topology panel closes so reopening re-evaluates.
  const [orientationUserChose, setOrientationUserChose] = useState(false);
  // Measured topology-graph container size (from a ResizeObserver on the canvas wrapper). Drives the
  // fill-maximizing default-orientation pick. Null until first measured.
  const [topoContainerSize, setTopoContainerSize] = useState<{ w: number; h: number } | null>(null);
  const topoContainerRef = useRef<HTMLDivElement | null>(null);
  // Bumped by "Tidy" to force a fresh dagre layout + re-fit even when inputs are unchanged.
  const [tidyNonce, setTidyNonce] = useState(0);

  // REST seed: coordinator GraphDescriptor (GET /api/runs/{id}/graph, coordinator variant).
  const [restDescriptor, setRestDescriptor] = useState<GraphDescriptor | null>(null);
  const [graphError, setGraphError] = useState<FormattedApiError | null>(null);
  const [runLoadError, setRunLoadError] = useState<FormattedApiError | null>(null);
  const [workPlanError, setWorkPlanError] = useState<FormattedApiError | null>(null);

  // Topology seed from work plan + children (for subtask status projection).
  const [topoSeed, setTopoSeed] = useState(initialTopologyState);

  // Agent name -> role title, fetched from the project team roster, so a subtask card can show the
  // assigned agent's ROLE (e.g. "Repo Auditor") and not just their cast name (e.g. "Deckard").
  const [roleByAgent, setRoleByAgent] = useState<Record<string, string>>({});
  const [projectName, setProjectName] = useState('Project');

  // ---------------------------------------------------------------------------
  // Orchestration lifecycle poll (issues 3 & 4). Reads the coordinator_status field
  // (added by the backend concurrently — optional) plus the work-plan status, both
  // tolerated as absent. Polls until the orchestration reaches a terminal phase.
  // ---------------------------------------------------------------------------
  const [coordStatusField, setCoordStatusField] = useState<string | undefined>(undefined);
  const [coordStatusReason, setCoordStatusReason] = useState<string | undefined>(undefined);
  const [coordinatorSteerable, setCoordinatorSteerable] = useState<boolean | undefined>(undefined);
  const [workPlanStatus, setWorkPlanStatus] = useState<string | undefined>(undefined);
  // Per-run work-plan snapshot.
  const [workPlanData, setWorkPlanData] = useState<WorkPlanResponse | null>(null);

  // Sandbox preview port-forward state.
  const [previewDialogOpen, setPreviewDialogOpen] = useState(false);
  const [previewTargetPort, setPreviewTargetPort] = useState('3000');
  const [previewSession,    setPreviewSession]    = useState<PortForwardSessionDto | undefined>(undefined);
  const [previewSessions,   setPreviewSessions]   = useState<PortForwardSessionDto[]>([]);
  const [previewBusy,       setPreviewBusy]       = useState(false);
  const [previewError,      setPreviewError]      = useState<string | undefined>(undefined);

  // True once the work-plan endpoint has confirmed a 404 (run has no plan yet / is stuck).
  // Used to render a graceful empty state and to back off the lifecycle poll so the page
  // doesn't hammer the 404 endpoint on a tight loop.
  const [noWorkPlan, setNoWorkPlan] = useState(false);
  // True when the run detail confirms this is a child run (parent_run_id non-null). Child runs
  // never have a work-plan or outcome-plan; skip coordinator-only artifact fetches entirely.
  const [isChildRun, setIsChildRun] = useState(false);
  // Retry state for the header button.
  const [retrying, setRetrying] = useState(false);
  const [retryError, setRetryError] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [stopError, setStopError] = useState<string | null>(null);
  const [stopConfirmationOpen, setStopConfirmationOpen] = useState(false);
  // Per-run option toggles (autopilot + auto-approve-tools). Seeded once from the run detail,
  // then driven by user toggles (optimistic). Both cascade to the coordinator's children.
  const [autopilot, setAutopilot] = useState(false);
  const [autoApprove, setAutoApprove] = useState(false);
  const [autopilotBusy, setAutopilotBusy] = useState(false);
  const [autoApproveBusy, setAutoApproveBusy] = useState(false);
  const [automationError, setAutomationError] = useState<string | null>(null);
  const [tokenBreakdown, setTokenBreakdown] = useState<RunAgentTokenBreakdownDto | null>(null);
  const seededToggles = useRef(false);


  useEffect(() => {
    if (!projectId) {
      queueMicrotask(() => setProjectName('Project'));
      return;
    }
    let cancelled = false;
    queueMicrotask(() => setProjectName('Project'));
    apiClient.getProject(projectId)
      .then((project) => {
        if (!cancelled) setProjectName(project.name?.trim() || 'Project');
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [projectId]);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;

    // Fetch graph descriptor for REST seed (so finished coordinator runs still render).
    queueMicrotask(() => {
      setRestDescriptor(null);
      setGraphError(null);
    });
    apiClient.getRunGraph(runId)
      .then((desc) => {
        if (cancelled) return;
        setRestDescriptor(desc);
        setGraphError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        setGraphError(formatApiError(err, 'The saved run graph could not be loaded.'));
      });

    // Fetch work plan + children for topology status seed. Skip for child runs —
    // work-plan is a coordinator-only artifact and child runs will never have one.
    void (async () => {
      const runDetail = await apiClient.getRun(runId).catch((err) => {
        if (!cancelled) setRunLoadError(formatApiError(err, 'The run could not be loaded.'));
        return null;
      });
      if (cancelled) return;
      if (runDetail) setRunLoadError(null);
      if (runDetail?.parent_run_id != null) {
        setIsChildRun(true);
        return;
      }
      const [workPlan, children] = await Promise.all([
        apiClient.getWorkPlan(runId).catch((err) => {
          if (!(err instanceof ApiError && err.status === 404) && !cancelled) {
            setWorkPlanError(formatApiError(err, 'The work plan could not be loaded.'));
          }
          return null;
        }),
        apiClient.getCoordinatorChildren(runId).catch(() => null),
      ]);
      if (cancelled) return;
      if (workPlan) {
        setWorkPlanError(null);
        setNoWorkPlan(false);
        setTopoSeed(seedTopologyFromWorkPlan(workPlan, children));
        setWorkPlanData(workPlan);
      }
    })();

    return () => { cancelled = true; };
  }, [runId]);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    let consecutiveFailures = 0;
    const loadBreakdown = async () => {
      try {
        const next = await apiClient.getRunTokenBreakdown(runId);
        if (!cancelled) {
          consecutiveFailures = 0;
          setTokenBreakdown(next);
        }
      } catch {
        if (!cancelled) {
          consecutiveFailures += 1;
          setTokenBreakdown(null);
          if (consecutiveFailures >= 3) {
            clearInterval(handle);
          }
        }
      }
    };
    const handle: ReturnType<typeof setInterval> = setInterval(() => { void loadBreakdown(); }, 30000);
    void loadBreakdown();
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [runId]);


  // Fetch the project team once to resolve each assigned agent's role title for the subtask cards.
  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getTeam(projectId)
      .then((team) => {
        if (cancelled) return;
        const map: Record<string, string> = {};
        for (const m of team.members ?? []) {
          if (m.name && m.role_title) map[m.name] = m.role_title;
        }
        setRoleByAgent(map);
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [projectId]);


  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const TERMINAL = new Set<OrchPhase>(['complete', 'failed', 'blocked', 'declined']);
    queueMicrotask(() => {
      setRunLoadError(null);
      setWorkPlanError(null);
      setNoWorkPlan(false);
      setRunLevelStatus(undefined);
      setCoordStatusField(undefined);
      setCoordStatusReason(undefined);
      setCoordinatorSteerable(undefined);
      setWorkPlanStatus(undefined);
      setWorkPlanData(null);
      setPreviewSessions([]);
      setIsChildRun(false);
      seededToggles.current = false;
    });

    const tick = async () => {
      let detail: Awaited<ReturnType<typeof apiClient.getRun>>;
      try {
        detail = await apiClient.getRun(runId);
      } catch (err) {
        if (cancelled) return;
        setRunLoadError(formatApiError(err, 'The run could not be loaded.'));
        if (err instanceof ApiError && (err.status === 404 || err.status === 401 || err.status === 403)) return;
        timer = setTimeout(() => { void tick(); }, 8000);
        return;
      }
      if (cancelled) return;
      setRunLoadError(null);
      // Child runs (parent_run_id non-null) are not coordinator runs and will never have a
      // work-plan or outcome-plan. Skip coordinator-only artifact fetches to avoid 404 noise.
      const childRun = detail?.parent_run_id != null;
      setIsChildRun(childRun);
      let wp: WorkPlanResponse | null = null;
      let workPlanFailed = false;
      if (!childRun) {
        try {
          wp = await apiClient.getWorkPlan(runId);
          const children = await apiClient.getCoordinatorChildren(runId).catch(() => null);
          if (cancelled) return;
          setNoWorkPlan(false);
          setWorkPlanError(null);
          setWorkPlanData(wp);
          setTopoSeed(seedTopologyFromWorkPlan(wp, children));
        } catch (err) {
          if (cancelled) return;
          if (err instanceof ApiError && err.status === 404) {
            setNoWorkPlan(true);
            setWorkPlanError(null);
          } else {
            workPlanFailed = true;
            setWorkPlanError(formatApiError(err, 'The work plan could not be loaded.'));
          }
          wp = null;
        }
      } else {
        setNoWorkPlan(false);
        setWorkPlanError(null);
      }
      if (cancelled) return;
      const statusField = detail?.coordinator_status ?? undefined;
      const reasonField = detail?.coordinator_status_reason ?? undefined;
      const wpStatus = wp?.status ?? undefined;
      setCoordStatusField(statusField);
      setCoordStatusReason(reasonField);
      setCoordinatorSteerable(typeof detail?.coordinator_steerable === 'boolean' ? detail.coordinator_steerable : undefined);
      setWorkPlanStatus(wpStatus);
      setRunLevelStatus(detail?.status ?? undefined);
      // Seed the option toggles once from the run detail; subsequent user toggles own the state.
      if (!seededToggles.current && detail) {
        setAutopilot(Boolean(detail.autopilot));
        setAutoApprove(Boolean(detail.auto_approve_tools));
        seededToggles.current = true;
      }
      // Stop polling when the run-level status is already terminal even if the orchestration
      // coordinator_status field is absent (e.g., a run interrupted before emitting a terminal status).
      if (isTerminalRunStatus(detail?.status ? String(detail.status) : undefined)) return;
      const phase = normalizePhase(statusField) !== 'unknown'
        ? normalizePhase(statusField)
        : normalizePhase(wpStatus);
      if (!TERMINAL.has(phase)) timer = setTimeout(() => { void tick(); }, workPlanFailed ? 8000 : 4000);
    };

    void tick();
    return () => { cancelled = true; if (timer) clearTimeout(timer); };
  }, [runId, setRunLevelStatus]);

  // Goal is carried by the coordinator.started event.
  const goal = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'coordinator.started' && typeof evt.payload['goal'] === 'string') {
        return evt.payload['goal'] as string;
      }
    }
    return undefined;
  }, [events]);

  // The workflow the coordinator selected/planned this orchestration against. Carried by the
  // coordinator.workflow_selected event, which is persisted to the run event log and replayed on
  // reconnect — so it survives page reloads. Latest event wins.
  const selectedWorkflow = useMemo<{ name: string; auto: boolean; rationale?: string } | undefined>(() => {
    let picked: { name: string; auto: boolean; rationale?: string } | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.workflow_selected') {
        const name = evt.payload['selectedName'] ?? evt.payload['selectedId'];
        if (name != null && String(name).trim() !== '') {
          const rationale = evt.payload['rationale']
            ?? evt.payload['reason']
            ?? evt.payload['selectionReason']
            ?? evt.payload['selection_reason']
            ?? evt.payload['why'];
          picked = {
            name: String(name),
            auto: evt.payload['wasAutoSelected'] === true,
            rationale: rationale != null && String(rationale).trim() !== '' ? String(rationale) : undefined,
          };
        }
      }
    }
    return picked;
  }, [events]);

  // coordinator.graph SSE: highest-seq-wins over REST seed (same pattern as run.workflow_graph).
  const sseDescriptor = useMemo<GraphDescriptor | undefined>(() => {
    let best: { seq: number; desc: GraphDescriptor } | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.graph') {
        const seq = typeof evt.payload['seq'] === 'number' ? evt.payload['seq'] : 0;
        if (!best || seq >= best.seq) {
          best = { seq, desc: evt.payload as unknown as GraphDescriptor };
        }
      }
    }
    return best?.desc;
  }, [events]);

  const effectiveDescriptor: GraphDescriptor | null = sseDescriptor ?? restDescriptor;

  const latestOutcomePlanEvent = useMemo<RunStreamEvent | undefined>(() => {
    let latest: RunStreamEvent | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec' && (!latest || evt.sequence >= latest.sequence)) latest = evt;
    }
    return latest;
  }, [events]);

  const latestOutcomePlanDraftingEvent = useMemo<RunStreamEvent | undefined>(() => {
    let latest: RunStreamEvent | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec.drafting' && (!latest || evt.sequence >= latest.sequence)) latest = evt;
    }
    return latest;
  }, [events]);

  const specConfirmed = useMemo(
    () => events.some((e) => e.type === 'coordinator.outcome_spec.confirmed'),
    [events],
  );

  const outcomePlanConfirmedBy = useMemo(() => {
    let confirmedBy: string | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec.confirmed' && typeof evt.payload['confirmedBy'] === 'string') {
        confirmedBy = evt.payload['confirmedBy'] as string;
      }
    }
    return confirmedBy;
  }, [events]);

  const workPlanSeen = useMemo(
    () => workPlanData != null || events.some((e) => e.type === 'coordinator.work_plan'),
    [events, workPlanData],
  );

  const planningDescriptor = useMemo<GraphDescriptor | null>(() => {
    if (!effectiveDescriptor) return null;
    const base = effectiveDescriptor;

    const nodesById = new Map(base.nodes.map((node) => [node.id, node]));
    const coordinator = nodesById.get('coordinator') ?? base.nodes[0];
    if (!coordinator) return base;

    const outcomeNode: GraphDescriptor['nodes'][number] = {
      id: 'outcome-plan',
      label: 'Outcome plan',
      role: 'outcome_plan',
      kind: latestOutcomePlanDraftingEvent || latestOutcomePlanEvent || specConfirmed || coordStatusField === 'drafting'
        ? 'live'
        : 'planned',
      node_type: 'action',
    };
    const workNode: GraphDescriptor['nodes'][number] = {
      id: 'work-plan',
      label: 'Work plan',
      role: 'work_plan',
      kind: workPlanSeen ? 'live' : 'planned',
      node_type: 'action',
    };

    const originalNodes = base.nodes.filter((node) => node.id !== 'outcome-plan' && node.id !== 'work-plan');
    const planningUnlocked = specConfirmed || workPlanSeen || (!latestOutcomePlanEvent && originalNodes.some((node) => node.node_type === 'subtask'));
    const downstreamNodes = planningUnlocked ? originalNodes.filter((node) => node.id !== coordinator.id) : [];
    const originalDownstreamIds = new Set(downstreamNodes.map((node) => node.id));
    const originalEdges = planningUnlocked
      ? base.edges.filter((edge) => edge.from !== coordinator.id && edge.to !== coordinator.id && originalDownstreamIds.has(edge.from) && originalDownstreamIds.has(edge.to))
      : [];
    const firstDownstreamIds = planningUnlocked
      ? new Set(base.edges.filter((edge) => !edge.loopback && edge.from === coordinator.id).map((edge) => edge.to))
      : new Set<string>();
    if (planningUnlocked && firstDownstreamIds.size === 0) {
      for (const node of downstreamNodes) firstDownstreamIds.add(node.id);
    }

    const nodes = planningUnlocked
      ? [coordinator, outcomeNode, workNode, ...downstreamNodes]
      : [coordinator, outcomeNode];
    const edges: GraphDescriptor['edges'] = [
      { from: coordinator.id, to: 'outcome-plan', cardinality: 'direct', loopback: false },
    ];
    if (planningUnlocked) {
      edges.push({ from: 'outcome-plan', to: 'work-plan', cardinality: 'direct', loopback: false });
      for (const id of firstDownstreamIds) {
        if (id !== 'outcome-plan' && id !== 'work-plan') {
          edges.push({ from: 'work-plan', to: id, cardinality: firstDownstreamIds.size > 1 ? 'fanout' : 'direct', loopback: false });
        }
      }
      edges.push(...originalEdges);
    }

    return { ...base, start_node_id: coordinator.id, nodes, edges };
  }, [effectiveDescriptor, latestOutcomePlanDraftingEvent, latestOutcomePlanEvent, specConfirmed, workPlanSeen, coordStatusField]);

  // Derived orchestration lifecycle (issues 3 & 4).
  const orch = useMemo<OrchState>(
    () => deriveOrchState(events, coordStatusField, coordStatusReason, workPlanStatus),
    [events, coordStatusField, coordStatusReason, workPlanStatus],
  );
  const viewState = useMemo(
    () => deriveCoordinatorRunViewState(runLevelStatus, orch, runLoadError),
    [runLevelStatus, orch, runLoadError],
  );
  const runStatusColor = semanticStateColorForBucket(viewState.bucket);
  // Derive sandbox backend from sandbox.selected events for the Preview Sandbox button.
  const sandboxBackend = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'sandbox.selected') {
        const backend = evt.payload['backend'] ?? evt.payload['Backend'];
        if (backend) return String(backend);
      }
    }
    return undefined;
  }, [events]);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    apiClient.listPortForwards(runId)
      .then((sessions) => {
        if (cancelled) return;
        setPreviewSessions(sessions);
        setPreviewSession((current) => {
          if (current && sessions.some((session) => session.session_id === current.session_id)) return current;
          return sessions.find((session) => previewUrlFromSession(session)) ?? sessions[0];
        });
      })
      .catch(() => {
        if (!cancelled) setPreviewSessions([]);
      });
    return () => { cancelled = true; };
  }, [runId, events.length]);

  const runPreviewState = useMemo(() => latestPreviewStateFromEvents(events), [events]);
  const activePreviewSession = previewSession ?? previewSessions.find((session) => previewUrlFromSession(session)) ?? previewSessions[0];
  const activePreviewUrl = runPreviewState.status === 'ready' ? runPreviewState.previewUrl : null;

  // Coordinator graph node status override so it never shows a stale "Pending".
  const coordNodeStatusOverride = orchPhaseToTopoStatus(orch.phase)
    ?? (viewState.bucket === 'failed' || viewState.bucket === 'blocked'
      ? 'failed'
      : viewState.bucket === 'completed'
        ? 'completed'
        : undefined);

  // Topology state for subtask status projection.
  const topology = useMemo(
    () => buildTopologyState(events, topoSeed),
    [events, topoSeed],
  );

  // Per-subtask elapsed timing, derived from the subtask.* coordinator events (which carry a
  // timestamp_utc). Keyed by the raw subtaskId string. startedAt = first dispatched/running;
  // completedAt = first terminal (completed/failed/assemble_ready/rai_flagged). Drives a live counter
  // on each subtask card so the user can see how long it has been running.
  const subtaskTiming = useMemo<Record<string, { startedAt?: number; completedAt?: number }>>(() => {
    const STARTED = new Set(['subtask.dispatched', 'subtask.running', 'subtask.pending_capacity']);
    const TERMINAL = new Set(['subtask.completed', 'subtask.failed', 'subtask.assemble_ready', 'subtask.rai_flagged']);
    const map: Record<string, { startedAt?: number; completedAt?: number }> = {};
    for (const evt of events) {
      if (!STARTED.has(evt.type) && !TERMINAL.has(evt.type)) continue;
      const sid = evt.payload['subtaskId'];
      if (sid == null) continue;
      const key = String(sid);
      const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
      const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
      if (isNaN(tsMs)) continue;
      const cur = map[key] ?? {};
      if (STARTED.has(evt.type)) {
        cur.startedAt = cur.startedAt === undefined ? tsMs : Math.min(cur.startedAt, tsMs);
      } else {
        cur.completedAt = cur.completedAt === undefined ? tsMs : Math.max(cur.completedAt, tsMs);
      }
      map[key] = cur;
    }
    return map;
  }, [events]);

  // Per-assembly-stage elapsed timing (RAI / Review / Merge / Scribe), derived from the
  // coordinator.assembly_* events (which now carry timestamp_utc). Keyed by node ROLE so it can be
  // injected into the generic assembly node state the same way subtaskTiming feeds subtask cards —
  // giving each collective-assembly stage a live count-up timer that survives SSE replay/restart.
  const assemblyTiming = useMemo<Record<string, { startedAt?: number; completedAt?: number }>>(() => {
    const STARTED: Record<string, string> = {
      'coordinator.assembly_rai_started': 'rai',
      'coordinator.assembly_merge_started': 'merge',
      'coordinator.assembly_scribe_started': 'scribe',
    };
    const COMPLETED: Record<string, string> = {
      'coordinator.assembly_rai_completed': 'rai',
      'coordinator.assembly_merge_completed': 'merge',
      'coordinator.assembly_merge_failed': 'merge',
      'merge.conflicted': 'merge',
      'coordinator.assembly_scribe_completed': 'scribe',
    };
    const roleForReviewGateEvent = (evt: RunStreamEvent): string | undefined => {
      const gateKind = readGateKind(evt.payload);
      if (gateKind === 'build-test') return 'build_test';
      if (gateKind === 'rubberduck') return 'rubberduck';
      return 'review';
    };
    const map: Record<string, { startedAt?: number; completedAt?: number }> = {};
    for (const evt of events) {
      const reviewGateRole = evt.type === 'coordinator.assembly_review_requested'
        || evt.type === 'coordinator.assembly_review_approved'
        || evt.type === 'coordinator.assembly_changes_requested'
        || evt.type === 'coordinator.assembly_declined'
        ? roleForReviewGateEvent(evt)
        : undefined;
      const startRole = evt.type === 'coordinator.assembly_review_requested'
        ? reviewGateRole
        : STARTED[evt.type];
      const doneRole = evt.type === 'coordinator.assembly_review_approved'
        || evt.type === 'coordinator.assembly_changes_requested'
        || evt.type === 'coordinator.assembly_declined'
        ? reviewGateRole
        : COMPLETED[evt.type];
      const role = startRole ?? doneRole;
      if (!role) continue;
      const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
      const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
      if (isNaN(tsMs)) continue;
      const cur = map[role] ?? {};
      if (startRole) {
        cur.startedAt = cur.startedAt === undefined ? tsMs : Math.min(cur.startedAt, tsMs);
      } else {
        cur.completedAt = cur.completedAt === undefined ? tsMs : Math.max(cur.completedAt, tsMs);
      }
      map[role] = cur;
    }
    return map;
  }, [events]);

  const revisingSubtasks = useMemo<Record<string, { gateLabel: string; feedback?: string }>>(() => {
    let latestChange: { sequence: number; ids: string[]; gateLabel: string; feedback?: string } | null = null;
    let latestGateLabel = gateLabelForKind(undefined);
    for (const evt of events) {
      if (evt.type === 'coordinator.assembly_review_requested') {
        latestGateLabel = gateLabelForKind(readGateKind(evt.payload));
      }
      if (evt.type === 'coordinator.assembly_changes_requested') {
        latestChange = {
          sequence: evt.sequence ?? -1,
          ids: readSubtaskIdsFromPayload(evt.payload).map(normalizeSubtaskId),
          gateLabel: latestGateLabel,
          feedback: readStr(evt.payload, ['feedback']),
        };
      }
    }
    if (!latestChange || latestChange.ids.length === 0) return {};
    const stillRevising = new Set(latestChange.ids);
    const terminal = new Set(['subtask.completed', 'subtask.assemble_ready', 'subtask.failed', 'subtask.rai_flagged']);
    for (const evt of events) {
      if ((evt.sequence ?? -1) <= latestChange.sequence || !terminal.has(evt.type)) continue;
      const id = readStr(evt.payload, ['subtaskId', 'subtask_id']);
      if (id) stillRevising.delete(normalizeSubtaskId(id));
    }
    const result: Record<string, { gateLabel: string; feedback?: string }> = {};
    for (const id of stillRevising) result[id] = { gateLabel: latestChange.gateLabel, feedback: latestChange.feedback };
    return result;
  }, [events]);

  // Which coordinator loopback arc (if any) is currently "lit" blue: the review->coordinator
  // "Request changes" arc while a human-review request-changes wave is re-dispatching, or the
  // rai->coordinator "RAI flags" arc while an RAI flag is looping back. Mirrors the graph's
  // active-edge highlight (ActiveEdgeContext). A loop is active when its triggering event is the
  // most recent one that has not yet been superseded by a fresh assembly review / terminal.
  const activeLoopbackId = useMemo<string | undefined>(() => {
    let changesSeq = -1;
    let raiSeq = -1;
    let supersedeSeq = -1;
    for (const e of events) {
      const seq = e.sequence ?? -1;
      const t = e.type as string;
      if (t === 'coordinator.assembly_changes_requested') {
        changesSeq = Math.max(changesSeq, seq);
      } else if (t === 'subtask.rai_flagged') {
        raiSeq = Math.max(raiSeq, seq);
      } else if (
        (t === 'coordinator.assembly_review_requested' && isHumanReviewGateEvent(e)) ||
        t === 'coordinator.assembly_review_approved' ||
        t === 'coordinator.assembly_completed' ||
        t === 'coordinator.assembly_declined' ||
        t === 'coordinator.assembly_failed' ||
        t === 'coordinator.assembly_blocked' ||
        t === 'merge.conflicted'
      ) {
        supersedeSeq = Math.max(supersedeSeq, seq);
      }
    }
    const reviewActive = changesSeq > supersedeSeq && changesSeq >= raiSeq;
    const raiActive = raiSeq > supersedeSeq && raiSeq > changesSeq;
    if (!reviewActive && !raiActive) return undefined;
    if (!effectiveDescriptor) return undefined;
    const wantRole = reviewActive ? 'review' : 'rai';
    const roleById: Record<string, string> = {};
    for (const n of effectiveDescriptor.nodes) roleById[n.id] = (n.role ?? '').toLowerCase();
    const edge = effectiveDescriptor.edges.find(
      (e) => e.loopback && roleById[e.from] === wantRole,
    );
    return edge ? `${edge.from}-${edge.to}` : undefined;
  }, [events, effectiveDescriptor]);


  const { rfNodes, displayEdges, bboxLR, bboxTB } = useMemo<{ rfNodes: Node[]; displayEdges: Edge[]; bboxLR: { w: number; h: number }; bboxTB: { w: number; h: number } }>(() => {
    if (!planningDescriptor) return { rfNodes: [], displayEdges: [], bboxLR: { w: 0, h: 0 }, bboxTB: { w: 0, h: 0 } };

    const fwdEdges: Edge[] = [];
    const allEdges: Edge[] = [];
    // Role lookup so loopback labels are derived from the SOURCE node's role rather than its
    // exact id (robust across descriptor id schemes). Tank adds two coordinator-level loopbacks:
    // rai->coordinator and review->coordinator (loopback:true, no label field on GraphEdge). Render
    // them as labelled back-edges matching the per-run loopback styling. Falls back gracefully when
    // a descriptor has zero loopbacks (older runs) — the loop simply produces no loopback edges.
    const roleById: Record<string, string> = {};
    for (const n of planningDescriptor.nodes) roleById[n.id] = effectiveGraphRole(n).toLowerCase();
    for (const edge of planningDescriptor.edges) {
      const edgeId = `${edge.from}-${edge.to}`;
      if (edge.loopback) {
        allEdges.push(loopbackEdge(edgeId, edge.from, edge.to, coordinatorLoopbackLabel(roleById[edge.from], edge.from)));
      } else {
        const e = forwardEdge(edgeId, edge.from, edge.to);
        fwdEdges.push(e);
        allEdges.push(e);
      }
    }

    const nodeSizeHints: Record<string, NodeSizeHint> = {};
    const raw: Node[] = planningDescriptor.nodes.map((node) => {
      const nt = node.node_type;
      // Per-node dagre height hints so the staircase packs variable-height nodes tightly. Fixed
      // stage/gate/system nodes are short by default; subtask (agent) nodes get the tall hint below;
      // the Human Review gate gets an expanded hint while it awaits a decision (on-face buttons).
      // Default per-node hint: the NARROW compact card (gate/system/coordinator nodes are icon +
      // title only). The subtask branch below widens+heightens to the tall pill; the model-caption
      // and Human-Review-awaiting cases adjust height further.
      nodeSizeHints[node.id] = {
        width:  FIXED_NODE_W,
        height: FIXED_NODE_H,
      };

      const planned = node.kind === 'planned';
      const terminalStatus = runStatusColor === 'danger' ? 'failed' : 'completed';
      const shouldTerminalizeLiveNodes = viewState.terminal && runStatusColor !== 'success';

      if (nt === 'subtask') {
        // Subtask (agent) nodes are the WIDE, tall pills — avatar + 2-line title + Name(Role) +
        // credits + model caption; hint dagre with the full subtask footprint.
        nodeSizeHints[node.id].width  = COMPACT_NODE_W;
        nodeSizeHints[node.id].height = COMPACT_NODE_H;
        // Subtask node — look up topology status by mapped id.
        const topoNode = resolveSubtaskTopoNode(node.id, topology);
        // Defensive: read display fields from flat props OR nested data map.
        const agentField  = node.agent  ?? (node.data?.['agent']  as string | undefined) ?? topoNode?.assignedAgent;
        const modelField  = node.model  ?? (node.data?.['model']  as string | undefined) ?? topoNode?.selectedModelId;
        const phaseField  = node.phase  ?? (node.data?.['phase']  as string | undefined);
        const childRunId  = readChildRunId(node) ?? topoNode?.childRunId;
        // node.id is "plan:subtask-{id}"; the subtask.* timing map is keyed by the raw "{id}".
        const subtaskKey  = node.id.replace(/^plan:/, '').replace(/^subtask-/, '');
        const timing      = subtaskTiming[subtaskKey];
        const revision = revisingSubtasks[normalizeSubtaskId(subtaskKey)];
        const topoStatus = revision
          ? 'revising'
          : terminalizedStatus(topoNode?.status ?? 'pending', shouldTerminalizeLiveNodes, terminalStatus);
        return {
          id:   node.id,
          type: 'subtask',
          data: {
            graphNodeId:   node.id,
            label:         node.label,
            topoStatus,
            topoNode,
            childGraphRef: node.child_graph_ref,
            childRunId,
            agent:         agentField,
            agentRole:     agentField ? roleByAgent[agentField] : undefined,
            model:         modelField,
            phase:         phaseField,
            projectId:     projectId ?? '',
            startedAt:     timing?.startedAt,
            completedAt:   timing?.completedAt ?? (shouldTerminalizeLiveNodes && isLiveStatus(topoNode?.status) ? Date.now() : undefined),
            executionPodName: topoNode?.executionPodName ?? null,
            revisionGateLabel: revision?.gateLabel,
            revisionFeedback: revision?.feedback,
            dir:           'GRID',
          } as SubtaskNodeData,
          position: { x: 0, y: 0 },
        };
      }

      // Coordinator or collective-assembly node — use generic WorkflowNode. def.key MUST be the
      // node ROLE (not node.id), so WorkflowNode's role-based logic fires: the review gate becomes
      // action-required ("Awaiting your review") and the coordinator keeps its "View session" button.
      const roleKey = effectiveGraphRole(node);
      const coordTopoNode = topology.nodes['coordinator'];

      // Resolve the workflow node's own topology entry (coordinator + assembly stages) so we can
      // surface its agent / model / pod on the pill. This is what makes the height CONTENT-DRIVEN:
      // Coordinator and RAI carry an agent (and model), so they render the TALL card like the
      // subtasks; pure gates (Outcome plan, Work plan, Merge, Scribe) have none and stay compact.
      const wfTopoNode = topology.nodes[node.id] ?? topology.nodes[roleKey];
      const isCoordinatorNode = node.id === 'coordinator';
      const wfAgent = node.agent ?? (node.data?.['agent'] as string | undefined) ?? wfTopoNode?.assignedAgent
        ?? (isCoordinatorNode ? 'Coordinator' : undefined);
      const wfModel = node.model ?? (node.data?.['model'] as string | undefined) ?? wfTopoNode?.selectedModelId;
      const wfPod = wfTopoNode?.executionPodName ?? null;
      // WorkflowNodes are always the SMALL card. When the node HAS a model (data-driven — Coordinator,
      // RAI, Scribe, or any gate carrying a model) it also renders a model caption BELOW the card, so
      // reserve the extra caption room in the layout; nodes with no model stay at the plain compact
      // height. (Human Review awaiting a decision is expanded further below to fit its on-face buttons.)
      if (wfModel) {
        nodeSizeHints[node.id].height = FIXED_NODE_WITH_CAPTION_H;
      }

      // Collective-assembly stage status. Two sources combine: the phase projection
      // (assemblyNodeStatus) covers RAI + the human Review gate, but merge/scribe have no distinct
      // orchestration phase, so their started/completed state is taken from the stage's own
      // timing events. Phase status wins when present (it preserves the review "failed"/decline
      // semantics); timing fills in the merge/scribe window so every stage can go live.
      const isAssemblyRole = roleKey === 'build_test' || roleKey === 'rai' || roleKey === 'review' || roleKey === 'merge' || roleKey === 'scribe';
      const at = isAssemblyRole ? assemblyTiming[roleKey] : undefined;
      const terminalStage = node.terminal_stage ?? readStr(node.data ?? {}, ['terminal_stage', 'terminalStage']);
      const terminalOrParkedPhase = orch.phase === 'failed'
        || orch.phase === 'blocked'
        || orch.phase === 'declined'
        || orch.phase === 'needs_resolution';
      const timingStatus: StepStatus | undefined =
        at?.completedAt !== undefined ? 'completed'
        : at?.startedAt !== undefined ? 'started'
        : undefined;
      const phaseStatus = isAssemblyRole && !(planned && terminalOrParkedPhase && !terminalStage)
        ? assemblyNodeStatus(roleKey, orch.phase, terminalStage)
        : undefined;
      // Timing wins once a stage has actually finished: after the user approves the review (or
      // merge/scribe begin), the orchestration phase can linger on `in_review`, which would
      // otherwise keep the Human Review gate showing "Awaiting your review". A real decline still
      // surfaces via phaseStatus === 'failed', which keeps precedence.
      const assemblyStatus = isAssemblyRole
        ? (phaseStatus === 'failed' ? 'failed'
           : timingStatus === 'completed' ? 'completed'
           : (phaseStatus ?? timingStatus))
        : undefined;

      let nodePlanned = planned;
      let stepStatus: StepStatus;
      if (node.id === 'coordinator') {
        stepStatus = topoStatusToStepStatus(coordNodeStatusOverride ?? coordTopoNode?.status ?? 'unknown');
      } else if (node.id === 'outcome-plan') {
        stepStatus = specConfirmed
          ? 'completed'
          : (latestOutcomePlanEvent || latestOutcomePlanDraftingEvent || coordStatusField === 'drafting')
            ? 'started'
            : 'pending';
      } else if (node.id === 'work-plan') {
        stepStatus = workPlanSeen ? 'completed' : 'pending';
      } else if (assemblyStatus !== undefined) {
        stepStatus = assemblyStatus;
        nodePlanned = false; // the stage has been reached; it is live, not planned
      } else {
        stepStatus = topoStatusToStepStatus(terminalizedStatus(node.status ?? readStr(node.data ?? {}, ['status']), shouldTerminalizeLiveNodes, terminalStatus));
      }
      if (!nodePlanned && shouldTerminalizeLiveNodes && stepStatus === 'started') {
        stepStatus = runStatusColor === 'danger' ? 'failed' : 'completed';
      }

      const st: ExecutorState = nodePlanned
        ? { status: 'pending' }
        : { status: stepStatus };

      // Human Review gate awaiting a decision renders on-face action buttons and grows — reserve the
      // room in the layout so the staircase keeps clear of it. (Matches WorkflowNode's isHumanWaiting.)
      if (roleKey === 'review' && !nodePlanned && stepStatus === 'started') {
        nodeSizeHints[node.id].height = REVIEW_EXPANDED_NODE_H;
      }

      // Feed the stage's wall-clock timing so the generic WorkflowNode renders a live count-up
      // timer (RAI / Review / Merge / Scribe), matching the subtask cards.
      if (at?.startedAt !== undefined) {
        st.startedAt = at.startedAt;
        st.completedAt = at.completedAt;
      }

      const def: ExecutorDef = {
        key:             roleKey,
        label:           node.label,
        roleDescription: roleDescForRole(roleKey),
        Icon:            iconForRole(roleKey),
      };

      return {
        id:   node.id,
        type: 'workflow',
        data: {
          def,
          state:     st,
          isPlanned: nodePlanned,
          nodeType:  nt,
          runId:     runId      ?? '',
          executionId: runId    ?? '',
          projectId:   projectId ?? '',
          // Agent / role / model / pod resolved from the node's topology entry (coordinator +
          // assembly stages). Their presence drives the content-driven TALL vs compact card.
          agentName:      wfAgent,
          agentRoleTitle: wfAgent ? roleByAgent[wfAgent] : undefined,
          modelId:        wfModel,
          executionPodName: wfPod,
          // Carry a child run id only when the descriptor supplies one. Coordinator assembly
          // stages currently report lifecycle/results on the parent stream, so their session
          // activity is scoped by event type in AgentSessionPanel rather than by a fake run id.
          childRunId:    readChildRunId(node),
          childGraphRef: node.child_graph_ref,
          dir:         'GRID',
          previewUrl:  roleKey === 'build_test' ? activePreviewUrl ?? undefined : undefined,
        } as WorkflowNodeData,
        position: { x: 0, y: 0 },
      };
    });

    const staircaseOpts = {
      rankSep: COORD_GRAPH_RANK_SEP,
      nodeSep: COORD_GRAPH_NODE_SEP,
      // Cascade the long mostly-linear spine diagonally so the run uses the panel's height (not
      // just its width). True parallel ranks still fan out; the sequence steps consistently one
      // way (LR ⇒ down-right, TB ⇒ down-right) and never reverses.
      targetAspect: 1.35,
      minStepRanks: 3,
    };
    // Lay out BOTH orientations deterministically so we can (a) render the active one and
    // (b) compare their footprints to auto-pick the orientation that fills the panel best.
    const laidOutLR = layoutDagStaircase(raw, fwdEdges, { ...staircaseOpts, rankdir: 'LR' }, nodeSizeHints);
    const laidOutTB = layoutDagStaircase(raw, fwdEdges, { ...staircaseOpts, rankdir: 'TB' }, nodeSizeHints);
    const laidOutNodes = graphOrientation === 'TB' ? laidOutTB : laidOutLR;
    return {
      rfNodes:      laidOutNodes,
      displayEdges: routeGridEdges(allEdges, laidOutNodes),
      bboxLR:       layoutBBox(laidOutLR, nodeSizeHints),
      bboxTB:       layoutBBox(laidOutTB, nodeSizeHints),
    };
  }, [planningDescriptor, topology, projectId, runId, coordNodeStatusOverride, orch.phase, subtaskTiming, assemblyTiming, roleByAgent, latestOutcomePlanDraftingEvent, latestOutcomePlanEvent, specConfirmed, workPlanSeen, coordStatusField, graphOrientation, tidyNonce, viewState.terminal, runStatusColor, revisingSubtasks, activePreviewUrl]);

  const hasSubtaskNodes = useMemo(
    () => (planningDescriptor?.nodes ?? []).some((n) => n.node_type === 'subtask'),
    [planningDescriptor],
  );
  const outcomePlanDraftingActive = !specConfirmed
    && !latestOutcomePlanEvent
    && !workPlanSeen
    && (orch.phase === 'drafting_outcome' || Boolean(latestOutcomePlanDraftingEvent));
  const inSpecAuthoring = !specConfirmed && !hasSubtaskNodes && (orch.phase === 'unknown' || outcomePlanDraftingActive);

  // While the Coordinator is still drafting the Outcome plan (inSpecAuthoring), the assembly
  // stages (RAI / Human Review / Merge / Scribe) are not yet committed work — no spec confirmed,
  // no subtasks, no orchestration phase. Presenting them as planned pipeline nodes implies a
  // downstream plan that does not exist. Filter them (and edges referencing them) out of the
  // rendered graph until drafting ends, leaving only the live Coordinator node. The descriptor
  // itself is left untouched; this is purely a display-time projection.
  const assemblyNodeIds = useMemo(() => {
    const ids = new Set<string>();
    for (const n of planningDescriptor?.nodes ?? []) {
      const role = effectiveGraphRole(n).toLowerCase();
      if (role === 'rai' || role === 'review' || role === 'merge' || role === 'scribe') ids.add(n.id);
    }
    return ids;
  }, [planningDescriptor]);

  const { displayNodes, displayEdges2 } = useMemo<{ displayNodes: Node[]; displayEdges2: Edge[] }>(() => {
    if (!inSpecAuthoring) return { displayNodes: rfNodes, displayEdges2: displayEdges };
    const filteredNodes = rfNodes.filter((n) => !assemblyNodeIds.has(n.id));
    // Defensive fallback: never render an empty graph box. If filtering would drop every node
    // (e.g. a descriptor with assembly stages but no coordinator node), keep the full graph.
    if (filteredNodes.length === 0) return { displayNodes: rfNodes, displayEdges2: displayEdges };
    const keptIds = new Set(filteredNodes.map((n) => n.id));
    const filteredEdges = displayEdges.filter((e) => keptIds.has(e.source) && keptIds.has(e.target));
    return { displayNodes: filteredNodes, displayEdges2: filteredEdges };
  }, [inSpecAuthoring, rfNodes, displayEdges, assemblyNodeIds]);

  const [outcomePlanClarifying, setOutcomePlanClarifying] = useState(false);
  const pendingApprovalCounts = useMemo(() => pendingApprovalsByRun(events, runId ?? ''), [events, runId]);

  useEffect(() => {
    if (latestOutcomePlanEvent) queueMicrotask(() => setOutcomePlanClarifying(false));
  }, [latestOutcomePlanEvent]);

  const { sessionTree, sessionNodeIds, defaultSessionNodeId } = useMemo<{
    sessionTree: RunSessionTree[];
    sessionNodeIds: Set<string>;
    defaultSessionNodeId: string | null;
  }>(() => {
    const candidates = displayNodes.filter((node) => {
      // Include every planned subtask — not only dispatched ones (with a childRunId) — so the
      // session tree mirrors the full work plan. Planned/pending subtasks show their status but
      // have no streamed conversation until they are dispatched.
      if (node.type === 'subtask') {
        return true;
      }
      const wfData = node.data as WorkflowNodeData | undefined;
      return wfData?.def?.key === 'coordinator'
        || wfData?.def?.key === 'outcome_plan'
        || wfData?.def?.key === 'work_plan'
        || wfData?.def?.key === 'build_test'
        || wfData?.def?.key === 'rai'
        || wfData?.def?.key === 'review'
        || wfData?.def?.key === 'merge'
        || wfData?.def?.key === 'scribe';
    });
    if (candidates.length === 0) {
      return {
        sessionTree: [],
        sessionNodeIds: new Set<string>(),
        defaultSessionNodeId: null,
      };
    }

    // Sibling reading order is derived from the DESCRIPTOR's emission order (planningDescriptor.nodes:
    // [coordinator, outcome-plan, work-plan, ...downstream]) — the same dependency order the graph
    // ranks by — NOT from graph POSITION or the (layout-affected) display node array. This keeps the
    // run tree order/structure completely independent of the graph layout: LR/TB orientation, tidy,
    // and fit never reorder it. `descriptorOrder` is preferred; `orderIndex` (display order) is a
    // defensive fallback for any node not present in the descriptor.
    const descriptorOrder = new Map<string, number>(
      (planningDescriptor?.nodes ?? []).map((n, i) => [n.id, i] as const),
    );
    const orderIndex = new Map<string, number>();
    candidates.forEach((node, index) => orderIndex.set(node.id, index));

    const sessionMeta = new Map<string, {
      nodeId: string;
      label: string;
      agentName?: string;
      agentRole?: string;
      status: string;
      childRunId?: string;
      startedAt?: number;
      completedAt?: number;
      pendingApprovalCount?: number;
      order: number;
      x: number;
      y: number;
      isCoordinator: boolean;
      isSubtask: boolean;
      roleKey?: string;
      model?: string;
    }>();

    for (const node of candidates) {
      const order = descriptorOrder.get(node.id) ?? orderIndex.get(node.id) ?? 0;
      if (node.type === 'subtask') {
        const data = node.data as SubtaskNodeData;
        sessionMeta.set(node.id, {
          nodeId: node.id,
          label: data.label,
          agentName: data.agent,
          agentRole: data.agentRole,
          status: String(data.topoStatus ?? 'pending'),
          childRunId: data.childRunId,
          startedAt: data.startedAt,
          completedAt: data.completedAt,
          pendingApprovalCount: data.childRunId ? pendingApprovalCounts.get(data.childRunId) : 0,
          order,
          x: node.position.x,
          y: node.position.y,
          isCoordinator: false,
          isSubtask: true,
          roleKey: 'subtask',
          model: data.model,
        });
      } else {
        const data = node.data as WorkflowNodeData;
        const roleKey = data.def.key;
        const status =
          roleKey === 'outcome_plan'
            ? (outcomePlanClarifying && !specConfirmed ? 'revising' : specConfirmed ? 'confirmed' : latestOutcomePlanEvent ? 'awaiting_confirmation' : outcomePlanDraftingActive ? 'drafting_outcome' : 'pending')
            : roleKey === 'work_plan'
              ? (workPlanSeen ? 'completed' : 'pending')
              : roleKey === 'review' && orch.phase === 'in_review' && !viewState.terminal
                ? 'awaiting_review'
              : data.state.status === 'started' ? 'running'
                : data.state.status === 'completed' ? 'completed'
                  : data.state.status === 'failed' ? 'failed'
                    : data.state.status === 'revise' ? 'rai_flagged'
                      : 'pending';
        // Only the real coordinator/root node defaults its agent name to "Coordinator".
        // Child workflow/assembly stages leave agentName undefined unless they carry a
        // genuine assignment, so the render-level fallback labels them correctly instead
        // of mislabeling every pipeline stage as the Coordinator.
        const isCoordinatorNode = node.id === 'coordinator' || data.def.key === 'coordinator';
        sessionMeta.set(node.id, {
          nodeId: node.id,
          label: data.def.label,
          agentName: data.agentName ?? (isCoordinatorNode ? 'Coordinator' : undefined),
          agentRole: data.agentRoleTitle ?? data.def.roleDescription,
          status,
          // Preserve a real descriptor-provided sub-run id; otherwise assembly stages intentionally
          // stay on the coordinator stream and AgentSessionPanel filters it to the selected stage.
          childRunId: isCoordinatorNode ? undefined : data.childRunId,
          startedAt: data.state.startedAt,
          completedAt: data.state.completedAt,
          pendingApprovalCount: isCoordinatorNode ? pendingApprovalCounts.get(runId ?? '') : (data.childRunId ? pendingApprovalCounts.get(data.childRunId) : 0),
          order,
          x: node.position.x,
          y: node.position.y,
          isCoordinator: isCoordinatorNode,
          isSubtask: false,
          roleKey,
          model: data.modelId,
        });
      }
    }

    const rootMeta = [...sessionMeta.values()].find((meta) => meta.isCoordinator) ?? [...sessionMeta.values()][0];
    if (!rootMeta) {
      return {
        sessionTree: [],
        sessionNodeIds: new Set<string>(),
        defaultSessionNodeId: null,
      };
    }

    // The coordinator dispatches every subtask directly, so the session tree is flat:
    // one Coordinator root with all subtasks as siblings. Data dependencies between
    // subtasks are shown in the graph, not as session-tree nesting.
    const childIdsByParent = new Map<string, string[]>();
    for (const meta of sessionMeta.values()) {
      if (meta.nodeId === rootMeta.nodeId) continue;
      const list = childIdsByParent.get(rootMeta.nodeId) ?? [];
      list.push(meta.nodeId);
      childIdsByParent.set(rootMeta.nodeId, list);
    }

    const buildTree = (nodeId: string): RunSessionTree => {
      const meta = sessionMeta.get(nodeId)!;
      const children = [...(childIdsByParent.get(nodeId) ?? [])]
        .sort((a, b) => compareRunTreeSiblings(sessionMeta.get(a)!, sessionMeta.get(b)!))
        .map((childId) => buildTree(childId));
      return {
        nodeId: meta.nodeId,
        label: meta.label,
        agentName: meta.agentName,
        agentRole: meta.agentRole,
        status: meta.status,
        childRunId: meta.childRunId,
        startedAt: meta.startedAt,
        completedAt: meta.completedAt,
        pendingApprovalCount: meta.pendingApprovalCount,
        children,
        roleKey: meta.roleKey,
        isSubtask: meta.isSubtask,
        isCoordinator: meta.isCoordinator,
        model: meta.model,
        // Row indent depth is recomputed from real nesting by flattenRunTree; this seed is unused.
        depth: 0,
      };
    };

    return {
      sessionTree: [buildTree(rootMeta.nodeId)],
      sessionNodeIds: new Set(sessionMeta.keys()),
      defaultSessionNodeId: rootMeta.nodeId,
    };
  }, [displayNodes, latestOutcomePlanEvent, orch.phase, outcomePlanClarifying, outcomePlanDraftingActive, pendingApprovalCounts, runId, specConfirmed, viewState.terminal, workPlanSeen]);

  const flatSessionTree = useMemo(() => flattenRunTree(sessionTree), [sessionTree]);
  const taskRows = flatSessionTree.filter((node) => node.nodeId !== defaultSessionNodeId);
  const coordinatorChildSummary = useMemo(
    () => summarizeCoordinatorChildren(taskRows.filter((node) => node.isSubtask)),
    [taskRows],
  );
  const taskStatusSummary = taskRows.reduce(
    (acc, node) => {
      const status = node.status;
      if (FAILED_TASK_STATUSES.has(status)) acc.failed += 1;
      else if (BLOCKED_TASK_STATUSES.has(status)) acc.blocked += 1;
      else if (WAITING_TASK_STATUSES.has(status)) acc.waiting += 1;
      else if (PENDING_TASK_STATUSES.has(status)) acc.pending += 1;
      return acc;
    },
    { pending: 0, waiting: 0, blocked: 0, failed: 0 },
  );
  const hasRunningSessionItem = flatSessionTree.some((node) => node.startedAt !== undefined && node.completedAt === undefined);
  const elapsedNow = useTickingNow(hasRunningSessionItem);
  const earliestStart = flatSessionTree.reduce<number | undefined>(
    (min, node) => (node.startedAt == null ? min : min == null ? node.startedAt : Math.min(min, node.startedAt)),
    undefined,
  );
  const elapsedLabel = earliestStart ? fmtTotal(elapsedNow - earliestStart) : '0s';
  const runStatusText = viewState.label;
  const taskCountsLabel = `${taskRows.length} tasks · ${taskStatusSummary.pending} pending · ${taskStatusSummary.waiting} waiting`;

  // ---------------------------------------------------------------------------
  // Steering chat side panel (#163) — a slide-in chat replaces the old inline steer bar.
  // ---------------------------------------------------------------------------

  const [planPanelOpen, setPlanPanelOpen] = useState(false);
  const [artifactsPanelOpen, setArtifactsPanelOpen] = useState(false);
  // Files chip opens the produced-files browser.
  const [filesPanelOpen, setFilesPanelOpen] = useState(false);
  // Left rail (run tree) collapse — collapsed shrinks the rail to a thin strip so the center
  // Messages surface gets more width. Default expanded.
  const [treeRailCollapsed, setTreeRailCollapsed] = useState(false);
  // Run-wide (coordinator-level) collective-diff summary for the Changes chip above the composer.
  const [runChangesSummary, setRunChangesSummary] = useState<{ files: number; added: number; removed: number } | null>(null);
  const [topologyPanelOpen, setTopologyPanelOpen] = useState(false);

  // Measure the topology graph container so we can auto-pick the fill-maximizing orientation.
  useEffect(() => {
    const el = topoContainerRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const measure = () => {
      const w = el.clientWidth;
      const h = el.clientHeight;
      if (w > 0 && h > 0) {
        setTopoContainerSize((prev) => (prev && prev.w === w && prev.h === h ? prev : { w, h }));
      }
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [topologyPanelOpen]);

  // Reset the manual-override + measurement when the panel closes so each open re-evaluates the
  // default orientation from scratch (deterministic: same run + same container ⇒ same choice).
  useEffect(() => {
    if (!topologyPanelOpen) {
      setOrientationUserChose(false);
      setTopoContainerSize(null);
    }
  }, [topologyPanelOpen]);

  // Auto-pick the DEFAULT orientation to fill the most of the panel. For each staircase footprint the
  // fit scale into the container is min(cw/bw, ch/bh); the larger scale fills more area. We only drive
  // the default here — a manual toolbar toggle sets orientationUserChose and wins from then on. This
  // never touches the run-tree ordering (that is derived from dependency edges, not graph layout).
  useEffect(() => {
    if (orientationUserChose || !topologyPanelOpen) return;
    const size = topoContainerSize;
    if (!size || bboxLR.w <= 0 || bboxTB.w <= 0) return;
    const scaleLR = Math.min(size.w / bboxLR.w, size.h / bboxLR.h);
    const scaleTB = Math.min(size.w / bboxTB.w, size.h / bboxTB.h);
    // Tie-break toward LR (landscape default) when the two fits are effectively equal.
    const best: 'LR' | 'TB' = scaleTB > scaleLR * 1.001 ? 'TB' : 'LR';
    setGraphOrientation((prev) => (prev === best ? prev : best));
  }, [orientationUserChose, topologyPanelOpen, topoContainerSize, bboxLR, bboxTB]);

  const [sessionPanelOpen, setSessionPanelOpen] = useState(true);
  const [panelNodeId, setPanelNodeId] = useState<string | null>(null);
  const [composerFocusSignal, setComposerFocusSignal] = useState(0);
  const lastSelectedOutcomePlanSeqRef = useRef<number | null>(null);

  const openPanelForNode = useCallback((nodeId: string, opts?: { closeTopology?: boolean }) => {
    setPanelNodeId(nodeId);
    setSessionPanelOpen(true);
    // Selecting a session via "View session" should surface it in the run tree immediately —
    // close the topology overlay if it's open instead of leaving it stacked on top. Plain node
    // clicks/selection within the topology graph itself should NOT close the topology panel.
    if (opts?.closeTopology) {
      setTopologyPanelOpen(false);
    }
  }, []);

  // Imperative handle to the full-topology viewport (registered by TopologyViewportController inside
  // the ReactFlowProvider) so a node click can cinematically pan+zoom onto the node.
  const topologyViewportApiRef = useRef<TopologyViewportApi | null>(null);

  const focusOutcomePlanComposer = useCallback(() => {
    setOutcomePlanClarifying(true);
    setPanelNodeId('outcome-plan');
    setSessionPanelOpen(true);
    setComposerFocusSignal((value) => value + 1);
  }, []);

  useEffect(() => {
    if (!latestOutcomePlanEvent || isChildRun) return;
    if (lastSelectedOutcomePlanSeqRef.current === latestOutcomePlanEvent.sequence) return;
    lastSelectedOutcomePlanSeqRef.current = latestOutcomePlanEvent.sequence;
    queueMicrotask(() => {
      setPanelNodeId('outcome-plan');
      setSessionPanelOpen(true);
    });
  }, [isChildRun, latestOutcomePlanEvent]);

  const activePanelNodeId = panelNodeId && sessionNodeIds.has(panelNodeId) ? panelNodeId : defaultSessionNodeId;
  const selectedSessionItem = flatSessionTree.find((node) => node.nodeId === activePanelNodeId) ?? flatSessionTree[0] ?? null;
  const executingSessionItem = useMemo(() => {
    const nonRoot = flatSessionTree.filter((node) => node.nodeId !== defaultSessionNodeId);
    if (viewState.terminal) {
      if (viewState.bucket === 'failed' || viewState.bucket === 'blocked') {
        return nonRoot.find((node) => FAILED_TASK_STATUSES.has(node.status))
          ?? nonRoot.find((node) => BLOCKED_TASK_STATUSES.has(node.status) || WAITING_TASK_STATUSES.has(node.status))
          ?? nonRoot.find((node) => EXECUTING_TASK_STATUSES.has(node.status))
          ?? selectedSessionItem
          ?? flatSessionTree[0]
          ?? null;
      }
      return nonRoot.find((node) => semanticStateColorForStatus(node.status) === 'success')
        ?? nonRoot.find((node) => EXECUTING_TASK_STATUSES.has(node.status))
        ?? selectedSessionItem
        ?? flatSessionTree[0]
        ?? null;
    }
    return nonRoot.find((node) => EXECUTING_TASK_STATUSES.has(node.status))
      ?? nonRoot.find((node) => WAITING_TASK_STATUSES.has(node.status) || BLOCKED_TASK_STATUSES.has(node.status))
      ?? selectedSessionItem
      ?? flatSessionTree[0]
      ?? null;
  }, [defaultSessionNodeId, flatSessionTree, selectedSessionItem, viewState.bucket, viewState.terminal]);
  const executingStateColor = executingSessionItem
    ? semanticStateColorForStatus(executingSessionItem.status)
    : semanticStateColorForBucket(viewState.bucket);
  const executionWorkflowName = selectedWorkflow?.name ?? 'pending';
  const executionWhy = selectedWorkflow?.rationale
    ?? viewState.reason
    ?? (selectedWorkflow
      ? selectedWorkflow.auto
        ? 'Automatically selected by the coordinator'
        : 'Selected for this orchestration'
      : goal
        ? `Goal: ${goal}`
        : orch.phase !== 'unknown'
          ? `Phase: ${orchPhaseLabel(orch.phase)}`
          : `Source: ${viewState.sourceLabel}`);
  const executionKickerLabel = viewState.terminal
    ? runStatusColor === 'danger' ? 'Failed' : 'Finished'
    : viewState.bucket === 'waiting' ? 'Waiting'
      : viewState.bucket === 'pending' ? 'Queued'
        : viewState.bucket === 'blocked' ? 'Blocked'
          : 'Executing';
  const executionDisplayStateColor = viewState.terminal ? runStatusColor : executingStateColor;
  const executionContextReason = runStatusColor === 'danger'
    ? (viewState.reason ?? executionWhy)
    : executionWhy;
  // The workflow-name ⓘ tooltip explains WHY this workflow was SELECTED (the selection rationale),
  // never the failure/status context (that stays on the inline reason line beneath the name).
  const executionWhySelected = selectedWorkflow?.rationale
    ?? (selectedWorkflow
      ? selectedWorkflow.auto
        ? 'Automatically selected by the coordinator'
        : 'Selected for this run'
      : 'Workflow not selected yet');
  const executionReasonShort = executionContextReason && executionContextReason.length <= 60
    ? executionContextReason
    : null;
  const selectedGraphNodeId = selectedSessionItem?.nodeId ?? defaultSessionNodeId;
  const linkedDisplayNodes = useMemo(
    () => displayNodes.map((node) => ({
      ...node,
      selected: node.id === selectedGraphNodeId,
    })),
    [displayNodes, selectedGraphNodeId],
  );

  // A compact signature of the laid-out geometry (node ids + rounded positions + edge endpoints).
  // Folded into the ReactFlow remount key so fitView re-runs whenever the layout bounds change —
  // even when the node/edge COUNTS stay the same (e.g. positions shift, ids/edges swap on a status
  // change). Rounding keeps it stable against sub-pixel jitter.
  const layoutSignature = useMemo(() => {
    let h = 5381;
    const mix = (str: string) => { for (let i = 0; i < str.length; i += 1) h = ((h << 5) + h + str.charCodeAt(i)) | 0; };
    for (const n of displayNodes) mix(`${n.id}:${Math.round(n.position.x)},${Math.round(n.position.y)};`);
    for (const e of displayEdges2) mix(`${e.source}>${e.target}|`);
    return (h >>> 0).toString(36);
  }, [displayNodes, displayEdges2]);

  // Merge "Browse files": route to the project Workspace with the coordinator integration branch
  // selected, so refresh/back preserve the browsed ref and the user lands in the WORK section.
  const browseAssemblyFiles = useCallback(() => {
    if (!projectId || !runId) return;
    const query = new URLSearchParams({
      run: runId,
      ref: `agentweaver/integration/${runId}`,
    });
    navigate(`/projects/${projectId}/workspace?${query.toString()}`);
  }, [navigate, projectId, runId]);

  // Collective-assembly "View execution": RAI/Scribe have their own persisted sub-run streams, so
  // focus that session in the slide-up. For the review node, open the artifacts/review panel where
  // the Approve / Request changes / Decline actions live.
  const viewAssemblyExecution = useCallback((id: string) => {
    if (id.endsWith('-rai') || id.endsWith('-scribe')) openPanelForNode(id);
    else setArtifactsPanelOpen(true);
  }, [openPanelForNode]);

  // Option toggles — optimistic update, revert on error. Both cascade to children server-side.
  const toggleAutopilot = useCallback((next: boolean) => {
    if (!runId || autopilotBusy || !viewState.canToggleAutomation) return;
    setAutomationError(null);
    setAutopilot(next);
    setAutopilotBusy(true);
    apiClient.setAutopilot(runId, next)
      .then((res) => setAutopilot(Boolean(res.autopilot)))
      .catch((err) => {
        setAutopilot(!next);
        setAutomationError(`Autopilot update failed: ${formatApiErrorMessage(err, 'Could not update autopilot.')}`);
      })
      .finally(() => setAutopilotBusy(false));
  }, [runId, autopilotBusy, viewState.canToggleAutomation]);

  const toggleAutoApprove = useCallback((next: boolean) => {
    if (!runId || autoApproveBusy || !viewState.canToggleAutomation) return;
    setAutomationError(null);
    setAutoApprove(next);
    setAutoApproveBusy(true);
    apiClient.setAutoApprove(runId, next)
      .then((res) => setAutoApprove(Boolean(res.auto_approve_tools)))
      .catch((err) => {
        setAutoApprove(!next);
        setAutomationError(`Auto-approve update failed: ${formatApiErrorMessage(err, 'Could not update auto-approve.')}`);
      })
      .finally(() => setAutoApproveBusy(false));
  }, [runId, autoApproveBusy, viewState.canToggleAutomation]);

  const handleRetry = useCallback(async () => {
    if (!runId || !projectId || retrying) return;
    setRetrying(true);
    setRetryError(null);
    setStopError(null);
    try {
      const res = await apiClient.retryRun(runId);
      navigate(`/projects/${projectId}/orchestrations/${res.run_id}`);
    } catch (err) {
      setRetryError(formatApiErrorMessage(err, 'Could not retry this run.'));
      setRetrying(false);
    }
  }, [runId, projectId, retrying, navigate]);

  const handleStopRun = useCallback(async () => {
    if (!runId || stopping) return;
    setStopping(true);
    setStopError(null);
    try {
      await apiClient.steerCoordinator(runId, { kind: 'stop' });
      reconnectStream();
    } catch (err) {
      setStopError(formatApiErrorMessage(err, 'Could not stop this run.'));
    } finally {
      setStopping(false);
    }
  }, [reconnectStream, runId, stopping]);

  const startPreview = () => {
    const port = parseInt(previewTargetPort, 10);
    if (isNaN(port) || port <= 0 || port > 65535) {
      setPreviewError('Enter a valid port number (1–65535).');
      return;
    }
    if (!runId) return;
    setPreviewBusy(true);
    setPreviewError(undefined);
    apiClient.startPortForward(runId, port)
      .then((session) => {
        setPreviewSession(session);
        setPreviewSessions((sessions) => [session, ...sessions.filter((s) => s.session_id !== session.session_id)]);
      })
      .catch((err) => setPreviewError(formatApiErrorMessage(err, 'Could not start the sandbox preview.')))
      .finally(() => setPreviewBusy(false));
  };

  const stopPreview = () => {
    if (!runId || !activePreviewSession) return;
    setPreviewBusy(true);
    apiClient.stopPortForward(runId, activePreviewSession.session_id)
      .then(() => {
        setPreviewSession(undefined);
        setPreviewSessions((sessions) => sessions.filter((s) => s.session_id !== activePreviewSession.session_id));
      })
      .catch((err) => setPreviewError(formatApiErrorMessage(err, 'Could not stop the sandbox preview.')))
      .finally(() => setPreviewBusy(false));
  };

  const isKubernetesSandbox = sandboxBackend === 'kubernetes-sandbox-claim';
  const showPreviewSandboxButton = isKubernetesSandbox
    && (runPreviewState.status !== 'none' || Boolean(activePreviewSession));
  const previewUrl = activePreviewUrl ?? previewUrlFromSession(activePreviewSession);
  const keepaliveUrl = activePreviewSession?.keepalive_url ?? activePreviewSession?.keepaliveUrl ?? null;

  useEffect(() => {
    if (!keepaliveUrl) return;
    const id = setInterval(() => {
      apiClient.pingKeepalive(keepaliveUrl)
        .catch((err) => setPreviewError(`Preview keepalive failed: ${formatApiErrorMessage(err, 'The preview connection may expire.')}`));
    }, 60_000);
    return () => clearInterval(id);
  }, [keepaliveUrl]);

  const shortId         = runId && runId.length > 8 ? runId.slice(0, 8) : (runId ?? '');
  const isConnecting    = streamStatus === 'connecting';
  const isStreaming     = streamStatus === 'streaming';
  const hasGraph        = rfNodes.length > 0;
  // Reused by both the left-rail minimap AND the Work Plan detail thumbnail (#UI-bug-3) so the
  // graph-rendering ReactFlow/MiniMap markup exists in exactly one place.
  const renderTopologyThumbnail = (variant: 'rail' | 'workplan') => (
    <div
      role="button"
      tabIndex={0}
      className={variant === 'rail' ? styles.minimapButton : mergeClasses(styles.minimapButton, styles.workPlanTopologyThumbnail)}
      aria-label="Open full topology graph"
      onClick={() => setTopologyPanelOpen(true)}
      onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); setTopologyPanelOpen(true); } }}
      data-testid={variant === 'rail' ? 'open-topology-minimap' : 'open-topology-thumbnail-workplan'}
    >
      <span className={styles.minimapCaption}>Topology</span>
      <div className={styles.minimapCanvas} aria-hidden="true">
        {!topologyPanelOpen && hasGraph ? (
        <ExecutionModalContext.Provider value={viewAssemblyExecution}>
        <BrowseFilesContext.Provider value={browseAssemblyFiles}>
        <ActiveEdgeContext.Provider value={activeLoopbackId}>
        <CoordinatorSessionContext.Provider value={(opts) => openPanelForNode('coordinator', opts)}>
        <CoordPanelContext.Provider value={openPanelForNode}>
          <ReactFlow
            nodes={linkedDisplayNodes}
            edges={displayEdges2}
            nodeTypes={coordinatorNodeTypes}
            edgeTypes={workflowEdgeTypes}
            fitView
            fitViewOptions={{ padding: 0.12 }}
            nodesDraggable={false}
            nodesConnectable={false}
            nodesFocusable={false}
            edgesFocusable={false}
            elementsSelectable={false}
            panOnDrag={false}
            panOnScroll={false}
            zoomOnScroll={false}
            zoomOnPinch={false}
            zoomOnDoubleClick={false}
            preventScrolling={false}
            proOptions={{ hideAttribution: true }}
            style={{ width: '100%', height: '100%' }}
          >
            <MiniMap
              nodeStrokeWidth={0}
              nodeBorderRadius={2}
              pannable={false}
              zoomable={false}
              bgColor="var(--colorNeutralBackground2)"
              maskColor="transparent"
              nodeColor={(n) => {
                const s = (n.data as SubtaskNodeData | undefined)?.topoStatus as string | undefined;
                if (s === 'completed' || s === 'assemble_ready') return '#107c41';
                if (s === 'running' || s === 'dispatching' || s === 'awaiting_assembly' || s === 'assembling') return '#8c837c';
                if (s === 'waiting') return '#d47c00';
                if (s === 'failed' || s === 'declined') return '#c50f1f';
                return '#b8afa8';
              }}
            />
          </ReactFlow>
        </CoordPanelContext.Provider>
        </CoordinatorSessionContext.Provider>
        </ActiveEdgeContext.Provider>
        </BrowseFilesContext.Provider>
        </ExecutionModalContext.Provider>
        ) : (
          <span className={styles.minimapEmpty}>No graph yet</span>
        )}
      </div>
    </div>
  );
  const isRetryable     = viewState.canRetry;
  // Stop/toggle endpoints still require an active run, but coordinator messaging uses the backend's
  // explicit steerability bit so review-gated runs can receive operator instructions.
  const coordActive = coordinatorSteerable === true || (coordinatorSteerable === undefined && viewState.canStop);

  // A run can be terminally finished at the RUN level (Failed/Declined/Merged) while its WorkPlan
  // status still reads `in_review` — e.g. a run interrupted by a pre-durability build. In that state
  // the in-memory assembly-review gate is NOT armed, so presenting an actionable review bar would
  // 409. Treat the review as actionable only when the run itself is not terminal.
  const runTerminal = viewState.terminal;
  const reviewActionable = orch.phase === 'in_review' && !runTerminal;
  const selectedBuildTestNode = selectedSessionItem
    ? isBuildTestNodeIdOrLabel(selectedSessionItem.nodeId, selectedSessionItem.label)
    : false;

  // Map the coordinator orchestration phase onto the standard artifact-browser run status so the
  // reused Changes/Files rail shows the review bar (Approve / Request changes / Decline) exactly when
  // the ONE collective human-review gate is open.
  const coordRunStatus = useMemo(() => {
    if (runTerminal) {
      switch (runLevelStatus) {
        case 'completed':
        case 'merged':       return 'merged';
        case 'declined':     return 'declined';
        case 'failed':
        case 'blocked':
        case 'merge_failed': return 'merge_failed';
        default:             return runLevelStatus ?? 'merge_failed';
      }
    }
    switch (orch.phase) {
      case 'in_review':  return reviewActionable ? 'awaiting_review' : (runLevelStatus ?? 'merge_failed');
      case 'complete':   return 'merged';
      case 'declined':   return 'declined';
      case 'failed':
      case 'blocked':    return 'merge_failed';
      default:           return 'in_progress';
    }
  }, [orch.phase, reviewActionable, runLevelStatus, runTerminal]);

  // Adapter that points the standard artifact browser at the coordinator's collective assembly:
  // files/diff come from the integration branch (the coordinator owns no worktree), and the three
  // review actions are delivered to the collective assembly gate instead of the per-run endpoints.
  const coordAdapter = useMemo<ArtifactBrowserAdapter>(() => ({
    getFiles: (rid, filter) => apiClient.getAssemblyFiles(rid, filter),
    getFileDiff: (rid, path) => apiClient.getAssemblyFileDiff(rid, path),
    getWorkspace: (rid) => apiClient.getAssemblyWorkspace(rid),
    getContent: (rid, path) => apiClient.getAssemblyFileContent(rid, path),
    approve: (rid) => apiClient.reviewAssembly(rid, 'approve'),
    approveLabel: 'Approve & merge',
    approveAriaLabel: 'Approve human review and continue to merge',
    approveAcceptedStatus: 'review_accepted',
    requestChanges: (rid, comment) => apiClient.reviewAssembly(rid, 'request_changes', comment),
    decline: (rid) => apiClient.reviewAssembly(rid, 'decline'),
  }), []);

  // Run-wide changes summary: the coordinator's collective integration diff (assembly files).
  // getAssemblyFiles returns [] before assembly runs, so this stays null until real changes exist.
  // Refetch as the run advances/settles (coordRunStatus) so the Changes chip diff stays live.
  useEffect(() => {
    if (isChildRun || !runId) {
      setRunChangesSummary(null);
      return;
    }
    // Clear stale chips from the previous run immediately so a new runId never briefly shows
    // the prior run's counts while the new getAssemblyFiles is in flight.
    setRunChangesSummary(null);
    let cancelled = false;
    apiClient.getAssemblyFiles(runId)
      .then((entries) => {
        if (cancelled) return;
        if (!entries || entries.length === 0) {
          setRunChangesSummary(null);
          return;
        }
        const added = entries.reduce((sum, e) => sum + (e.added_lines ?? 0), 0);
        const removed = entries.reduce((sum, e) => sum + (e.removed_lines ?? 0), 0);
        setRunChangesSummary({ files: entries.length, added, removed });
      })
      .catch(() => { if (!cancelled) setRunChangesSummary(null); });
    return () => { cancelled = true; };
  }, [isChildRun, runId, coordRunStatus]);

  // Run-wide summary chips pinned just above the composer. Three distinct chips — Goal, Changes,
  // Files — each opening the matching overlay. All three are pinned to the coordinator/run
  // view and ALWAYS represent run-wide data, regardless of which node/scope is selected. Changes +
  // Files stay visible but disabled ("· None") when no run-wide diff/files exist yet.
  const runSummaryChips = useMemo<ReactNode>(() => {
    if (isChildRun) return null;
    const chips: ReactNode[] = [];

    // 1) Goal — opens the Outcome plan overlay (goal/scope/assumptions/questions).
    chips.push(
      <button
        key="goal"
        type="button"
        className={styles.runChip}
        onClick={() => setPlanPanelOpen(true)}
        data-testid="run-summary-chip-goal"
        title="Open the goal, scope, and assumptions"
        aria-label="Open the goal, scope, and assumptions"
      >
        <span className={styles.runChipLabel}>Goal</span>
        {specConfirmed && <span className={styles.runChipDot} aria-hidden="true" />}
      </button>,
    );

    // 2) Changes — opens the integration diff. Disabled + muted when no diff exists.
    if (runChangesSummary) {
      chips.push(
        <button
          key="changes"
          type="button"
          className={styles.runChip}
          onClick={() => setArtifactsPanelOpen(true)}
          data-testid="run-summary-chip-changes"
          title="Review the run-wide integration diff"
        >
          <span className={styles.runChipLabel}>Changes</span>
          <span className={styles.runChipCount}>
            {`\u00b7 ${runChangesSummary.files.toLocaleString()} ${runChangesSummary.files === 1 ? 'file' : 'files'} \u00b7 `}
          </span>
          <span className={styles.runChipAdded}>+{runChangesSummary.added.toLocaleString()}</span>
          <span className={styles.runChipRemoved}>&minus;{runChangesSummary.removed.toLocaleString()}</span>
        </button>,
      );
    } else {
      chips.push(
        <span
          key="changes"
          className={mergeClasses(styles.runChip, styles.runChipDisabled)}
          data-testid="run-summary-chip-changes"
          aria-disabled="true"
          title="No integration changes yet"
        >
          <span className={styles.runChipLabel}>Changes</span>
          <span className={styles.runChipCount}>{'\u00b7 None'}</span>
        </span>,
      );
    }

    // 3) Files — opens the produced-files browser. Disabled + muted when no files exist.
    if (runChangesSummary) {
      chips.push(
        <button
          key="files"
          type="button"
          className={styles.runChip}
          onClick={() => setFilesPanelOpen(true)}
          data-testid="run-summary-chip-files"
          title="Browse the files produced in this run"
          aria-label={`Browse produced files: ${runChangesSummary.files} ${runChangesSummary.files === 1 ? 'file' : 'files'}`}
        >
          <span className={styles.runChipLabel}>Files</span>
          <span className={styles.runChipCount}>{`\u00b7 ${runChangesSummary.files.toLocaleString()}`}</span>
        </button>,
      );
    } else {
      chips.push(
        <span
          key="files"
          className={mergeClasses(styles.runChip, styles.runChipDisabled)}
          data-testid="run-summary-chip-files"
          aria-disabled="true"
          title="No produced files yet"
        >
          <span className={styles.runChipLabel}>Files</span>
          <span className={styles.runChipCount}>{'\u00b7 None'}</span>
        </span>,
      );
    }

    return chips.length > 0 ? <>{chips}</> : null;
  }, [isChildRun, runChangesSummary, specConfirmed, styles]);

  const primaryAction = reviewActionable
    ? {
        label: 'Review changes',
        icon: <DocumentRegular />,
        disabled: false,
        onClick: () => setArtifactsPanelOpen(true),
        testId: 'coordinator-review-changes',
      }
    : null;

  const handleAssemblyApproval = useCallback(async (decision: 'approve' | 'decline') => {
    if (!runId) return;
    setAutomationError(null);
    try {
      await apiClient.reviewAssembly(runId, decision);
      reconnectStream();
    } catch (err) {
      setAutomationError(`Assembly review failed: ${formatApiErrorMessage(err, 'Could not update assembly review.')}`);
    }
  }, [reconnectStream, runId]);

  // Nested agentic progress tree: coordinator/agents and their tasks with live status.
  const approvalSteps = useMemo<AgentStep[]>(() => reviewActionable
    ? [{
        id: 'assembly-review',
        title: 'Human review',
        statusBadge: 'Run-level',
        body: 'Review the assembled result and the integration diff, then decide whether to merge and finalize this run.',
        status: 'warning',
        needsInput: true,
        riskText: 'Approve to merge and finalize (Merge \u2192 Scribe), or decline to send it back.',
        disclaimer: 'You can request changes from the Artifacts tab.',
        approveLabel: 'Approve & merge',
        denyLabel: 'Decline',
        defaultOpen: true,
      }]
    : [], [reviewActionable]);

  // ---------------------------------------------------------------------------
  // Messages — the intent-grouped Timeline now lives inside AgentSessionPanel,
  // which owns the scope-aware event stream (coordinator root vs. selected child),
  // the composer/steering, approvals and Changes/Files. The page no longer builds
  // its own timeline model.
  // ---------------------------------------------------------------------------

  const graphEmptyState = graphEmptyCopy(isConnecting, noWorkPlan, graphError, viewState);
  const topologySelectionCopy = selectedSessionItem
    ? `Selected: ${selectedSessionItem.label}`
    : orch.phase !== 'unknown'
      ? orchPhaseLabel(orch.phase)
      : 'No task selected';
  const topologyInspectorContent = (
    <div className={styles.topologyInspector} data-testid="topology-inspector">
      <div className={styles.topologyInspectorSummary}>
        <Text className={styles.hint}>{topologySelectionCopy}</Text>
        <Text className={styles.hint}>Select a node to focus its run messages, changes, and files.</Text>
      </div>
      {hasGraph ? (
        <ExecutionModalContext.Provider value={viewAssemblyExecution}>
        <BrowseFilesContext.Provider value={browseAssemblyFiles}>
        <ActiveEdgeContext.Provider value={activeLoopbackId}>
        <CoordinatorSessionContext.Provider value={(opts) => openPanelForNode('coordinator', opts)}>
        <CoordPanelContext.Provider value={openPanelForNode}>
          <ReactFlowProvider>
          <TopologyToolbar
            orientation={graphOrientation}
            onToggleOrientation={() => {
              setOrientationUserChose(true);
              setGraphOrientation((o) => (o === 'LR' ? 'TB' : 'LR'));
            }}
            onTidy={() => setTidyNonce((n) => n + 1)}
            fitPadding={0.14}
          />
          <TopologyViewportController apiRef={topologyViewportApiRef} fitPadding={0.14} />
          <div
            className={`${styles.dagContainer} ${styles.topologyDag}`}
            role="region"
            data-testid="topology-scroll-container"
            data-graph-scroll="owned"
            data-pan-enabled="true"
            tabIndex={0}
            aria-label="Topology graph. Drag to pan; use the toolbar or ctrl+scroll to zoom."
          >
            <div ref={topoContainerRef} data-testid="topology-graph-canvas" style={{ width: '100%', height: '100%' }}>
              <ReactFlow
                key={`${graphOrientation}:${displayNodes.length}:${displayEdges2.length}:${tidyNonce}:${layoutSignature}`}
                nodes={linkedDisplayNodes}
                edges={displayEdges2}
                nodeTypes={coordinatorNodeTypes}
                edgeTypes={workflowEdgeTypes}
                fitView
                fitViewOptions={{ padding: 0.14 }}
                minZoom={0.2}
                maxZoom={2}
                nodesDraggable={false}
                nodesConnectable={false}
                nodesFocusable={false}
                edgesFocusable={false}
                panOnScroll
                preventScrolling={false}
                zoomOnScroll={false}
                zoomOnPinch
                zoomOnDoubleClick={false}
                panOnDrag
                style={{ width: '100%', height: '100%' }}
                onNodeClick={(_, node) => {
                  openPanelForNode(node.id);
                  topologyViewportApiRef.current?.centerOnNode(node);
                }}
                onPaneClick={() => {
                  topologyViewportApiRef.current?.fitAll();
                }}
                proOptions={{ hideAttribution: true }}
              >
                <MiniMap
                  nodeStrokeWidth={0}
                  nodeBorderRadius={3}
                  zoomable
                  pannable
                  bgColor="var(--colorNeutralBackground2)"
                  maskColor="rgba(0, 0, 0, 0.06)"
                  maskStrokeColor="var(--colorNeutralStroke1)"
                  maskStrokeWidth={2}
                  style={{
                    bottom: 8,
                    right: 8,
                    width: 104,
                    height: 72,
                    border: '1px solid var(--colorNeutralStroke2)',
                    borderRadius: '6px',
                    boxShadow: 'var(--shadow4)',
                  }}
                  nodeColor={(n) => {
                    const s = (n.data as SubtaskNodeData | undefined)?.topoStatus as string | undefined;
                    if (s === 'completed' || s === 'assemble_ready') return '#107c41';
                    if (s === 'running' || s === 'dispatching' || s === 'awaiting_assembly' || s === 'assembling') return '#8c837c';
                    if (s === 'waiting') return '#d47c00';
                    if (s === 'failed' || s === 'declined') return '#c50f1f';
                    return '#b8afa8';
                  }}
                />
              </ReactFlow>
            </div>
          </div>
          </ReactFlowProvider>
          {inSpecAuthoring && <Text className={styles.hint}>The execution pipeline appears once you confirm the Outcome plan.</Text>}
        </CoordPanelContext.Provider>
        </CoordinatorSessionContext.Provider>
        </ActiveEdgeContext.Provider>
        </BrowseFilesContext.Provider>
        </ExecutionModalContext.Provider>
      ) : (
        <EmptyState title={graphEmptyState.title} description={graphEmptyState.body} />
      )}
    </div>
  );
  const stateIconClass = (color: SemanticStateColor) => {
    switch (color) {
      case 'running': return styles.runTreeStatusRunning;
      case 'success': return styles.runTreeStatusSuccess;
      case 'danger': return styles.runTreeStatusDanger;
      case 'input': return styles.runTreeStatusInput;
      default: return styles.runTreeStatusQueued;
    }
  };
  const stateTextClass = (color: SemanticStateColor) => {
    switch (color) {
      case 'running': return styles.stateTextRunning;
      case 'success': return styles.stateTextSuccess;
      case 'danger': return styles.stateTextDanger;
      case 'input': return styles.stateTextInput;
      default: return styles.stateTextQueued;
    }
  };
  const treeBranchIds = useMemo(() => {
    const ids: string[] = [];
    const walk = (nodes: RunSessionTree[]) => nodes.forEach((n) => {
      if (n.children.length > 0) { ids.push(n.nodeId); walk(n.children); }
    });
    walk(sessionTree);
    return ids;
  }, [sessionTree]);
  const renderTreeItems = (nodes: RunSessionTree[]): ReactNode => nodes.map((item) => {
    const color = semanticStateColorForStatus(item.status);
    const statusLabel = runTreeStatusLabel(item.status, item.nodeId === 'outcome-plan' ? outcomePlanConfirmedBy : undefined);
    const selected = item.nodeId === activePanelNodeId;
    // Task-first layout: PRIMARY = task title (root/coordinator = "Coordinator").
    // SECONDARY = "{statusLabel} · {agentName} ({role})". Agent identity lives only
    // in the secondary line — no role pill, no "Unassigned agent" bold fallback.
    const isRootNode = item.nodeId === defaultSessionNodeId;
    const primaryText = isRootNode ? 'Coordinator' : item.label;
    const buildTestNode = isBuildTestNodeIdOrLabel(item.nodeId, item.label);
    const identityName = isRootNode ? 'Coordinator' : item.agentName;
    const identityRole = isRootNode ? (item.agentRole ?? 'Coordinator') : item.agentRole;
    const identityText = identityName
      ? (identityRole ? `${identityName} (${identityRole})` : identityName)
      : (identityRole ?? '');
    const avatarName = identityName ?? item.agentRole ?? item.label;
    const layout = (
      <TreeItemLayout
        className={mergeClasses(styles.treeItemLayout, selected && styles.treeItemLayoutSelected)}
        iconBefore={(
          <span
            className={mergeClasses(styles.runTreeStatusIcon, stateIconClass(color))}
            data-testid="run-tree-status-icon"
            data-state-color={color}
            style={{ backgroundColor: 'transparent', borderTopStyle: 'none' }}
            aria-hidden="true"
          >
            {runTreeStatusIcon(item.status)}
          </span>
        )}
      >
        <span className={styles.treeNode}>
          <AgentAvatar name={avatarName} size={22} circle />
          <span className={styles.treeText}>
            <span className={styles.treePrimary} title={primaryText}>
              {primaryText}
              {buildTestNode && runPreviewState.status === 'ready' && <span className={styles.runTreePreviewPill}>Preview</span>}
              {buildTestNode && runPreviewState.status === 'pending' && <span className={mergeClasses(styles.runTreePreviewPill, styles.runTreePreviewPillPending)}>Pending</span>}
              {buildTestNode && runPreviewState.status === 'failed' && <span className={mergeClasses(styles.runTreePreviewPill, styles.runTreePreviewPillUnavailable)}>Unavailable</span>}
            </span>
            <span className={styles.treeMetaRow}>
              <span
                className={mergeClasses(styles.treeStatusText, stateTextClass(color))}
                data-state-color={color}
              >
                <span className={styles.treeStatusDot} aria-hidden="true" />
                {statusLabel}
              </span>
              {isRootNode && coordinatorChildSummary ? (
                <span className={styles.treeIdentity} title={coordinatorChildSummary}>{`\u00b7 ${coordinatorChildSummary}`}</span>
              ) : identityText ? (
                <span className={styles.treeIdentity} title={identityText}>{`\u00b7 ${identityText}`}</span>
              ) : null}
              {(item.pendingApprovalCount ?? 0) > 0 && (
                <span className={mergeClasses(styles.treeStatusText, styles.stateTextInput)} data-testid="run-tree-pending-approval">
                  <span className={styles.treeStatusDot} aria-hidden="true" />
                  {item.pendingApprovalCount} approval{item.pendingApprovalCount === 1 ? '' : 's'} needed
                </span>
              )}
            </span>
          </span>
        </span>
      </TreeItemLayout>
    );
    // Flat list: every node (Coordinator + all subtasks/stages) renders as a single-level leaf
    // row so there is NO indentation and NO expand chevron — every row shares the identical
    // [status icon][avatar][title / status · Agent (Role)] structure and aligns at the same
    // left edge. Hierarchy reads from the status + "· Agent (Role)" secondary, not indentation.
    return (
      <TreeItem
        key={item.nodeId}
        itemType="leaf"
        value={item.nodeId}
        aria-label={`Select ${item.label}: ${statusLabel}`}
        onClick={() => openPanelForNode(item.nodeId)}
      >
        {layout}
      </TreeItem>
    );
  });
  const openPreview = () => {
    if (runPreviewState.status === 'ready') {
      window.open(runPreviewState.previewUrl, '_blank', 'noopener,noreferrer');
    }
  };
  const previewStatusContent = (compact = false) => {
    switch (runPreviewState.status) {
      case 'ready':
        return (
          <>
            <div className={styles.previewStatusStack}>
              <Text weight="semibold">{compact ? 'Build & Test preview is active.' : 'Preview from Build & Test is active.'}</Text>
              {runPreviewState.targetPort && <Text className={styles.previewStatusReason}>Port {runPreviewState.targetPort}</Text>}
            </div>
            <Button appearance="primary" size="small" icon={<OpenRegular />} onClick={openPreview}>
              Open preview
            </Button>
          </>
        );
      case 'pending':
        return (
          <div className={styles.previewStatusStack}>
            <Text weight="semibold">Preview pending approval</Text>
            <Text className={styles.previewStatusReason}>
              Human review can still proceed when it is available.
            </Text>
          </div>
        );
      case 'failed':
        return (
          <div className={styles.previewStatusStack}>
            <Text weight="semibold">Preview unavailable</Text>
            <Text className={styles.previewStatusReason}>
              {previewFailureCopy(runPreviewState)}. Human review can still proceed.
            </Text>
          </div>
        );
      default:
        return null;
    }
  };
  const previewStatusSlot = runPreviewState.status === 'none'
    ? undefined
    : (
      <div
        className={`${styles.selectedTaskPreviewCta} ${runPreviewState.status === 'pending' ? styles.selectedTaskPreviewPending : ''} ${runPreviewState.status === 'failed' ? styles.selectedTaskPreviewUnavailable : ''}`}
        data-testid="human-review-preview-status"
      >
        {previewStatusContent()}
      </div>
    );
  const retryHint = isRetryable ? 'Starts a fresh run from the same goal. The original run is kept and linked.' : 'Re-run available after failure';
  const stopHint = viewState.canStop ? 'Stop cancels run' : 'Stop while running';
  const retryAriaLabel = isRetryable ? 'Re-run this orchestration' : `Re-run unavailable: ${retryHint}`;
  const stopAriaLabel = viewState.canStop ? 'Stop run' : `Stop run unavailable: ${stopHint}`;

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  if (runLoadError && !restDescriptor && events.length === 0) {
    return (
      <div className={styles.root}>
        <nav className={styles.breadcrumb} aria-label="Breadcrumb">
          <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
          <span aria-hidden="true">/</span>
          <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{projectName}</Link>
          <span aria-hidden="true">/</span>
          <span>Orchestration {shortId}</span>
        </nav>
        <section className={styles.pageError} aria-live="polite">
          <Display>{viewState.label}</Display>
          <Text className={styles.stateReason}>{runLoadError.message}</Text>
          {runLoadError.detail && <Text className={styles.stateReason}>{runLoadError.detail}</Text>}
          <Text className={styles.phaseSource}>{viewState.sourceLabel}</Text>
          <div className={styles.pageErrorActions}>
            <Button appearance="primary" onClick={() => window.location.reload()}>Refresh</Button>
            <Button appearance="secondary" onClick={() => navigate(`/projects/${projectId}`)}>Back to project</Button>
          </div>
        </section>
      </div>
    );
  }

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{projectName}</Link>
        <span aria-hidden="true">/</span>
        <span>Orchestration {shortId}</span>
      </nav>

      {(retryError || stopError || automationError || workPlanError || (runLoadError && (restDescriptor || events.length > 0)) || streamError || droppedEventCount > 0 || streamStatus === 'error') && (
        <div className={styles.statusBannerStack} aria-live="polite">
          {runLoadError && (restDescriptor || events.length > 0) && (
            <MessageBar intent="warning">
              <MessageBarBody>Run refresh failed: {runLoadError.message}{runLoadError.detail ? ` ${runLoadError.detail}` : ''}</MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" size="small" onClick={reconnectStream}>Reconnect stream</Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {workPlanError && (
            <MessageBar intent="warning">
              <MessageBarBody>Work plan refresh failed: {workPlanError.message}{workPlanError.detail ? ` ${workPlanError.detail}` : ''}</MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" size="small" onClick={reconnectStream}>Refresh</Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {(streamError || streamStatus === 'error' || droppedEventCount > 0) && (
            <MessageBar intent="warning" data-testid="coordinator-stream-health">
              <MessageBarBody>
                {streamError ? `Live stream issue: ${streamError}` : streamStatus === 'error' ? 'Live stream disconnected.' : 'Live stream dropped older events.'}
                {droppedEventCount > 0 ? ` ${droppedEventCount} event${droppedEventCount === 1 ? '' : 's'} were dropped from the in-memory buffer; refresh for a complete replay.` : ' Refresh or reconnect if the graph looks stale.'}
              </MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" size="small" onClick={reconnectStream}>Reconnect</Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {retryError && (
            <MessageBar intent="error">
              <MessageBarBody>Re-run failed: {retryError}</MessageBarBody>
            </MessageBar>
          )}
          {stopError && (
            <MessageBar intent="error">
              <MessageBarBody>Stop failed: {stopError}</MessageBarBody>
            </MessageBar>
          )}
          {automationError && (
            <MessageBar intent="error">
              <MessageBarBody>{automationError}</MessageBarBody>
            </MessageBar>
          )}
        </div>
      )}

      <div className={styles.console} data-testid="run-operator-console">
        <div className={styles.runHeader} data-testid="run-header">
          <div className={styles.identityArea} data-testid="run-summary">
            <div className={styles.topTitleRow}>
              <div className={styles.identityLead}>
                <h1
                  className={styles.titleText}
                  style={{ whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden' }}
                  title={`Orchestration run ${runId}`}
                  data-testid="run-title"
                >
                  Orchestration
                </h1>
                {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Live" />}
                <span
                  className={mergeClasses(styles.statusChip, styles.statusChipStrong, stateTextClass(runStatusColor))}
                  data-testid="run-status-chip"
                  data-state-color={runStatusColor}
                >
                  {runStatusText}
                </span>
              </div>
              <div className={styles.statsStrip} aria-label="Run progress" data-testid="run-progress-chips">
                <span className={styles.statusChip}>{taskCountsLabel}</span>
                <span className={styles.statusChip}>{elapsedLabel} elapsed</span>
              </div>
              <div className={styles.compactChromeActions}>
                {primaryAction && (
                  <Button
                    appearance="primary"
                    size="small"
                    icon={primaryAction.icon}
                    disabled={primaryAction.disabled}
                    onClick={primaryAction.onClick}
                    data-testid="compact-primary-run-action"
                  >
                    {primaryAction.label}
                  </Button>
                )}
                <Button
                  appearance={isRetryable ? 'secondary' : 'subtle'}
                  size="small"
                  icon={<ArrowRepeatAllRegular />}
                  disabled={!isRetryable || retrying}
                  onClick={() => void handleRetry()}
                  data-testid="coordinator-retry-button"
                  aria-label={retryAriaLabel}
                  title={retryHint}
                />
                <Button
                  appearance={viewState.canStop ? 'secondary' : 'subtle'}
                  size="small"
                  icon={stopping ? <Spinner size="extra-tiny" /> : <DismissRegular />}
                  disabled={!viewState.canStop || stopping}
                  onClick={() => setStopConfirmationOpen(true)}
                  data-testid="coordinator-stop-button"
                  aria-label={stopAriaLabel}
                  title={stopHint}
                />
                {showPreviewSandboxButton && (
                  <Button
                    appearance="transparent"
                    size="small"
                    icon={<OpenRegular />}
                    onClick={() => { setPreviewDialogOpen(true); setPreviewError(undefined); }}
                    aria-label="Preview Sandbox"
                    title="Preview Sandbox"
                  />
                )}
              </div>
            </div>
          </div>
        </div>

        <div className={mergeClasses(styles.bodyGrid, treeRailCollapsed && styles.bodyGridCollapsed)}>
          {treeRailCollapsed ? (
            <aside className={styles.treeRailCollapsed} aria-label="Run tree (collapsed)">
              <Button
                appearance="subtle"
                size="small"
                icon={<PanelLeftExpandRegular />}
                aria-label="Expand run tree"
                data-testid="toggle-run-tree"
                onClick={() => setTreeRailCollapsed(false)}
              />
            </aside>
          ) : (
          <aside className={styles.treeRail} aria-label="Run tree">
            <div className={styles.treeRailHeader}>
              <TitleText>Run tree</TitleText>
              <div className={styles.treeRailHeaderRight}>
                <Text className={styles.hint}>{flatSessionTree.length} nodes</Text>
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<PanelLeftContractRegular />}
                  aria-label="Collapse run tree"
                  data-testid="toggle-run-tree"
                  onClick={() => setTreeRailCollapsed(true)}
                />
              </div>
            </div>
            {flatSessionTree.length === 0 ? (
              <div className={styles.treeEmpty}>
                <EmptyState
                  title="Coordinator is still shaping the run"
                  description="The task tree appears after the outcome plan or saved work plan arrives. Keep the stream open; if it stays empty, message the coordinator from Chat or retry a failed run."
                />
              </div>
            ) : (
              <div className={styles.treeScroll}>
                <Tree aria-label="Run tree" size="small" openItems={treeBranchIds}>
                  {renderTreeItems(flatSessionTree)}
                </Tree>
              </div>
            )}
            <div className={styles.railStatusBlock} data-testid="rail-status-block">
              <span className={styles.railStatusCaption}>Workflow</span>
              <span className={styles.railStatusWorkflow}>
                <FlowchartRegular aria-hidden="true" />
                <span title={executionWorkflowName}>{executionWorkflowName}</span>
                <Tooltip content={executionWhySelected} relationship="description" withArrow>
                  <span className={styles.railStatusInfoTrigger} tabIndex={0} role="button" aria-label="Why this workflow was selected" data-testid="rail-status-reason-info">
                    <InfoRegular className={styles.railStatusInfoGlyph} aria-hidden="true" />
                  </span>
                </Tooltip>
              </span>
              <span className={styles.railStatusReason} data-state-color={executionDisplayStateColor} data-testid="rail-status-reason">
                <span className={mergeClasses(styles.railStatusState, stateTextClass(executionDisplayStateColor))}>{executionKickerLabel}</span>
                {executionReasonShort && <span className={styles.railStatusReasonShort}>{executionReasonShort}</span>}
              </span>
              {orch.phase === 'blocked' && orch.ineligibleSubtasks && orch.ineligibleSubtasks.length > 0 && (
                <div className={styles.railStatusIneligible} data-testid="assembly-ineligible-subtasks">
                  <span className={styles.railStatusIneligibleCaption}>
                    {orch.reason ?? 'Assembly is waiting on subtasks that aren\u2019t ready.'}
                  </span>
                  <ul className={styles.railStatusIneligibleList}>
                    {orch.ineligibleSubtasks.map((sub) => (
                      <li key={sub.id} data-testid="assembly-ineligible-subtask">
                        <span className={styles.railStatusIneligibleId}>#{sub.id}</span>
                        {sub.title ? <span className={styles.railStatusIneligibleTitle}>{sub.title}</span> : null}
                        {sub.status ? (
                          <span className={styles.railStatusIneligibleState}>{sub.status.replace(/_/g, ' ')}</span>
                        ) : null}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
            <div className={styles.treeRailFooter}>
              {renderTopologyThumbnail('rail')}
              </div>
          </aside>
          )}

          <section className={styles.centerZone} aria-label="Selected task">
            {reviewActionable && approvalSteps.length > 0 && (
              <div className={styles.approvalGateWrap}>
                <AgentStepList
                  steps={approvalSteps}
                  onApprove={() => void handleAssemblyApproval('approve')}
                  onDeny={() => void handleAssemblyApproval('decline')}
                  aria-label="Approvals and gates"
                />
              </div>
            )}
            {selectedBuildTestNode && runPreviewState.status !== 'none' && (
              <div
                className={`${styles.selectedTaskPreviewCta} ${runPreviewState.status === 'pending' ? styles.selectedTaskPreviewPending : ''} ${runPreviewState.status === 'failed' ? styles.selectedTaskPreviewUnavailable : ''}`}
                data-testid="selected-build-preview-cta"
              >
                {previewStatusContent()}
              </div>
            )}

            <div className={styles.centerTabBody}>
              {/* Messages — the single conversation surface. AgentSessionPanel renders the
                  intent-driven Timeline (ChainOfThought steps), the composer/steering with
                  Autopilot/Auto-approve toggles, in-thread approvals and per-scope Changes. */}
              <div className={styles.readoutBody}>
                <AgentSessionPanel
                  variant="docked"
                  open={sessionPanelOpen && Boolean(activePanelNodeId)}
                  selectedNodeId={activePanelNodeId}
                  coordinatorRunId={runId ?? ''}
                  projectId={projectId ?? ''}
                  tree={sessionTree}
                  onClose={() => setSessionPanelOpen(false)}
                  onSelectNode={openPanelForNode}
                  onCoordinatorFollowUp={reconnectStream}
                  coordinatorActive={coordActive}
                  composerFocusSignal={composerFocusSignal}
                  onOutcomePlanClarify={() => setOutcomePlanClarifying(true)}
                  artifactAdapter={coordAdapter}
                  runChips={runSummaryChips}
                  outcomePlanDispatched={hasSubtaskNodes || viewState.terminal}
                  workPlanTopologyThumbnail={renderTopologyThumbnail('workplan')}
                  credits={{
                    totalNanoAiu: tokenBreakdown?.totalNanoAiu ?? null,
                    detail: <AgentTokenBreakdown data={tokenBreakdown} roleByAgent={roleByAgent} plain showHeader={false} />,
                  }}
                  automation={{
                    autopilot,
                    autoApprove,
                    autopilotBusy,
                    autoApproveBusy,
                    canToggle: viewState.canToggleAutomation,
                    onToggleAutopilot: () => toggleAutopilot(!autopilot),
                    onToggleAutoApprove: () => toggleAutoApprove(!autoApprove),
                  }}
                />
              </div>
            </div>
          </section>
        </div>
      </div>

      <SlidePanel
        open={topologyPanelOpen}
        onClose={() => setTopologyPanelOpen(false)}
        title="Topology"
        width="min(1040px, 96vw)"
        bodyClassName={styles.topologyPanelBody}
      >
        {topologyInspectorContent}
      </SlidePanel>

      {!isChildRun && (
        <SlidePanel
          open={planPanelOpen}
          onClose={() => setPlanPanelOpen(false)}
          title="Outcome plan"
          width="min(880px, 96vw)"
        >
          <OutcomePlanPanel
            runId={runId}
            projectId={projectId ?? undefined}
            events={events}
            streamStatus={streamStatus}
            runStatus={runLevelStatus}
            onCollapse={() => setPlanPanelOpen(false)}
            onReconnect={reconnectStream}
            onClarifyPlan={() => { setPlanPanelOpen(false); focusOutcomePlanComposer(); }}
            dispatched={hasSubtaskNodes || viewState.terminal}
          />
        </SlidePanel>
      )}

      {!isChildRun && (
        <SlidePanel
          open={artifactsPanelOpen}
          onClose={() => setArtifactsPanelOpen(false)}
          title="Changes"
          width="100vw"
          flushBody
        >
          <CoordinatorArtifactsPanel runId={runId} runStatus={coordRunStatus} adapter={coordAdapter} liveUpdateKey={artifactsLiveUpdateKey} previewStatusSlot={previewStatusSlot} />
        </SlidePanel>
      )}

      {!isChildRun && (
        <SlidePanel
          open={filesPanelOpen}
          onClose={() => setFilesPanelOpen(false)}
          title="Files"
          width="100vw"
          flushBody
        >
          <CoordinatorArtifactsPanel runId={runId} runStatus={coordRunStatus} adapter={coordAdapter} initialTab="files" liveUpdateKey={artifactsLiveUpdateKey} />
        </SlidePanel>
      )}

      {/* Sandbox preview port-forward dialog */}
      <Dialog open={previewDialogOpen} onOpenChange={(_, d) => { if (!d.open) setPreviewDialogOpen(false); }}>
        <DialogSurface style={{ maxWidth: previewUrl ? '900px' : '480px' }}>
          <DialogBody>
            <DialogTitle
              action={
                <Button
                  appearance="subtle"
                  aria-label="Close"
                  icon={<DismissRegular />}
                  onClick={() => setPreviewDialogOpen(false)}
                />
              }
            >
              Sandbox Preview
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, paddingTop: tokens.spacingVerticalM }}>
              {!activePreviewSession ? (
                <>
                  <Text>
                    Preview traffic is proxied through the Agentweaver API server.
                    Enter the port your app is listening on inside the sandbox.
                  </Text>
                  <Field label="Target port (inside sandbox)" validationMessage={previewError} validationState={previewError ? 'error' : 'none'}>
                    <Input
                      type="number"
                      value={previewTargetPort}
                      onChange={(_, d) => setPreviewTargetPort(d.value)}
                      disabled={previewBusy}
                    />
                  </Field>
                </>
              ) : (
                <>
                  <Text>
                    Preview active for port {activePreviewSession.target_port} on pod <code>{activePreviewSession.pod_name}</code>.
                    {previewUrl ? ' The proxied preview is shown below.' : ' The API server did not return a proxied preview URL.'}
                  </Text>
                  {previewUrl && (
                    <iframe
                      title="Sandbox preview"
                      src={previewUrl}
                      referrerPolicy="no-referrer"
                      style={{ width: '100%', minHeight: '360px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 6 }}
                    />
                  )}
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    Session ID: {activePreviewSession.session_id}
                  </Text>
                </>
              )}
            </DialogContent>
            <DialogActions>
              {!activePreviewSession ? (
                <>
                  {previewUrl && (
                    <Button appearance="primary" icon={<OpenRegular />} onClick={() => window.open(previewUrl, '_blank', 'noopener,noreferrer')}>
                      Open preview
                    </Button>
                  )}
                  <Button
                    appearance="primary"
                    onClick={startPreview}
                    disabled={previewBusy}
                    icon={previewBusy ? <Spinner size="extra-tiny" /> : undefined}
                  >
                    Start
                  </Button>
                  <Button appearance="secondary" onClick={() => setPreviewDialogOpen(false)}>Cancel</Button>
                </>
              ) : (
                <>
                  <Button
                    appearance="secondary"
                    onClick={stopPreview}
                    disabled={previewBusy}
                  >
                    Stop
                  </Button>
                  <Button appearance="secondary" onClick={() => setPreviewDialogOpen(false)}>Close</Button>
                </>
              )}
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      <Dialog open={stopConfirmationOpen} onOpenChange={(_, d) => setStopConfirmationOpen(d.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Stop this run?</DialogTitle>
            <DialogContent>
              Are you sure you want to stop this run?
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setStopConfirmationOpen(false)} disabled={stopping}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                onClick={() => {
                  setStopConfirmationOpen(false);
                  void handleStopRun();
                }}
                disabled={stopping}
              >
                Stop run
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
