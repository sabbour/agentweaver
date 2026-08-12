import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Dropdown,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuDivider,
  MenuGroup,
  MenuGroupHeader,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Option,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowJoinRegular,
  ArrowSplitRegular,
  ArrowUploadRegular,
  BeakerRegular,
  BranchRegular,
  CheckmarkCircleRegular,
  CodeRegular,
  DeleteRegular,
  DismissRegular,
  EyeRegular,
  FlagRegular,
  FlowchartRegular,
  PeopleTeamRegular,
  PersonFeedbackRegular,
  PersonRegular,
  ShieldCheckmarkRegular,
  SparkleRegular,
  TextBulletListLtrRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { EmptyState, PageHeader } from './ui';
import { ScheduleTriggerDialog } from './ScheduleTriggerDialog';
import { DAG_NODE_SEP,
  layoutDag,
  workflowNodeSizeHint } from '../utils/dagLayout';
import { addEdge,
  addNode,
  AUTHORABLE_WORKFLOW_NODE_TYPES,
  getEventTrigger,
  getScheduleTrigger,
  NODE_TYPE_LABELS,
  parseWorkflowYaml,
  readWorkflowId,
  removeEdgeAt,
  removeNode,
  renameNode,
  scheduleTriggerLabel,
  setBranchTarget,
  setEdgeFieldAt,
  setHeaderField,
  setNodeField,
  setNodeStringArrayField,
  setScheduleTrigger,
  } from '../utils/workflowYaml';
import { ActiveEdgeContext,
  ExecutionModalContext,
  forwardEdge,
  iconForRole,
  loopbackEdge,
  roleDescForRole,
  workflowEdgeTypes,
  workflowNodeTypes,
  } from './WorkflowGraphPanel';
import '@xyflow/react/dist/style.css';
import {
  applyNodeChanges,
  Background,
  Controls,
  ReactFlow,
  useReactFlow,
} from '@xyflow/react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ComponentType } from 'react';
import type { GraphNodeType, WorkflowDetailDto } from '../api/types';
import type { WfModel, WfNode } from '../utils/workflowYaml';
import type { WorkflowNodeData } from './WorkflowGraphPanel';
import type { Connection, Edge, Node, NodeChange, OnSelectionChangeParams } from '@xyflow/react';
// US8 — visual execution-graph workflow editor. Extends the read-only ReactFlow
// render (US6) into a writeable canvas. The on-disk YAML remains the single source
// of truth: graph edits serialize back onto the YAML document (preserving unknown
// fields), and editing the YAML re-derives the graph. Save PUTs the YAML, identical
// to the WorkflowEditor (US7).

export interface VisualWorkflowEditorProps {
  projectId: string;
  workflowId: string;
  initialYaml: string;
  onSave?: (workflow: WorkflowDetailDto) => void;
  onClose?: () => void;
}

// Map a canonical node type onto the shared WorkflowNode visual (role key + node_type shape).
const TYPE_ROLE: Record<string, string> = {
  prompt: 'agent',
  peer_review: 'review',
  build_test: 'review',
  open_pull_request: 'action',
  publish: 'agent',
  check: 'rai',
  fan_out: 'subtask',
  fan_in: 'assembly',
  coordinator_composed: 'coordinator',
  serial: 'agent',
  merge: 'merge',
  scribe: 'scribe',
  terminal: 'assembly',
};

const TYPE_GRAPHNODE: Record<string, GraphNodeType> = {
  prompt: 'agent',
  peer_review: 'gate',
  build_test: 'gate',
  open_pull_request: 'action',
  publish: 'action',
  check: 'gate',
  fan_out: 'action',
  fan_in: 'action',
  coordinator_composed: 'subtask',
  serial: 'action',
  merge: 'action',
  scribe: 'action',
  terminal: 'terminal',
};

// Node types whose `agent` field is meaningful (FR-045 type-aware authoring).
const AGENT_TYPES = new Set(['prompt', 'peer_review', 'build_test', 'coordinator_composed', 'publish']);
const READONLY_NODE_TYPES = new Set(['merge', 'scribe']);

