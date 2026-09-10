import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ChevronDownRegular,
  ChevronRightRegular,
  ErrorCircleFilled,
  InfoRegular,
  WarningFilled,
} from '@fluentui/react-icons';
import '@xyflow/react/dist/style.css';
import {
  Handle,
  MarkerType,
  Position,
  ReactFlow,
} from '@xyflow/react';
import { useCallback, useMemo, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import type { Edge, Node, NodeProps } from '@xyflow/react';
import type {
  AgentPodInfoDto,
  ClusterDiagnosticsDto,
  SandboxClaimObjectDto,
  WarmPoolInstanceDto,
  WarmPoolStatusDto,
} from '../api/types';

export const TOPOLOGY_NODE_WIDTH = 280;
export const TOPOLOGY_NODE_HEIGHT = 96;
export const TOPOLOGY_EXPANDED_HEIGHT = 276;
export const TOPOLOGY_COLUMN_GAP = 100;
export const TOPOLOGY_ROW_GAP = 28;

const useStyles = makeStyles({
  container: {
    display: 'grid',
    gap: tokens.spacingVerticalM,
  },
  graphViewport: {
    height: '440px',
    minWidth: 0,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  graphCanvas: {
    height: '100%',
  },
  instancePanel: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  instanceGroup: {
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  instanceGroupTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  instanceList: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  instanceListItem: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  instanceListMeta: {
    minWidth: 0,
  },
  node: {
    width: `clamp(232px, 72vw, ${TOPOLOGY_NODE_WIDTH}px)`,
    height: '100%',
    boxSizing: 'border-box',
    border: `2px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    boxShadow: tokens.shadow2,
    transitionProperty: 'height, box-shadow',
    transitionDuration: tokens.durationNormal,
    transitionTimingFunction: tokens.curveEasyEase,
  },
  healthy: {
    borderTopColor: tokens.colorPaletteGreenBorder2,
    borderRightColor: tokens.colorPaletteGreenBorder2,
    borderBottomColor: tokens.colorPaletteGreenBorder2,
    borderLeftColor: tokens.colorPaletteGreenBorder2,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  warning: {
    borderTopColor: tokens.colorPaletteMarigoldBorderActive,
    borderRightColor: tokens.colorPaletteMarigoldBorderActive,
    borderBottomColor: tokens.colorPaletteMarigoldBorderActive,
    borderLeftColor: tokens.colorPaletteMarigoldBorderActive,
    backgroundColor: tokens.colorPaletteMarigoldBackground1,
  },
  critical: {
    borderTopColor: tokens.colorPaletteRedBorder2,
    borderRightColor: tokens.colorPaletteRedBorder2,
    borderBottomColor: tokens.colorPaletteRedBorder2,
    borderLeftColor: tokens.colorPaletteRedBorder2,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  unknown: {},
  toggle: {
    appearance: 'none',
    width: '100%',
    minHeight: `${TOPOLOGY_NODE_HEIGHT - 4}px`,
    boxSizing: 'border-box',
    padding: tokens.spacingHorizontalM,
    border: 0,
    backgroundColor: 'transparent',
    color: 'inherit',
    display: 'grid',
    gridTemplateColumns: '20px minmax(0, 1fr) 20px',
    gridTemplateRows: 'auto auto',
    columnGap: tokens.spacingHorizontalS,
    rowGap: tokens.spacingVerticalXS,
    alignContent: 'center',
    textAlign: 'left',
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '-3px',
    },
  },
  statusIcon: {
    gridColumn: '1',
    gridRow: '1 / span 2',
    alignSelf: 'center',
    fontSize: '18px',
  },
  statusHealthy: { color: tokens.colorPaletteGreenForeground1 },
  statusWarning: { color: tokens.colorPaletteMarigoldForeground2 },
  statusCritical: { color: tokens.colorPaletteRedForeground1 },
  statusUnknown: { color: tokens.colorNeutralForeground3 },
  title: {
    gridColumn: '2',
    gridRow: '1',
    minWidth: 0,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
    wordBreak: 'break-word',
  },
  detail: {
    gridColumn: '2',
    gridRow: '2',
    minWidth: 0,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  chevron: {
    gridColumn: '3',
    gridRow: '1 / span 2',
    alignSelf: 'center',
    color: tokens.colorNeutralForeground3,
  },
  disclosure: {
    minHeight: 0,
    display: 'flex',
    flex: 1,
    flexDirection: 'column',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    animationName: {
      from: { opacity: 0 },
      to: { opacity: 1 },
    },
    animationDuration: tokens.durationNormal,
    animationTimingFunction: tokens.curveEasyEase,
  },
  rows: {
    margin: 0,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    display: 'grid',
    gridTemplateColumns: 'minmax(68px, auto) minmax(0, 1fr)',
    columnGap: tokens.spacingHorizontalS,
    rowGap: tokens.spacingVerticalXS,
  },
  rowLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  rowValue: {
    minWidth: 0,
    margin: 0,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  monospace: {
    fontFamily: tokens.fontFamilyMonospace,
  },
  actions: {
    marginTop: 'auto',
    minHeight: '32px',
    padding: `0 ${tokens.spacingHorizontalM} ${tokens.spacingVerticalS}`,
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  link: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'none',
    ':hover': {
      textDecorationLine: 'underline',
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '2px',
    },
  },
  instanceName: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  instanceDetail: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  reducedMotion: {
    '@media (prefers-reduced-motion: reduce)': {
      transitionDuration: '0.01ms',
      animationDuration: '0.01ms',
    },
  },
});

export type NodeStatus = 'healthy' | 'warning' | 'critical' | 'unknown';

export interface TopologyDetailRow {
  label: string;
  value: string;
  monospace?: boolean;
}

export interface TopologyAction {
  label: string;
  to: string;
}

export interface ClusterNodeData extends Record<string, unknown> {
  title: string;
  detail: string;
  status: NodeStatus;
  expanded: boolean;
  rows: TopologyDetailRow[];
  actions?: TopologyAction[];
  onToggle: (id: string) => void;
}

function StatusIcon({ status, className }: { status: NodeStatus; className: string }) {
  if (status === 'healthy') return <CheckmarkCircleFilled className={className} aria-hidden="true" />;
  if (status === 'warning') return <WarningFilled className={className} aria-hidden="true" />;
  if (status === 'critical') return <ErrorCircleFilled className={className} aria-hidden="true" />;
  return <InfoRegular className={className} aria-hidden="true" />;
}

function ClusterNode({ id, data }: NodeProps) {
  const styles = useStyles();
  const node = data as ClusterNodeData;
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };
  const label = `${node.title}: ${node.detail}`;
  const statusClass = node.status === 'healthy'
    ? styles.statusHealthy
    : node.status === 'warning'
      ? styles.statusWarning
      : node.status === 'critical'
        ? styles.statusCritical
        : styles.statusUnknown;

  return (
    <article
      className={mergeClasses(styles.node, styles[node.status], styles.reducedMotion)}
      aria-label={label}
      data-testid={`cluster-topology-node-${id}`}
    >
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <button
        type="button"
        className={styles.toggle}
        aria-expanded={node.expanded}
        aria-label={`${node.expanded ? 'Collapse' : 'Expand'} ${label}`}
        onClick={() => node.onToggle(id)}
        data-testid={`cluster-topology-toggle-${id}`}
      >
        <StatusIcon
          status={node.status}
          className={mergeClasses(styles.statusIcon, statusClass)}
        />
        <span className={styles.title} title={node.title}>{node.title}</span>
        <span className={styles.detail} title={node.detail}>{node.detail}</span>
        {node.expanded
          ? <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
          : <ChevronRightRegular className={styles.chevron} aria-hidden="true" />}
      </button>
      {node.expanded ? (
        <div className={styles.disclosure} data-testid={`cluster-topology-details-${id}`}>
          <dl className={styles.rows}>
            {node.rows.map((row) => (
              <div key={`${row.label}-${row.value}`} style={{ display: 'contents' }}>
                <dt className={styles.rowLabel}>{row.label}</dt>
                <dd className={mergeClasses(styles.rowValue, row.monospace && styles.monospace)} title={row.value}>
                  {row.value}
                </dd>
              </div>
            ))}
          </dl>
          {(node.actions?.length ?? 0) > 0 ? (
            <nav className={styles.actions} aria-label={`${node.title} actions`}>
              {node.actions!.map((action) => (
                <RouterLink key={action.to} to={action.to} className={styles.link}>
                  {action.label}
                </RouterLink>
              ))}
            </nav>
          ) : null}
        </div>
      ) : null}
      <Handle type="source" position={Position.Right} style={handleStyle} />
    </article>
  );
}

const nodeTypes = { cluster: ClusterNode };

// eslint-disable-next-line react-refresh/only-export-components -- pure formatting helper is unit-tested with the graph model.
export function formatTopologyAge(ageSeconds: number | null | undefined): string {
  if (ageSeconds == null) return 'Not reported';
  if (ageSeconds < 60) return `${Math.floor(ageSeconds)}s`;
  if (ageSeconds < 3600) return `${Math.floor(ageSeconds / 60)}m`;
  if (ageSeconds < 86_400) return `${Math.floor(ageSeconds / 3600)}h`;
  return `${Math.floor(ageSeconds / 86_400)}d`;
}

function clusterStatus(data: ClusterDiagnosticsDto): NodeStatus {
  if (data.checks.some((check) => check.status === 'critical' || check.status === 'degraded')) return 'critical';
  if (data.checks.some((check) => check.status === 'warning')) return 'warning';
  return data.checks.length > 0 && data.checks.every((check) => check.status === 'healthy')
    ? 'healthy'
    : 'unknown';
}

function poolStatus(status: string): NodeStatus {
  if (status === 'healthy') return 'healthy';
  if (status === 'warning') return 'warning';
  if (status === 'critical' || status === 'failed' || status === 'unavailable') return 'critical';
  return 'unknown';
}

function claimStatus(phase: string, ready: boolean): NodeStatus {
  if (phase === 'bound' && ready) return 'healthy';
  if (phase === 'pending') return 'warning';
  if (phase === 'failed' || phase === 'lost' || phase === 'unavailable') return 'critical';
  return 'unknown';
}

function podStatus(status: string): NodeStatus {
  if (status === 'ready') return 'healthy';
  if (status === 'pending' || status === 'warming') return 'warning';
  if (status === 'failed' || status === 'lost' || status === 'unavailable') return 'critical';
  return 'unknown';
}

function instanceStatus(status: string): NodeStatus {
  if (status === 'available') return 'healthy';
  if (status === 'claimed' || status === 'warming') return 'warning';
  if (status === 'failed' || status === 'lost' || status === 'unavailable') return 'critical';
  return 'unknown';
}

function topologyEdge(source: string, target: string): Edge {
  const stroke = 'var(--colorNeutralStroke2)';
  return {
    id: `${source}->${target}`,
    source,
    target,
    style: { stroke, strokeWidth: 1.5 },
    markerEnd: { type: MarkerType.ArrowClosed, color: stroke, width: 12, height: 12 },
  };
}

function stableId(prefix: string, value: string): string {
  return `${prefix}-${encodeURIComponent(value).replaceAll('%', '_')}`;
}

function runActions(projectId?: string | null, runId?: string | null): TopologyAction[] {
  if (!projectId) return [];
  const actions: TopologyAction[] = [{ label: 'View project', to: `/projects/${projectId}` }];
  if (runId) {
    actions.unshift({
      label: 'View run',
      to: `/projects/${projectId}/orchestrations/${runId}`,
    });
  }
  return actions;
}

function runProjectLookup(data: ClusterDiagnosticsDto): Map<string, string> {
  const result = new Map<string, string>();
  for (const pool of data.warm_pools ?? []) {
    for (const instance of pool.instances ?? []) {
      if (instance.run_id && instance.project_id) result.set(instance.run_id, instance.project_id);
    }
  }
  return result;
}

// eslint-disable-next-line react-refresh/only-export-components -- pure initial-state policy is unit-tested independently.
export function initiallyExpandedTopologyIds(data: ClusterDiagnosticsDto): Set<string> {
  const expanded = new Set<string>();
  if (clusterStatus(data) !== 'healthy') expanded.add('cluster');
  for (const pool of data.warm_pools ?? []) {
    if (
      poolStatus(pool.status) !== 'healthy'
      || pool.ready_replicas < pool.desired_replicas
      || (pool.desired_replicas > 0 && pool.available_replicas === 0)
    ) {
      expanded.add(stableId('pool', pool.name));
    }
    for (const instance of pool.instances ?? []) {
      if (instance.status !== 'available') expanded.add(stableId('instance', `${pool.name}/${instance.name}`));
    }
  }
  for (const claim of data.sandbox_claims ?? []) {
    if (claim.phase === 'bound' || claimStatus(claim.phase, claim.ready) !== 'healthy') {
      expanded.add(stableId('claim', claim.name));
    }
  }
  for (const pod of [...data.active_agent_pods, ...data.orphaned_agent_pods]) {
    if (podStatus(pod.status) !== 'healthy' || data.orphaned_agent_pods.includes(pod)) {
      expanded.add(stableId('pod', `${pod.claim_name}/${pod.pod_name ?? ''}`));
    }
  }
  return expanded;
}

interface BuildTopologyOptions {
  expandedIds: ReadonlySet<string>;
  onToggle: (id: string) => void;
}

export interface ClusterTopologyModel {
  nodes: Node<ClusterNodeData>[];
  edges: Edge[];
}

function instanceRows(instance: WarmPoolInstanceDto, pool: WarmPoolStatusDto): TopologyDetailRow[] {
  return [
    { label: 'State', value: instance.status },
    { label: 'Claim', value: instance.claim_name ?? 'Unclaimed', monospace: Boolean(instance.claim_name) },
    { label: 'Run', value: instance.run_id ?? 'None', monospace: Boolean(instance.run_id) },
    { label: 'Project', value: instance.project_id ?? 'Not resolved', monospace: Boolean(instance.project_id) },
    { label: 'Age', value: formatTopologyAge(instance.age_seconds) },
    { label: 'Pool', value: pool.name, monospace: true },
  ];
}

function claimRows(claim: SandboxClaimObjectDto): TopologyDetailRow[] {
  return [
    { label: 'Phase', value: claim.phase },
    { label: 'Ready', value: claim.ready ? 'Yes' : 'No' },
    { label: 'Sandbox', value: claim.bound_sandbox ?? 'Not bound', monospace: Boolean(claim.bound_sandbox) },
    { label: 'Warm pool', value: claim.warm_pool ?? 'Ad hoc or pending', monospace: Boolean(claim.warm_pool) },
    { label: 'Run', value: claim.run_id ?? 'Not reported', monospace: Boolean(claim.run_id) },
    { label: 'Age', value: formatTopologyAge(claim.age_seconds) },
  ];
}

function podRows(pod: AgentPodInfoDto, orphaned: boolean): TopologyDetailRow[] {
  return [
    { label: 'State', value: pod.status },
    { label: 'Relationship', value: orphaned ? 'Orphaned pod' : 'Active run pod' },
    { label: 'Claim', value: pod.claim_name, monospace: true },
    { label: 'Run', value: pod.run_id ?? 'Not reported', monospace: Boolean(pod.run_id) },
    { label: 'Age', value: formatTopologyAge(pod.age_seconds) },
  ];
}

// eslint-disable-next-line react-refresh/only-export-components -- pure graph transform is unit-tested independently.
export function buildClusterTopology(
  data: ClusterDiagnosticsDto,
  { expandedIds, onToggle }: BuildTopologyOptions,
): ClusterTopologyModel {
  const nodes: Node<ClusterNodeData>[] = [];
  const edges: Edge[] = [];
  const poolIds = new Map<string, string>();
  const instanceIdsByClaimName = new Map<string, string>();
  const claimIds = new Map<string, string>();
  const projectsByRun = runProjectLookup(data);
  const columns = [0, 1, 2, 3, 4].map((column) => column * (TOPOLOGY_NODE_WIDTH + TOPOLOGY_COLUMN_GAP));
  const y = [0, 0, 0, 0, 0];

  const addNode = (
    id: string,
    column: number,
    node: Pick<ClusterNodeData, 'title' | 'detail' | 'status' | 'rows' | 'actions'>,
  ) => {
    const expanded = expandedIds.has(id);
    const height = expanded ? TOPOLOGY_EXPANDED_HEIGHT : TOPOLOGY_NODE_HEIGHT;
    nodes.push({
      id,
      type: 'cluster',
      data: { ...node, expanded, onToggle },
      position: { x: columns[column], y: y[column] },
      style: { height },
    });
    y[column] += height + TOPOLOGY_ROW_GAP;
  };

  const unhealthyChecks = data.checks.filter((check) => check.status !== 'healthy');
  addNode('cluster', 0, {
    title: 'Cluster',
    detail: `${data.checks.filter((check) => check.status === 'healthy').length} / ${data.checks.length} checks healthy`,
    status: clusterStatus(data),
    rows: [
      { label: 'Snapshot', value: new Date(data.generated_utc).toLocaleString() },
      { label: 'Probe time', value: `${Math.round(data.total_duration_ms)}ms` },
      { label: 'Health', value: unhealthyChecks.length === 0 ? 'All checks healthy' : `${unhealthyChecks.length} need attention` },
      ...(unhealthyChecks.slice(0, 2).map((check) => ({
        label: check.name,
        value: check.message || check.status,
      }))),
    ],
  });

  for (const pool of data.warm_pools ?? []) {
    const id = stableId('pool', pool.name);
    poolIds.set(pool.name, id);
    const allocated = Math.max(0, pool.ready_replicas - pool.available_replicas);
    addNode(id, 1, {
      title: pool.name,
      detail: `Warm pool · ${pool.ready_replicas} / ${pool.desired_replicas} ready`,
      status: poolStatus(pool.status),
      rows: [
        { label: 'Health', value: pool.status },
        { label: 'Ready', value: `${pool.ready_replicas} / ${pool.desired_replicas}` },
        { label: 'Available', value: String(pool.available_replicas) },
        { label: 'Allocated', value: String(allocated) },
        { label: 'Instances', value: String(pool.instances?.length ?? 0) },
        { label: 'Age', value: formatTopologyAge(pool.age_seconds) },
      ],
    });
    edges.push(topologyEdge('cluster', id));
  }

  for (const pool of data.warm_pools ?? []) {
    for (const instance of pool.instances ?? []) {
      const id = stableId('instance', `${pool.name}/${instance.name}`);
      if (instance.claim_name) instanceIdsByClaimName.set(instance.claim_name, id);
      addNode(id, 2, {
        title: instance.name,
        detail: instance.status === 'claimed'
          ? 'Warm instance · claimed'
          : instance.status === 'available'
            ? 'Warm instance · available'
            : `Warm instance · ${instance.status}`,
        status: instanceStatus(instance.status),
        rows: instanceRows(instance, pool),
        actions: runActions(instance.project_id, instance.run_id),
      });
      edges.push(topologyEdge(poolIds.get(pool.name) ?? 'cluster', id));
    }
  }

  for (const claim of data.sandbox_claims ?? []) {
    const id = stableId('claim', claim.name);
    claimIds.set(claim.name, id);
    const projectId = claim.run_id ? projectsByRun.get(claim.run_id) : undefined;
    addNode(id, 3, {
      title: claim.name,
      detail: `Sandbox claim · ${claim.phase}`,
      status: claimStatus(claim.phase, claim.ready),
      rows: claimRows(claim),
      actions: runActions(projectId, claim.run_id),
    });
    edges.push(topologyEdge(instanceIdsByClaimName.get(claim.name) ?? poolIds.get(claim.warm_pool ?? '') ?? 'cluster', id));
  }

  const activePodSet = new Set(data.active_agent_pods);
  for (const pod of [...data.active_agent_pods, ...data.orphaned_agent_pods]) {
    const id = stableId('pod', `${pod.claim_name}/${pod.pod_name ?? ''}`);
    const projectId = pod.run_id ? projectsByRun.get(pod.run_id) : undefined;
    const orphaned = !activePodSet.has(pod);
    addNode(id, 4, {
      title: pod.pod_name ?? pod.claim_name,
      detail: `Agent pod · ${pod.status}${orphaned ? ' · orphaned' : ''}`,
      status: orphaned && podStatus(pod.status) === 'healthy' ? 'warning' : podStatus(pod.status),
      rows: podRows(pod, orphaned),
      actions: runActions(projectId, pod.run_id),
    });
    edges.push(topologyEdge(claimIds.get(pod.claim_name) ?? 'cluster', id));
  }

  return { nodes, edges };
}

export function ClusterTopologyGraph({ data }: { data: ClusterDiagnosticsDto }) {
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => initiallyExpandedTopologyIds(data));
  const onToggle = useCallback((id: string) => {
    setExpandedIds((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);
  const { nodes, edges } = useMemo(
    () => buildClusterTopology(data, { expandedIds, onToggle }),
    [data, expandedIds, onToggle],
  );
  const styles = useStyles();

  return (
    <div className={styles.container} data-testid="cluster-topology-graph">
      <div className={styles.graphViewport} data-testid="cluster-topology-viewport">
        <ReactFlow
          className={styles.graphCanvas}
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.15, maxZoom: 1 }}
          minZoom={0.2}
          maxZoom={2}
          nodesDraggable={false}
          nodesConnectable={false}
          nodesFocusable={false}
          edgesFocusable={false}
          panOnScroll
          zoomOnScroll
          zoomActivationKeyCode={['Meta', 'Control']}
          zoomOnDoubleClick={false}
          panOnDrag
          proOptions={{ hideAttribution: true }}
        />
      </div>
      <div className={styles.instancePanel}>
        {(data.warm_pools ?? []).map((pool) => (
          <section key={`${pool.name}-instances`} className={styles.instanceGroup}>
            <span className={styles.instanceGroupTitle}>{pool.name} instances</span>
            <ul className={styles.instanceList}>
              {(pool.instances ?? []).map((instance) => (
                <li
                  key={`${pool.name}-${instance.name}`}
                  className={styles.instanceListItem}
                  aria-label={`${instance.name}: Warm instance · ${instance.status}`}
                >
                  <div className={styles.instanceListMeta}>
                    <div className={styles.instanceName} title={instance.name}>{instance.name}</div>
                    <div className={styles.instanceDetail}>
                      {instance.status === 'claimed'
                        ? `Claimed${instance.claim_name ? ` by ${instance.claim_name}` : ''}`
                        : instance.status === 'available'
                          ? 'Unclaimed warm instance'
                          : 'Warming up'}
                    </div>
                  </div>
                  {instance.run_id && instance.project_id ? (
                    <RouterLink
                      to={`/projects/${instance.project_id}/orchestrations/${instance.run_id}`}
                      className={styles.link}
                    >
                      {instance.run_id}
                    </RouterLink>
                  ) : null}
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </div>
  );
}
