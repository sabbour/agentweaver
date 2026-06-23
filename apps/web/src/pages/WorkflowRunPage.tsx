import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Spinner,
  Switch,
  Text,
  Title2,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  BotRegular,
  CheckmarkCircleRegular,
  DismissRegular,
  MergeRegular,
  NotebookRegular,
  PersonRegular,
  ShieldRegular,
} from '@fluentui/react-icons';
import {
  ReactFlow,
  type Node,
  type Edge,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useRunStream, type RunStreamEvent, type EventType } from '../api/sse';
import { apiClient } from '../api/apiClient';
import type { TeamDto, GraphDescriptor } from '../api/types';
import { API_KEY, API_URL } from '../config';
import { layoutDag, NODE_W, NODE_H, NODE_TYPE_W, NODE_TYPE_H } from '../utils/dagLayout';
import { RunWatcher } from '../components/RunWatcher';
import { QuestionAnswerCard } from '../components/QuestionAnswerCard';
import { useCtrlScrollZoom, ZoomControls } from '../components/board/useCtrlScrollZoom';
import {
  workflowNodeTypes,
  workflowEdgeTypes,
  forwardEdge,
  loopbackEdge,
  ExecutionModalContext,
  ActiveEdgeContext,
  roleDescForRole,
  iconForRole,
  type ExecutorDef,
  type ExecutorState,
  type StepStatus,
  type WorkflowNodeData,
} from '../components/WorkflowGraphPanel';

// ExecutionModalContext and ActiveEdgeContext are imported from WorkflowGraphPanel.
// WorkflowNode, LoopbackEdge, edge types, and all card styles are also from WorkflowGraphPanel.

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type ExecutorKey = 'agent' | 'rai' | 'review' | 'merge' | 'scribe' | 'assemble-ready';

const EXECUTORS: ExecutorDef[] = [
  { key: 'agent',  label: 'Agent',  roleDescription: 'AI Assistant',    Icon: BotRegular      },
  { key: 'rai',    label: 'Rai',    roleDescription: 'RAI Reviewer',     Icon: ShieldRegular   },
  { key: 'review', label: 'Review', roleDescription: 'Human Review',     Icon: PersonRegular   },
  { key: 'merge',  label: 'Merge',  roleDescription: 'Merge Coordinator',Icon: MergeRegular    },
  { key: 'scribe', label: 'Scribe', roleDescription: 'Session Logger',   Icon: NotebookRegular },
];

// Coordinator CHILD runs execute a TRIMMED pipeline server-side: agent → RAI →
// assemble-ready. Human Review, Merge, and Scribe run ONCE later on the collective
// output at the coordinator level, so a child must not show those nodes. Assemble-ready
// is a terminal node — the child is parked awaiting collective assembly.
const CHILD_EXECUTORS: ExecutorDef[] = [
  { key: 'agent',          label: 'Agent',          roleDescription: 'AI Assistant',                Icon: BotRegular            },
  { key: 'rai',            label: 'Rai',            roleDescription: 'RAI Reviewer',                Icon: ShieldRegular         },
  { key: 'assemble-ready', label: 'Assemble-ready', roleDescription: 'Awaiting collective assembly', Icon: CheckmarkCircleRegular },
];

// ---------------------------------------------------------------------------
// Styles — page-level
// ---------------------------------------------------------------------------

const usePageStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  runIdLabel: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  dagContainer: {
    height: '520px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    '& .react-flow__renderer': {
      borderRadius: '8px',
    },
  },
  dagLoading: {
    height: '520px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
});

// ---------------------------------------------------------------------------
// Styles — custom workflow node (defined in WorkflowGraphPanel — imported above)
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Edge helpers + edge/node type constants
// ---------------------------------------------------------------------------

// Forward edges only — fed to dagre. Loop-backs are excluded so dagre doesn't
// invert cycles and corrupt LR rank assignment.
const FORWARD_EDGES: Edge[] = [
  forwardEdge('agent-rai',    'agent',  'rai'),
  forwardEdge('rai-review',   'rai',    'review'),
  forwardEdge('review-merge', 'review', 'merge'),
  forwardEdge('merge-scribe', 'merge',  'scribe'),
];