const SPECIAL_GATES = [
  {
    key: 'rai',
    label: 'RAI Check',
    type: 'check',
    gate_kind: 'rai',
    role: 'review',
    kind: 'gate',
    branches: ['revise', 'safety-failed', 'no-changes', 'review'],
    Icon: ShieldCheckmarkRegular,
    description: 'Responsible-AI safety gate on the latest changes.',
  },
  {
    key: 'rubberduck',
    label: 'Rubberduck Review',
    type: 'check',
    gate_kind: 'rubberduck',
    role: 'review',
    kind: 'gate',
    branches: ['pass', 'revise'],
    Icon: PersonFeedbackRegular,
    description: 'Lightweight self-review checkpoint before proceeding.',
  },
  {
    key: 'human-review',
    label: 'Human Review',
    type: 'check',
    gate_kind: 'human-review',
    role: 'review',
    kind: 'gate',
    branches: ['approved', 'request-changes', 'declined'],
    Icon: PersonRegular,
    description: 'Pause for a human approve / request-changes decision.',
  },
  {
    key: 'build-test',
    label: 'Build & Test',
    type: 'build_test',
    role: 'review',
    kind: 'live',
    agent: 'qa-engineer',
    branches: ['approved', 'request-changes', 'declined'],
    Icon: BeakerRegular,
    description: 'Run build + tests as a live QA gate (qa-engineer).',
  },
] as const;

const DEFAULT_BRANCHES: Record<string, string[]> = Object.fromEntries(
  SPECIAL_GATES.map((g) => [g.key, [...g.branches]]),
);

// Groups the "Add node" palette buckets primitives into (FR-050 authoring UX, #558).
type NodePaletteGroup = 'gates' | 'steps' | 'actions' | 'flow';

// Per-primitive palette metadata: a scannable icon + a one-line, plain-language
// description + the group header it sits under. `build_test` is deliberately absent:
// it is fully represented by the "Build & Test" preset in SPECIAL_GATES, and listing
// the raw type again produced a confusing duplicate "Build & Test" row (#558). Users
// who want a build/test step add the preset and edit it in the inspector.
const NODE_TYPE_META: Record<string, { Icon: ComponentType; description: string; group: NodePaletteGroup }> = {
  prompt: { Icon: SparkleRegular, description: 'A single agent turn that produces work.', group: 'steps' },
  peer_review: { Icon: PeopleTeamRegular, description: "Another agent reviews the previous step's output.", group: 'gates' },
  check: { Icon: CheckmarkCircleRegular, description: 'Generic verdict gate that branches on an outcome.', group: 'gates' },
  open_pull_request: { Icon: BranchRegular, description: 'Open a pull request on the connected GitHub repository.', group: 'actions' },
  publish: { Icon: ArrowUploadRegular, description: 'Package or deliver approved output with an agent turn.', group: 'actions' },
  fan_out: { Icon: ArrowSplitRegular, description: 'Split work into parallel subtasks.', group: 'flow' },
  fan_in: { Icon: ArrowJoinRegular, description: 'Gather parallel subtask results back together.', group: 'flow' },
  coordinator_composed: { Icon: FlowchartRegular, description: 'Delegate to a nested coordinator sub-workflow.', group: 'flow' },
  serial: { Icon: TextBulletListLtrRegular, description: 'Run a fixed sequence of steps in order.', group: 'flow' },
  terminal: { Icon: FlagRegular, description: 'Terminal end-state for a branch of the workflow.', group: 'flow' },
};

function gateKey(node: WfNode): string | null {
  if (node.type === 'build_test') return 'build-test';
  if (node.type === 'check' && node.gate_kind) return node.gate_kind;
  return null;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
  },
  identityGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: tokens.spacingHorizontalM,
    flexGrow: 1,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr' },
  },
  identityWide: {
    gridColumn: '1 / -1',
    '@media (max-width: 760px)': { gridColumn: 'auto' },
  },
  scheduleRow: {
    gridColumn: '1 / -1',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 760px)': {
      gridColumn: 'auto',
      alignItems: 'stretch',
      flexDirection: 'column',
    },
  },
  scheduleSummary: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  split: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    minHeight: '560px',
    '@media (max-width: 900px)': {
      flexDirection: 'column',
      minHeight: 0,
    },
  },
  canvasPane: {
    flexBasis: '60%',
    flexGrow: 1,
    overflow: 'hidden',
    position: 'relative',
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    '@media (max-width: 900px)': { minHeight: '420px' },
  },
  sidePane: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    flexBasis: '40%',
    overflowY: 'auto',
    maxHeight: '560px',
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    '@media (max-width: 900px)': {
      flexBasis: 'auto',
      maxHeight: 'none',
    },
  },
  paneHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    justifyContent: 'space-between',
  },
  canvasToolbar: {
    position: 'absolute',
    top: tokens.spacingVerticalS,
    left: tokens.spacingHorizontalS,
    zIndex: 5,
  },
  yamlArea: {
    flexGrow: 1,
    width: '100%',
    minHeight: '480px',
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
    padding: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
    resize: 'vertical',
    outline: 'none',
    boxSizing: 'border-box',
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  footerActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginLeft: 'auto',
  },
  branchStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  statusWarning: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorStatusWarningForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  hintText: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  menuItemContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    maxWidth: '320px',
    whiteSpace: 'normal',
  },
  menuItemTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  menuItemDescription: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
});

function parseApiError400(err: unknown): { message: string; line: number | null } {
  if (!(err instanceof ApiError) || err.status !== 400) {
    const msg = err instanceof Error ? err.message : String(err);
    return { message: msg, line: null };
  }
  try {
    const parsed = JSON.parse(err.body) as { error?: string; line?: number | null };
    return { message: parsed.error ?? err.body, line: parsed.line ?? null };
  } catch {
    return { message: err.body, line: null };
  }
}

/** Build ReactFlow nodes/edges from the model, preserving any manually-dragged positions. */
function buildGraph(
  model: WfModel,
  positions: Map<string, { x: number; y: number }>,
): { rfNodes: Node[]; rfEdges: Edge[] } {
  const order = new Map(model.nodes.map((n, i) => [n.id, i]));

  const rfEdges: Edge[] = model.edges.map((e, i) => {
    const back = (order.get(e.to) ?? 0) <= (order.get(e.from) ?? 0);
    const id = `e${i}`;
    if (back) {
      return { ...loopbackEdge(id, e.from, e.to, e.when ?? ''), data: { index: i } };
    }
    const fe = forwardEdge(id, e.from, e.to);
    return { ...fe, label: e.when || undefined, data: { index: i } };
  });

  const forwardOnly = rfEdges.filter((e) => e.type !== 'loopback');
  const hints: Record<string, { width: number; height: number }> = {};
  const raw: Node[] = model.nodes.map((n) => {
    const role = TYPE_ROLE[n.type] ?? 'agent';
    const gnt = TYPE_GRAPHNODE[n.type] ?? 'action';
    hints[n.id] = workflowNodeSizeHint(gnt);
    return {
      id: n.id,
      type: 'workflow',
      position: { x: 0, y: 0 },
      data: {
        def: {
          key: role,
          label: n.label || n.id,
          roleDescription: roleDescForRole(role),
          Icon: iconForRole(role),
        },
        state: { status: 'pending' },
        nodeType: gnt,
        isPlanned: true,
        connectable: true,
        interactionTestId: `workflow-node-${n.id}`,
        handleTestIdPrefix: `workflow-node-${n.id}-handle`,
      } as WorkflowNodeData,
    };
  });

  const laid = layoutDag(raw, forwardOnly, { rankdir: 'LR', rankSep: 80, nodeSep: DAG_NODE_SEP }, hints);
  const rfNodes = laid.map((n) => {
    const p = positions.get(n.id);
    return p ? { ...n, position: p } : n;
  });
  return { rfNodes, rfEdges };
}

/** Verdicts a check/gate node declares that have no outgoing `when` edge (check-completeness, FR-052). */
function unroutedVerdicts(model: WfModel): { nodeId: string; verdicts: string[] }[] {
  const result: { nodeId: string; verdicts: string[] }[] = [];
  for (const n of model.nodes) {
    const key = gateKey(n);
    const branches = n.type === 'build_test' ? DEFAULT_BRANCHES['build-test'] : n.branches;
    if (!key || !branches || branches.length === 0) continue;
    const routed = new Set(
      model.edges.filter((e) => e.from === n.id && e.when).map((e) => e.when as string),
    );
    const missing = branches.filter((b) => !routed.has(b));
    if (missing.length > 0) result.push({ nodeId: n.id, verdicts: missing });
  }
  return result;
}