// Loop-back edges — sides and heights computed automatically by LoopbackEdge.
const LOOPBACK_EDGES: Edge[] = [
  loopbackEdge('rai-agent-revise',    'rai',    'agent', 'Revise'),
  loopbackEdge('review-agent-change', 'review', 'agent', 'Request changes'),
];

const ALL_EDGES: Edge[] = [...FORWARD_EDGES, ...LOOPBACK_EDGES];

// CHILD pipeline edges — agent → RAI → assemble-ready.
const CHILD_FORWARD_EDGES: Edge[] = [
  forwardEdge('agent-rai',    'agent', 'rai'),
  forwardEdge('rai-assemble', 'rai',   'assemble-ready'),
];

const CHILD_EDGES: Edge[] = [...CHILD_FORWARD_EDGES];

// Statuses for which the run is finished/parked and its live SSE stream is closed, so the
// timeline must be seeded from the persisted events endpoint. Generous on purpose: a child
// parks at assemble-ready, and listing unknown-but-inactive states here is harmless.
const SEED_STATUSES: ReadonlySet<string> = new Set([
  'completed', 'failed', 'merged', 'declined', 'merge_failed',
  'parked', 'assemble_ready', 'assembled', 'cancelled', 'stopped',
]);

// Fold a persisted-events REST seed under live SSE deltas. Seeded events come first in
// order; a live event is appended only when not already represented (dedupe by sequence,
// and singleton seq-0 events by type) so a finished run shows persisted progress and an
// in-flight reconnect still layers new deltas on top.
function mergeRunEvents(seed: RunStreamEvent[], live: RunStreamEvent[]): RunStreamEvent[] {
  if (seed.length === 0) return live;
  const merged = [...seed];
  const seenSeq = new Set(seed.filter((e) => e.sequence > 0).map((e) => e.sequence));
  const seenType = new Set(seed.map((e) => e.type));
  for (const evt of live) {
    if (evt.sequence > 0) {
      if (seenSeq.has(evt.sequence)) continue;
      seenSeq.add(evt.sequence);
    } else if (seenType.has(evt.type)) {
      continue;
    }
    merged.push(evt);
  }
  return merged;
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function WorkflowRunPage() {
  const styles = usePageStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  // Ctrl+Scroll zoom over the workflow diagram (workflow-zoom) — shared with the board.
  const { zoom, zoomIn, zoomOut, viewportRef } = useCtrlScrollZoom();

  const [agentName,      setAgentName]      = useState<string | undefined>(undefined);
  const [agentRoleTitle, setAgentRoleTitle] = useState<string | undefined>(undefined);
  const [modelId,        setModelId]        = useState<string | undefined>(undefined);
  const [runStatus,      setRunStatus]      = useState<string | undefined>(undefined);
  const [reviewedBy,     setReviewedBy]     = useState<string | undefined>(undefined);
  const [executionId,    setExecutionId]    = useState<string | undefined>(undefined);
  const [parentRunId,    setParentRunId]    = useState<string | undefined>(undefined);
  const [loading,        setLoading]        = useState(true);
  const [modalExecId,    setModalExecId]    = useState<string | undefined>(undefined);
  const [team,           setTeam]           = useState<TeamDto | undefined>(undefined);
  const [seedEvents,     setSeedEvents]     = useState<RunStreamEvent[]>([]);
  // REST-seeded graph descriptor (null = 404 or not yet resolved → hardcoded fallback).
  const [restDescriptor, setRestDescriptor] = useState<GraphDescriptor | null>(null);
  // Per-run auto-approve-tools option (seeded from the run detail; toggled live).
  const [autoApprove,    setAutoApprove]    = useState(false);
  const [autoApproveBusy, setAutoApproveBusy] = useState(false);

  // A run is a coordinator CHILD when GET /api/runs/{id} returns a non-null parent_run_id.
  const isChild = parentRunId !== undefined && parentRunId !== null && parentRunId !== '';

  const openExecutionModal = useCallback((id: string) => setModalExecId(id), []);

  useEffect(() => {
    if (!projectId || !runId) return;
    let cancelled = false;

    // Resolve the team member's role title by cast name (best-effort).
    const applyRoleTitle = (name: string | undefined, teamData: TeamDto) => {
      if (!name) return;
      const member = teamData.members.find(
        m => m.name.toLowerCase() === name.toLowerCase()
      );
      if (member) setAgentRoleTitle(member.role_title);
    };

    Promise.all([
      apiClient.getProjectRuns(projectId),
      apiClient.getTeam(projectId),
    ]).then(async ([runs, teamData]) => {
        if (cancelled) return;
        setTeam(teamData);
        const run = runs.find((r) => r.workflow_run_id === runId);

        if (run) {
          const name = run.agent_name ?? undefined;
          setAgentName(name);
          setRunStatus(run.status       ?? undefined);
          setReviewedBy(run.reviewed_by ?? undefined);
          setExecutionId(run.execution_id ?? undefined);
          setModelId(run.model_id ?? undefined);
          applyRoleTitle(name, teamData);

          // Fetch the run detail (GET /api/runs/{id}) to learn whether this is a coordinator
          // child (parent_run_id) and to get the authoritative terminal status. The list
          // endpoint above does not carry parent_run_id.
          const execId = run.execution_id;
          if (execId) {
            try {
              const detail = await apiClient.getRun(execId);
              if (cancelled) return;
              setParentRunId(detail.parent_run_id ?? undefined);
              if (detail.status) setRunStatus(detail.status);
              setAutoApprove(Boolean(detail.auto_approve_tools));
            } catch { /* parent_run_id unavailable — treat as a non-child run */ }
          }
          return;
        }

        // FIX (coordinator child View-run) — the run is NOT in the project list because
        // the server filters that list to parent runs (parent_run_id IS NULL). A child
        // run is reachable via GET /api/runs/{id}, which returns parent_run_id, status,
        // agent_name and model_source. For a child, the child RunId IS the stream/graph
        // key, so set executionId = runId directly (mirrors the inline expand in
        // CoordinatorRunPage). Without this the page never resolved executionId and span
        // forever on a Pending full pipeline.
        try {
          const detail = await apiClient.getRun(runId);
          if (cancelled) return;
          const name = detail.agent_name ?? undefined;
          setExecutionId(runId);
          setParentRunId(detail.parent_run_id ?? undefined);
          setRunStatus(detail.status ?? undefined);
          setAgentName(name);
          setModelId(detail.model_source ?? undefined);
          setAutoApprove(Boolean(detail.auto_approve_tools));
          applyRoleTitle(name, teamData);
        } catch { /* run not resolvable — leave executionId unset */ }
      })
      .catch(() => { /* non-fatal */ })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, runId]);

  const { events: liveEvents, status: streamStatus, reconnect } = useRunStream(executionId ?? '', API_KEY, API_URL);

  // FIX 2 — seed the execution timeline for a finished/parked run from the persisted
  // events endpoint. A terminal child has a closed SSE stream, so without this the graph
  // reads all-"Pending" and the timeline is empty. Mirrors the topology REST-seed pattern.
  useEffect(() => {
    if (!executionId) { setSeedEvents([]); return; }
    if (!runStatus || !SEED_STATUSES.has(runStatus)) { setSeedEvents([]); return; }
    let cancelled = false;
    apiClient.getRunEvents(executionId)
      .then((persisted) => {
        if (cancelled) return;
        setSeedEvents(persisted.map((e) => ({
          sequence: e.sequence,
          type: e.type as EventType,
          payload: e.payload,
        })));
      })
      .catch(() => { /* endpoint may 404 if the durable log is absent — fall back to SSE */ });
    return () => { cancelled = true; };
  }, [executionId, runStatus]);

  // Fetch the graph descriptor for this execution; 404 → null → hardcoded fallback.
  useEffect(() => {
    if (!executionId) return;
    let cancelled = false;
    apiClient.getRunGraph(executionId)
      .then((desc) => { if (!cancelled) setRestDescriptor(desc); })
      .catch(() => { /* non-fatal; null remains → hardcoded fallback */ });
    return () => { cancelled = true; };
  }, [executionId]);

  // Fold the REST seed under the live SSE deltas: persisted events first, then any live
  // event not already present (dedupe by sequence; singleton seq-0 events by type).
  const events = useMemo<RunStreamEvent[]>(
    () => mergeRunEvents(seedEvents, liveEvents),
    [seedEvents, liveEvents],
  );

  // Extract the run outcome (report_outcome { achieved, reason }) from the event stream.
  const runOutcome = useMemo<{ achieved: boolean; reason: string } | undefined>(() => {
    for (let i = events.length - 1; i >= 0; i--) {
      if (events[i].type === 'run.outcome') {
        const p = events[i].payload;
        return { achieved: p['achieved'] as boolean, reason: String(p['reason'] ?? '') };
      }
    }
    return undefined;
  }, [events]);

  // Bubbled questions (agent.question_asked / agent.question_answered). Pair by requestId so an
  // unanswered question renders a prominent answer box and an answered one collapses to a muted
  // state. Defensive payload key reads (requestId|request_id, etc.) tolerate backend casing.
  const questionItems = useMemo<Array<{ requestId: string; question: string; answer?: string; timedOut?: boolean; seq: number }>>(() => {
    const asked = new Map<string, { question: string; seq: number }>();
    const answered = new Map<string, { answer: string; timedOut: boolean }>();
    for (const evt of events) {
      const p = evt.payload;
      if (evt.type === 'agent.question_asked') {
        const requestId = String(p['requestId'] ?? p['request_id'] ?? '');
        if (!requestId) continue;
        asked.set(requestId, { question: String(p['question'] ?? ''), seq: evt.sequence });
      } else if (evt.type === 'agent.question_answered') {
        const requestId = String(p['requestId'] ?? p['request_id'] ?? '');
        if (!requestId) continue;
        answered.set(requestId, {
          answer: String(p['answer'] ?? ''),
          timedOut: Boolean(p['timedOut'] ?? p['timed_out'] ?? false),
        });
      }
    }
    return Array.from(asked.entries())
      .map(([requestId, a]) => {
        const ans = answered.get(requestId);
        return { requestId, question: a.question, answer: ans?.answer, timedOut: ans?.timedOut, seq: a.seq };
      })
      .sort((x, y) => x.seq - y.seq);
  }, [events]);

  // Extract the first run.degraded event — sandbox blocked at least one tool call.
  const runDegraded = useMemo<{ toolName: string; reason: string } | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'run.degraded') {
        const p = evt.payload;
        return { toolName: String(p['toolName'] ?? ''), reason: String(p['reason'] ?? 'Sandbox denied a tool call') };
      }
    }
    return undefined;
  }, [events]);

  // Derive executor states from SSE workflow.step events plus semantic review/merge events.
  const executorStates = useMemo<Record<string, ExecutorState>>(() => {
    const map: Record<string, ExecutorState> = {};
    for (const evt of events) {
      if (evt.type === 'workflow.step') {
        const step      = String(evt.payload['step'] ?? '');
        const evtStatus = String(evt.payload['status'] ?? 'started') as StepStatus;
        const evtAgent  = evt.payload['agent_name'] != null ? String(evt.payload['agent_name']) : undefined;
        const evtReviewer = evt.payload['reviewer'] != null ? String(evt.payload['reviewer']) : undefined;
        const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
        const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
        const evtMessage = evt.payload['message'] != null ? String(evt.payload['message']) : undefined;
        const prev = map[step];
        const newState: ExecutorState = { status: evtStatus, agentName: evtAgent ?? prev?.agentName, reviewer: evtReviewer ?? prev?.reviewer, message: evtMessage };
        if (evtStatus === 'started') {
          newState.startedAt = !isNaN(tsMs) ? tsMs : undefined;
        } else {
          newState.startedAt = prev?.startedAt;
          if (!isNaN(tsMs)) newState.completedAt = tsMs;
        }
        map[step] = newState;
      } else if (evt.type === 'review.changes_requested') {
        // Belt-and-suspenders: backend now emits workflow.step for review, but handle the
        // semantic event too so older in-flight runs update correctly.
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'revise', completedAt: Date.now() };
        }
      } else if (evt.type === 'review.approved') {
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'completed', completedAt: Date.now() };
        }
      } else if (evt.type === 'review.declined') {
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'failed', completedAt: Date.now() };
        }
        if (!map['merge']) {
          map['merge'] = { status: 'skipped' };
        }
      } else if (evt.type === 'merge.completed') {
        if (!map['merge'] || map['merge'].status === 'started') {
          map['merge'] = { ...map['merge'], status: 'completed' };
        }
      } else if (evt.type === 'merge.failed') {
        if (!map['merge'] || map['merge'].status === 'started') {
          map['merge'] = { ...map['merge'], status: 'failed' };
        }
      } else if (evt.type === 'run.assemble_ready' || evt.type === 'subtask.assemble_ready') {
        // CHILD pipeline terminal: the child finished agent + RAI and is parked awaiting
        // collective assembly. Mark the assemble-ready node complete.
        const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
        const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
        map['assemble-ready'] = { status: 'completed', completedAt: !isNaN(tsMs) ? tsMs : Date.now() };
      } else if (evt.type === 'agent.intent') {
        // Track the latest intent message so the agent card shows real progress text.
        const intentText = evt.payload['intent'] != null ? String(evt.payload['intent']) : undefined;
        if (intentText) {
          const prev = map['agent'];
          map['agent'] = { ...prev, status: prev?.status ?? 'started', intent: intentText };
        }
      }
    }

    // Optimistic: if stream is done and merge completed but no scribe events arrived,
    // treat as skipped (scribe was skipped due to missing project/agent context, not completed).
    const streamDone = streamStatus === 'done' || streamStatus === 'error';
    if (!isChild && streamDone && map['merge']?.status === 'completed' && !map['scribe']) {
      map['scribe'] = { status: 'skipped' };
    }

    // CHILD pipeline: agent → RAI → assemble-ready only. When the run is finished/parked
    // (terminal status or closed stream) and the explicit assemble_ready event did not
    // arrive, infer the trimmed pipeline progress so the child shows execution rather than
    // all-"Pending". Review/Merge/Scribe are intentionally never set for a child.
    if (isChild) {
      const childParked = streamDone || (runStatus !== undefined && SEED_STATUSES.has(runStatus));
      if (childParked) {
        if (!map['agent']) {
          map['agent'] = { status: runStatus === 'failed' ? 'failed' : 'completed', agentName };
        }
        if (map['agent']?.status === 'completed') {
          if (!map['rai']) map['rai'] = { status: 'completed' };
          if (!map['assemble-ready'] && map['rai']?.status === 'completed') {
            map['assemble-ready'] = { status: 'completed' };
          }
        }
      }
      return map;
    }

    // Optimistic: if stream is done and agent completed but no review/merge events arrived,
    // the run had no changes — both steps were skipped by the workflow.
    if (streamDone && map['agent']?.status === 'completed' && !map['review'] && !map['merge']) {
      map['review'] = { status: 'skipped' };
      map['merge']  = { status: 'skipped' };
    }

    // Fallback: stream done but no step events — infer from terminal run status.
    const hasStepEvents = Object.keys(map).length > 0;
    if (!hasStepEvents && streamDone && runStatus) {
      const isTerminal = ['merged', 'declined', 'merge_failed', 'failed', 'completed'].includes(runStatus);
      if (isTerminal) {
        const agentState: StepStatus = runStatus === 'failed' ? 'failed' : 'completed';
        map['agent'] = { status: agentState, agentName };
        if (runStatus !== 'failed') {
          map['rai']    = { status: 'completed' };
          map['scribe'] = { status: 'completed' };
        }
        if (runStatus === 'merged') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'completed' };
        } else if (runStatus === 'declined') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'skipped' };
        } else if (runStatus === 'merge_failed') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'failed' };
        } else if (runStatus === 'completed') {
          map['review'] = { status: 'skipped' };
          map['merge']  = { status: 'skipped' };
        }
      }
    }

    return map;
  }, [events, streamStatus, runStatus, agentName, isChild]);

  // Determine which loopback arc is "active" (lit in blue) based on executor states.
  // rai→agent: active when Rai requested a revision and agent is now running again.
  // review→agent: active when review completed via request-changes and agent is running.
  const activeLoopbackId = useMemo<string | undefined>(() => {
    const raiStatus    = executorStates['rai']?.status;
    const agentStatus  = executorStates['agent']?.status;
    const mergeStatus  = executorStates['merge']?.status;
    if (raiStatus === 'revise' && agentStatus === 'started') return 'rai-agent-revise';
    // review→agent: review requested changes but no merge started → request-changes loop
    if (agentStatus === 'started' && !mergeStatus && executorStates['review']?.status === 'revise') {
      return 'review-agent-change';
    }
    return undefined;
  }, [executorStates]);

  // SSE run.workflow_graph snapshot: highest-seq-wins over REST seed.
  const sseDescriptor = useMemo<GraphDescriptor | undefined>(() => {
    let best: { seq: number; desc: GraphDescriptor } | undefined;
    for (const evt of events) {
      if (evt.type === 'run.workflow_graph') {
        const seq = typeof evt.payload['seq'] === 'number' ? evt.payload['seq'] : 0;
        if (!best || seq >= best.seq) {
          best = { seq, desc: evt.payload as unknown as GraphDescriptor };
        }
      }
    }
    return best?.desc;
  }, [events]);

  // SSE wins over REST; null/undefined → hardcoded fallback.
  const effectiveDescriptor: GraphDescriptor | null | undefined = sseDescriptor ?? restDescriptor;

  // Build React Flow nodes + edges. When a descriptor is available, use it; otherwise fall
  // back to the hardcoded executor lists. Only non-loopback edges are fed to dagre so it
  // never tries to rank a cycle; loopback edges are drawn as back-arcs separately.
  const { rfNodes, displayEdges } = useMemo<{ rfNodes: Node[]; displayEdges: Edge[] }>(() => {
    // Defensive guard: for a child run, only use the descriptor when it has the child variant.
    // A stale full-variant descriptor arriving here would show the complete pipeline instead
    // of the trimmed agent → RAI → assemble-ready child pipeline.
    if (effectiveDescriptor && (!isChild || effectiveDescriptor.variant === 'child')) {
      const fwdEdges: Edge[] = [];
      const allEdges: Edge[] = [];
      for (const edge of effectiveDescriptor.edges) {
        const edgeId = `${edge.from}-${edge.to}`;
        if (edge.loopback) {
          const lbLabel =
            edge.from === 'rai'    && edge.to === 'agent' ? 'Revise'
          : edge.from === 'review' && edge.to === 'agent' ? 'Request changes'
          : '';
          allEdges.push(loopbackEdge(edgeId, edge.from, edge.to, lbLabel));
        } else {
          const e = forwardEdge(edgeId, edge.from, edge.to);
          fwdEdges.push(e);
          allEdges.push(e);
        }
      }
      const raw: Node[] = effectiveDescriptor.nodes.map((node) => {
        const planned = node.kind === 'planned';
        const st: ExecutorState = planned
          ? { status: 'pending' }
          : (executorStates[node.id] ?? { status: 'pending' });
        const def: ExecutorDef = {
          key:             node.id as ExecutorKey,
          label:           node.label,
          roleDescription: roleDescForRole(node.role),
          Icon:            iconForRole(node.role),
        };
        return {
          id:   node.id,
          type: 'workflow',
          data: {
            def,
            state:          st,
            isPlanned:      planned,
            nodeType:       node.node_type,
            agentName:      node.id === 'agent' ? (executorStates['agent']?.agentName ?? agentName) : undefined,
            agentRoleTitle: node.id === 'agent' ? agentRoleTitle : undefined,
            modelId:        node.id === 'agent' ? modelId : undefined,
            runId:          runId      ?? '',
            executionId:    executionId ?? '',
            projectId:      projectId  ?? '',
            reviewedBy:     node.id === 'review' ? (executorStates['review']?.reviewer ?? reviewedBy) : undefined,
            runOutcome:     node.id === 'agent' ? runOutcome : undefined,
            runDegraded:    node.id === 'agent' ? runDegraded : undefined,
          } as WorkflowNodeData,
          position: { x: 0, y: 0 },
        };
      });
      const nodeSizeHints = Object.fromEntries(
        effectiveDescriptor.nodes.map(n => {
          const nt = n.node_type;
          return [
            n.id,
            { width: NODE_TYPE_W[nt ?? ''] ?? NODE_W, height: NODE_TYPE_H[nt ?? ''] ?? NODE_H },
          ];
        })
      );
      return {
        rfNodes:      layoutDag(raw, fwdEdges, { rankdir: 'LR', rankSep: 60, nodeSep: 30 }, nodeSizeHints),
        displayEdges: allEdges,
      };
    }

    // --- Fallback: hardcoded executor lists (full or child variant) ---
    const fallbackDefs  = isChild ? CHILD_EXECUTORS    : EXECUTORS;
    const fallbackFwd   = isChild ? CHILD_FORWARD_EDGES : FORWARD_EDGES;
    const fallbackEdges = isChild ? CHILD_EDGES         : ALL_EDGES;
    const raw: Node[] = fallbackDefs.map((def) => ({
      id:   def.key,
      type: 'workflow',
      data: {
        def,
        state:          executorStates[def.key] ?? { status: 'pending' },
        agentName:      def.key === 'agent' ? (executorStates['agent']?.agentName ?? agentName) : undefined,
        agentRoleTitle: def.key === 'agent' ? agentRoleTitle : undefined,
        modelId:        def.key === 'agent' ? modelId : undefined,
        runId:          runId      ?? '',
        executionId:    executionId ?? '',
        projectId:      projectId  ?? '',
        reviewedBy:     def.key === 'review' ? (executorStates['review']?.reviewer ?? reviewedBy) : undefined,
        runOutcome:     def.key === 'agent' ? runOutcome : undefined,
        runDegraded:    def.key === 'agent' ? runDegraded : undefined,
      } as WorkflowNodeData,
      position: { x: 0, y: 0 },
    }));
    return {
      rfNodes:      layoutDag(raw, fallbackFwd, { rankdir: 'LR', rankSep: 60, nodeSep: 30 }),
      displayEdges: fallbackEdges,
    };
  }, [effectiveDescriptor, isChild, executorStates, agentName, agentRoleTitle, modelId, reviewedBy, executionId, runId, projectId, runOutcome, runDegraded]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId      = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting = streamStatus === 'connecting';
  const projectName  = team?.project_name ?? projectId;
  // The auto-approve endpoint 409s on a non-active run, so only offer the toggle while active.
  const runActive    = runStatus !== undefined && !SEED_STATUSES.has(runStatus);
  const toggleTarget = executionId ?? runId;

  const toggleAutoApprove = (next: boolean) => {
    if (autoApproveBusy) return;
    setAutoApprove(next);          // optimistic
    setAutoApproveBusy(true);
    apiClient.setAutoApprove(toggleTarget, next)
      .then((res) => setAutoApprove(Boolean(res.auto_approve_tools)))
      .catch(() => setAutoApprove(!next))   // revert on error
      .finally(() => setAutoApproveBusy(false));
  };

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{projectName}</Link>
        <span aria-hidden="true">/</span>
        <span>Run {shortId}</span>
      </nav>

      {/* Header */}
      <div className={styles.headerRow}>
        <Title2>Run</Title2>
        <span className={styles.runIdLabel}>{shortId}</span>
        {(loading || isConnecting) && <Spinner size="extra-tiny" aria-label="Loading" />}
        {!isChild && runActive && (
          <Tooltip
            content="Auto-approve tool permission requests for this run. Dangerous tools remain blocked by policy."
            relationship="description"
          >
            <Switch
              checked={autoApprove}
              disabled={autoApproveBusy}
              onChange={(_, d) => toggleAutoApprove(d.checked)}
              label="Auto-approve tools"
              labelPosition="before"
            />
          </Tooltip>
        )}
      </div>

      {/* Bubbled questions — a blocked worker awaiting an answer renders a prominent inline
          answer card; once answered (or optimistically applied) it collapses to a muted state.
          Answers POST against this run id (apiClient.answerQuestion). */}
      {questionItems.length > 0 && (
        <div aria-label="Questions awaiting an answer">
          {questionItems.map((q) => (
            <QuestionAnswerCard
              key={q.requestId}
              runId={runId}
              requestId={q.requestId}
              question={q.question}
              answer={q.answer}
              timedOut={q.timedOut}
            />
          ))}
        </div>
      )}

      {/* React Flow diagram wrapped in contexts so WorkflowNode can open the modal and arc highlighting works */}
      <ExecutionModalContext.Provider value={openExecutionModal}>
      <ActiveEdgeContext.Provider value={activeLoopbackId}>
        {/* While the run detail is still resolving we do NOT know whether this is a coordinator
            child yet. Rendering the graph here would flash the full agent→…→scribe placeholder
            for a child run (whose real pipeline is the trimmed agent→RAI→assemble-ready). Show a
            loading state until child-ness is known, then render the correct (trimmed or full) graph. */}
        {loading ? (
          <div className={styles.dagLoading} aria-label="Loading run graph" aria-busy="true">
            <Spinner size="small" label="Loading run…" />
          </div>
        ) : (
        <>
          <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
          <div className={styles.dagContainer} ref={viewportRef}>
            <div style={{ zoom, width: '100%', height: '100%' }}>
              <ReactFlow
                nodes={rfNodes}
                edges={displayEdges}
                nodeTypes={workflowNodeTypes}
                edgeTypes={workflowEdgeTypes}
                fitView
                fitViewOptions={{ padding: 0.15, maxZoom: 1.1 }}
                minZoom={0.5}
                nodesDraggable={false}
                nodesConnectable={false}
                nodesFocusable={false}
                edgesFocusable={false}
                panOnScroll={false}
                zoomOnScroll={false}
                zoomOnPinch={false}
                zoomOnDoubleClick={false}
                panOnDrag={false}
                proOptions={{ hideAttribution: true }}
              />
            </div>
          </div>
        </>
        )}
      </ActiveEdgeContext.Provider>
      </ExecutionModalContext.Provider>

      {/* Execution detail modal */}
      <Dialog open={!!modalExecId} onOpenChange={(_, d) => { if (!d.open) setModalExecId(undefined); }}>
        <DialogSurface style={{ maxWidth: '90vw', width: '900px', maxHeight: '90vh' }}>
          <DialogBody style={{ height: '80vh', display: 'flex', flexDirection: 'column' }}>
            <DialogTitle
              action={
                <Button
                  appearance="subtle"
                  aria-label="Close"
                  icon={<DismissRegular />}
                  onClick={() => setModalExecId(undefined)}
                />
              }
            >
              Execution {modalExecId ? modalExecId.slice(0, 8) : ''}
            </DialogTitle>
            <DialogContent style={{ flex: 1, overflow: 'hidden', padding: 0, display: 'flex', flexDirection: 'column' }}>
              {modalExecId && <RunWatcher key={modalExecId} runId={modalExecId} style={{ height: '100%' }} onReviewAction={reconnect} />}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setModalExecId(undefined)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}