// React Flow's `fitView` prop only fits the viewport once, on initial mount — it does
// not re-run when `nodes` is later replaced (e.g. via syncGraph after handleAddNode /
// handleAddSpecialGate write a new node into the YAML). Without this, a newly-added
// node positioned outside the current viewport by buildGraph/layoutDag renders behind
// the canvas pane's `overflow: hidden`, looking like the "Add node" click did nothing.
//
// This helper re-fits imperatively, but only when the node *count* grows — not on
// every `nodes` change — so unrelated edits (renaming a node's label or its id,
// reconnecting edges, dragging a node) don't steal the user's current pan/zoom
// position. A count increase is a precise enough signal for "a node was added":
// renames/reconnects preserve the count, and deletions decrease it (React Flow
// already keeps deleted nodes on-screen, so no re-fit is needed there either).
// Rendered as a child of <ReactFlow>, which exposes its internal provider context to
// descendants, so no extra <ReactFlowProvider> wrapper is needed here.
function FitViewOnNodeAdded({ nodes }: { nodes: Node[] }) {
  const { fitView } = useReactFlow();
  const prevCountRef = useRef<number | null>(null);

  useEffect(() => {
    const prevCount = prevCountRef.current;
    prevCountRef.current = nodes.length;
    if (prevCount === null || nodes.length <= prevCount) {
      // First run (mount-time `fitView` prop already handled it) or no growth.
      return;
    }
    // Defer to the next frame so the newly-added node's DOM measurements are ready.
    requestAnimationFrame(() => {
      void fitView({ padding: 0.2, maxZoom: 1.1, duration: 300 });
    });
  }, [nodes, fitView]);

  return null;
}

export function VisualWorkflowEditor({
  projectId,
  workflowId,
  initialYaml,
  onSave,
  onClose,
}: VisualWorkflowEditorProps) {
  const styles = useStyles();

  const [yamlText, setYamlText] = useState(initialYaml);
  const [nodes, setNodes] = useState<Node[]>([]);
  const [edges, setEdges] = useState<Edge[]>([]);
  const [model, setModel] = useState<WfModel | null>(null);
  const [parseError, setParseError] = useState<string | null>(null);

  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [selectedEdgeIndex, setSelectedEdgeIndex] = useState<number | null>(null);
  const [rightMode, setRightMode] = useState<'inspector' | 'yaml'>('inspector');
  const [scheduleOpen, setScheduleOpen] = useState(false);

  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<{ message: string; line: number | null } | null>(null);

  const positionsRef = useRef<Map<string, { x: number; y: number }>>(new Map());
  const isDirty = yamlText !== initialYaml;
  const isDirtyRef = useRef(isDirty);
  useEffect(() => { isDirtyRef.current = isDirty; }, [isDirty]);

  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => { if (isDirtyRef.current) e.preventDefault(); };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, []);

  // Re-derive the graph whenever the canonical YAML changes (either surface).
  useEffect(() => {
    const syncGraph = async () => {
      const { model: parsed, error } = parseWorkflowYaml(yamlText);
      setParseError(error);
      if (parsed) {
        setModel(parsed);
        const { rfNodes, rfEdges } = buildGraph(parsed, positionsRef.current);
        setNodes(rfNodes);
        setEdges(rfEdges);
      }
    };
    void syncGraph();
  }, [yamlText]);

  const onNodesChange = useCallback((changes: NodeChange[]) => {
    setNodes((nds) => applyNodeChanges(changes, nds));
    for (const ch of changes) {
      if (ch.type === 'position' && ch.position) positionsRef.current.set(ch.id, ch.position);
    }
  }, []);

  const onConnect = useCallback((conn: Connection) => {
    if (!conn.source || !conn.target) return;
    setYamlText((t) => addEdge(t, conn.source as string, conn.target as string));
  }, []);

  const onNodesDelete = useCallback((deleted: Node[]) => {
    setYamlText((t) => deleted.reduce((acc, n) => removeNode(acc, n.id), t));
  }, []);

  const onEdgesDelete = useCallback((deleted: Edge[]) => {
    const indices = deleted
      .map((e) => (e.data as { index?: number } | undefined)?.index)
      .filter((i): i is number => typeof i === 'number')
      .sort((a, b) => b - a);
    setYamlText((t) => indices.reduce((acc, i) => removeEdgeAt(acc, i), t));
  }, []);

  const onSelectionChange = useCallback((params: OnSelectionChangeParams) => {
    setSelectedNodeId(params.nodes[0]?.id ?? null);
    const idx = (params.edges[0]?.data as { index?: number } | undefined)?.index;
    setSelectedEdgeIndex(typeof idx === 'number' ? idx : null);
  }, []);

  const handleAddNode = useCallback((type: string) => {
    setYamlText((t) => {
      const existing = new Set((parseWorkflowYaml(t).model?.nodes ?? []).map((n) => n.id));
      let i = 1;
      let id = `${type}-${i}`;
      while (existing.has(id)) { i += 1; id = `${type}-${i}`; }
      setSelectedNodeId(id);
      setSelectedEdgeIndex(null);
      setRightMode('inspector');
      return addNode(t, { id, type });
    });
  }, []);

  const handleAddSpecialGate = useCallback((gate: (typeof SPECIAL_GATES)[number]) => {
    setYamlText((t) => {
      const existing = new Set((parseWorkflowYaml(t).model?.nodes ?? []).map((n) => n.id));
      let i = 1;
      let id = `${gate.key}-${i}`;
      while (existing.has(id)) { i += 1; id = `${gate.key}-${i}`; }
      setSelectedNodeId(id);
      setSelectedEdgeIndex(null);
      setRightMode('inspector');
      return addNode(t, {
        id,
        type: gate.type,
        label: gate.label,
        role: gate.role,
        kind: gate.kind,
        gate_kind: 'gate_kind' in gate ? gate.gate_kind : undefined,
        agent: 'agent' in gate ? gate.agent : undefined,
        branches: gate.type === 'check' ? [...gate.branches] : undefined,
      });
    });
  }, []);

  const selectedNode = useMemo(
    () => model?.nodes.find((n) => n.id === selectedNodeId) ?? null,
    [model, selectedNodeId],
  );
  const selectedEdge = useMemo(
    () => (selectedEdgeIndex != null ? model?.edges[selectedEdgeIndex] ?? null : null),
    [model, selectedEdgeIndex],
  );
  const selectedReadOnly = !!selectedNode && READONLY_NODE_TYPES.has(selectedNode.type);
  const selectedGateKey = selectedNode ? gateKey(selectedNode) : null;
  const selectedGateBranches = selectedNode && selectedGateKey
    ? (selectedNode.type === 'build_test'
        ? DEFAULT_BRANCHES['build-test']
        : (selectedNode.branches ?? DEFAULT_BRANCHES[selectedGateKey] ?? []))
    : [];
  const selectableTargets = model?.nodes.filter((n) => n.id !== selectedNode?.id) ?? [];

  const warnings = useMemo(() => (model ? unroutedVerdicts(model) : []), [model]);
  const scheduleTrigger = useMemo(() => getScheduleTrigger(yamlText), [yamlText]);
  const eventTrigger = useMemo(() => getEventTrigger(yamlText), [yamlText]);

  const handleRenameNode = useCallback((oldId: string, newId: string) => {
    if (!newId || newId === oldId) return;
    const pos = positionsRef.current.get(oldId);
    if (pos) { positionsRef.current.delete(oldId); positionsRef.current.set(newId, pos); }
    setSelectedNodeId(newId);
    setYamlText((t) => renameNode(t, oldId, newId));
  }, []);

  const handleNodeField = useCallback((id: string, field: string, value: string) => {
    setYamlText((t) => setNodeField(t, id, field, value));
  }, []);

  const handleBranchesField = useCallback((id: string, branches: string[]) => {
    setYamlText((t) => setNodeStringArrayField(t, id, 'branches', branches));
  }, []);

  const handleBranchTarget = useCallback((id: string, branch: string, target: string) => {
    setYamlText((t) => setBranchTarget(t, id, branch, target));
  }, []);

  const handleDeleteSelectedNode = useCallback(() => {
    if (!selectedNodeId) return;
    const id = selectedNodeId;
    positionsRef.current.delete(id);
    setSelectedNodeId(null);
    setYamlText((t) => removeNode(t, id));
  }, [selectedNodeId]);

  const handleEdgeField = useCallback((index: number, field: string, value: string) => {
    setYamlText((t) => setEdgeFieldAt(t, index, field, value));
  }, []);

  const handleDeleteSelectedEdge = useCallback(() => {
    if (selectedEdgeIndex == null) return;
    const idx = selectedEdgeIndex;
    setSelectedEdgeIndex(null);
    setYamlText((t) => removeEdgeAt(t, idx));
  }, [selectedEdgeIndex]);

  const handleScheduleSave = useCallback((trigger: NonNullable<ReturnType<typeof getScheduleTrigger>>) => {
    setYamlText((text) => setScheduleTrigger(text, trigger));
    setSaveError(null);
    setScheduleOpen(false);
  }, []);

  const handleScheduleRemove = useCallback(() => {
    setYamlText((text) => setScheduleTrigger(text, null));
    setSaveError(null);
    setScheduleOpen(false);
  }, []);

  const handleSave = useCallback(async () => {
    setSaving(true);
    setSaveError(null);
    const id = readWorkflowId(yamlText, workflowId);
    try {
      const saved = await apiClient.saveWorkflowYaml(projectId, id, yamlText);
      onSave?.(saved);
    } catch (err) {
      setSaveError(parseApiError400(err));
    } finally {
      setSaving(false);
    }
  }, [projectId, workflowId, yamlText, onSave]);

  const handleClose = useCallback(() => {
    if (isDirtyRef.current && !window.confirm('You have unsaved changes. Close without saving?')) return;
    onClose?.();
  }, [onClose]);

  const renderPresetItem = (g: (typeof SPECIAL_GATES)[number]) => (
    <MenuItem key={g.key} icon={<g.Icon />} onClick={() => handleAddSpecialGate(g)}>
      <div className={styles.menuItemContent}>
        <span className={styles.menuItemTitle}>{g.label}</span>
        <span className={styles.menuItemDescription}>{g.description}</span>
      </div>
    </MenuItem>
  );

  const renderPrimitiveItems = (group: NodePaletteGroup) =>
    AUTHORABLE_WORKFLOW_NODE_TYPES.filter((t) => NODE_TYPE_META[t]?.group === group).map((t) => {
      const meta = NODE_TYPE_META[t];
      const Icon = meta.Icon;
      return (
        <MenuItem key={t} icon={<Icon />} onClick={() => handleAddNode(t)}>
          <div className={styles.menuItemContent}>
            <span className={styles.menuItemTitle}>{NODE_TYPE_LABELS[t] ?? t}</span>
            <span className={styles.menuItemDescription}>{meta.description}</span>
          </div>
        </MenuItem>
      );
    });

  return (
    <div className={styles.root}>
      <PageHeader
        title={model?.name || workflowId}
        description={model?.id ?? workflowId}
        actions={
          <Button
            appearance="subtle"
            size="small"
            icon={<DismissRegular />}
            onClick={handleClose}
            aria-label="Close"
          />
        }
      />
      <div className={styles.identityGrid}>
        <Field label="Workflow id">
          <Input
            value={model?.id ?? ''}
            onChange={(_, d) => setYamlText((t) => setHeaderField(t, 'id', d.value))}
          />
        </Field>
        <Field label="Name">
          <Input
            value={model?.name ?? ''}
            onChange={(_, d) => setYamlText((t) => setHeaderField(t, 'name', d.value))}
          />
        </Field>
        <Field
          label="Description"
          hint="The coordinator reads this to decide when to select this workflow."
          className={styles.identityWide}
        >
          <Textarea
            value={model?.description ?? ''}
            onChange={(_, d) => setYamlText((t) => setHeaderField(t, 'description', d.value))}
            rows={2}
          />
        </Field>
        <div className={styles.scheduleRow}>
          <div className={styles.scheduleSummary}>
            <Text weight="semibold">Schedule trigger</Text>
            {scheduleTrigger ? (
              <Badge appearance="tint" color="informative">
                {scheduleTriggerLabel(scheduleTrigger)}
              </Badge>
            ) : (
              <Text size={200}>{eventTrigger ? 'No schedule configured' : 'Manual only'}</Text>
            )}
          </div>
          <Button
            appearance="secondary"
            disabled={Boolean(parseError)}
            onClick={() => setScheduleOpen(true)}
          >
            {scheduleTrigger ? 'Edit schedule trigger' : 'Add schedule trigger'}
          </Button>
        </div>
      </div>

      {parseError && (
        <MessageBar intent="warning">
          <MessageBarBody>YAML not parseable — showing last valid graph. {parseError}</MessageBarBody>
        </MessageBar>
      )}

      {warnings.length > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {warnings.map((w) => `Gate "${w.nodeId}" has unrouted verdict(s): ${w.verdicts.join(', ')}`).join(' · ')}
          </MessageBarBody>
        </MessageBar>
      )}

      {saveError && (
        <MessageBar intent="error">
          <MessageBarBody>
            {saveError.line != null ? `Line ${saveError.line}: ${saveError.message}` : saveError.message}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.split}>
        <div className={styles.canvasPane} data-testid="workflow-canvas">
          <div className={styles.canvasToolbar} role="toolbar" aria-label="Workflow canvas actions">
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button appearance="primary" size="small" icon={<AddRegular />}>Add node</Button>
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuGroup>
                    <MenuGroupHeader>Reviewers &amp; gates</MenuGroupHeader>
                    {SPECIAL_GATES.map(renderPresetItem)}
                    {renderPrimitiveItems('gates')}
                  </MenuGroup>
                  <MenuDivider />
                  <MenuGroup>
                    <MenuGroupHeader>Agent steps</MenuGroupHeader>
                    {renderPrimitiveItems('steps')}
                  </MenuGroup>
                  <MenuDivider />
                  <MenuGroup>
                    <MenuGroupHeader>Actions</MenuGroupHeader>
                    {renderPrimitiveItems('actions')}
                  </MenuGroup>
                  <MenuDivider />
                  <MenuGroup>
                    <MenuGroupHeader>Flow control</MenuGroupHeader>
                    {renderPrimitiveItems('flow')}
                  </MenuGroup>
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
          <ExecutionModalContext.Provider value={undefined}>
            <ActiveEdgeContext.Provider value={undefined}>
              <ReactFlow
                nodes={nodes}
                edges={edges}
                nodeTypes={workflowNodeTypes}
                edgeTypes={workflowEdgeTypes}
                onNodesChange={onNodesChange}
                onConnect={onConnect}
                onNodesDelete={onNodesDelete}
                onEdgesDelete={onEdgesDelete}
                onSelectionChange={onSelectionChange}
                nodesConnectable
                elementsSelectable
                fitView
                fitViewOptions={{ padding: 0.2, maxZoom: 1.1 }}
                proOptions={{ hideAttribution: true }}
              >
                <Background />
                <Controls showInteractive={false} />
                <FitViewOnNodeAdded nodes={nodes} />
              </ReactFlow>
            </ActiveEdgeContext.Provider>
          </ExecutionModalContext.Provider>
        </div>

        <div className={styles.sidePane}>
          <div className={styles.paneHeader}>
            <Text weight="semibold">
              {rightMode === 'yaml' ? 'YAML' : 'Inspector'}
            </Text>
            <Button
              appearance="subtle"
              size="small"
              icon={rightMode === 'yaml' ? <EyeRegular /> : <CodeRegular />}
              onClick={() => setRightMode((m) => (m === 'yaml' ? 'inspector' : 'yaml'))}
            >
              {rightMode === 'yaml' ? 'Inspector' : 'View YAML'}
            </Button>
          </div>

          {rightMode === 'yaml' && (
            <textarea
              className={styles.yamlArea}
              value={yamlText}
              onChange={(e) => { setYamlText(e.target.value); setSaveError(null); }}
              spellCheck={false}
              aria-label="Workflow YAML"
            />
          )}

          {rightMode === 'inspector' && selectedNode && (
            <>
              {selectedReadOnly && (
                <MessageBar intent="info">
                  <MessageBarBody>Merge and Scribe are platform-owned tail steps. Existing definitions load, but these steps are read-only.</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Node id">
                <Input
                  defaultValue={selectedNode.id}
                  key={`id-${selectedNode.id}`}
                  disabled={selectedReadOnly}
                  onBlur={(e) => handleRenameNode(selectedNode.id, e.target.value.trim())}
                />
              </Field>
              <Field label="Type">
                <Dropdown
                  selectedOptions={[selectedNode.type]}
                  value={NODE_TYPE_LABELS[selectedNode.type] ?? selectedNode.type}
                  disabled={selectedReadOnly}
                  onOptionSelect={(_, d) => {
                    if (d.optionValue) handleNodeField(selectedNode.id, 'type', d.optionValue as string);
                  }}
                >
                  {AUTHORABLE_WORKFLOW_NODE_TYPES.map((t) => (
                    <Option key={t} value={t} text={NODE_TYPE_LABELS[t] ?? t}>
                      {NODE_TYPE_LABELS[t] ?? t}
                    </Option>
                  ))}
                </Dropdown>
              </Field>
              <Field label="Label">
                <Input
                  defaultValue={selectedNode.label ?? ''}
                  key={`label-${selectedNode.id}`}
                  disabled={selectedReadOnly}
                  onBlur={(e) => handleNodeField(selectedNode.id, 'label', e.target.value)}
                />
              </Field>
              {AGENT_TYPES.has(selectedNode.type) && (
                <Field label="Agent">
                  <Input
                    defaultValue={selectedNode.agent ?? ''}
                    key={`agent-${selectedNode.id}`}
                    disabled={selectedReadOnly}
                    onBlur={(e) => handleNodeField(selectedNode.id, 'agent', e.target.value)}
                  />
                </Field>
              )}
              {(selectedNode.type === 'prompt' || selectedNode.type === 'publish') && (
                <Field label="Prompt">
                  <Textarea
                    defaultValue={selectedNode.prompt ?? ''}
                    key={`prompt-${selectedNode.id}`}
                    rows={4}
                    disabled={selectedReadOnly}
                    onBlur={(e) => handleNodeField(selectedNode.id, 'prompt', e.target.value)}
                  />
                </Field>
              )}
              {(selectedNode.type === 'prompt' || selectedNode.type === 'peer_review') && (
                <Field label="Model" hint="Optional">
                  <Input
                    defaultValue={selectedNode.model ?? ''}
                    key={`model-${selectedNode.id}`}
                    disabled={selectedReadOnly}
                    onBlur={(e) => handleNodeField(selectedNode.id, 'model', e.target.value)}
                  />
                </Field>
              )}
              {(selectedNode.type === 'peer_review' || selectedNode.type === 'fan_in') && (
                <Field label="Target" hint="Id of the reviewed / joined node">
                  <Input
                    defaultValue={selectedNode.target ?? ''}
                    key={`target-${selectedNode.id}`}
                    disabled={selectedReadOnly}
                    onBlur={(e) => handleNodeField(selectedNode.id, 'target', e.target.value)}
                  />
                </Field>
              )}
              {selectedGateKey && (
                <Field
                  label="Branch routing"
                  hint={selectedNode.type === 'build_test'
                    ? 'Build & Test uses fixed verdicts and no prompt field.'
                    : 'Each declared branch should route to a target node.'}
                >
                  <div className={styles.branchStack}>
                    {selectedNode.type === 'check' && (
                      <Input
                        value={selectedGateBranches.join(', ')}
                        aria-label="Gate branches"
                        onChange={(_, d) => handleBranchesField(
                          selectedNode.id,
                          d.value.split(',').map((b) => b.trim()).filter(Boolean),
                        )}
                      />
                    )}
                    {selectedGateBranches.map((branch) => {
                      const current = model?.edges.find((e) => e.from === selectedNode.id && e.when === branch)?.to ?? '';
                      return (
                        <Field key={branch} label={branch}>
                          <Dropdown
                            selectedOptions={current ? [current] : []}
                            value={current}
                            placeholder="Select target"
                            onOptionSelect={(_, d) => handleBranchTarget(
                              selectedNode.id,
                              branch,
                              d.optionValue as string,
                            )}
                          >
                            {selectableTargets.map((n) => (
                              <Option key={n.id} value={n.id} text={n.label || n.id}>
                                {n.label || n.id}
                              </Option>
                            ))}
                          </Dropdown>
                        </Field>
                      );
                    })}
                  </div>
                </Field>
              )}
              {!selectedReadOnly && (
                <Button appearance="secondary" icon={<DeleteRegular />} onClick={handleDeleteSelectedNode}>
                  Delete node
                </Button>
              )}
            </>
          )}

          {rightMode === 'inspector' && !selectedNode && selectedEdge && selectedEdgeIndex != null && (
            <>
              <Text className={styles.hintText}>{selectedEdge.from} → {selectedEdge.to}</Text>
              <Field label="When" hint="The verdict/predicate this edge fires on (empty = unconditional).">
                <Input
                  defaultValue={selectedEdge.when ?? ''}
                  key={`when-${selectedEdgeIndex}`}
                  onBlur={(e) => handleEdgeField(selectedEdgeIndex, 'when', e.target.value.trim())}
                />
              </Field>
              <Button appearance="secondary" icon={<DeleteRegular />} onClick={handleDeleteSelectedEdge}>
                Delete edge
              </Button>
            </>
          )}

          {rightMode === 'inspector' && !selectedNode && !selectedEdge && (
            <EmptyState
              title="Select a node or edge"
              description="Edit its properties, drag from a node handle to connect, or use Add node."
            />
          )}
        </div>
      </div>

      <div className={styles.footer}>
        {isDirty && (
          <span className={styles.statusWarning}>
            <WarningRegular fontSize={14} aria-hidden="true" />
            <Text size={200}>Unsaved changes</Text>
          </span>
        )}
        <div className={styles.footerActions}>
          <Button
            appearance="primary"
            disabled={saving}
            onClick={() => { void handleSave(); }}
          >
            {saving ? 'Saving...' : 'Save'}
          </Button>
        </div>
      </div>
      <ScheduleTriggerDialog
        open={scheduleOpen}
        trigger={scheduleTrigger}
        onDismiss={() => setScheduleOpen(false)}
        onSave={handleScheduleSave}
        onRemove={handleScheduleRemove}
      />
    </div>
  );
}
