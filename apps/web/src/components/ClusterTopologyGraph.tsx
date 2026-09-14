import {
  Badge,
  Button,
  Card,
  CardHeader,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import '@xyflow/react/dist/style.css';
import {
  Handle,
  MarkerType,
  Position,
  ReactFlow,
} from '@xyflow/react';
import { useMemo, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import type { Edge, Node, NodeProps } from '@xyflow/react';
import type {
  KubernetesTopologyDto,
  KubernetesTopologyLayer,
  KubernetesTopologyNodeDto,
} from '../api/types';
import { routeGridEdges } from '../utils/dagLayout';
import { workflowEdgeTypes } from './WorkflowGraphPanel';
import agentIcon from '../copilot-fluent-system/assets/icons/azure/assets/ai-plus-machine-learning--foundry-agent-service--708ac6493c69.svg';
import controlPlaneIcon from '../copilot-fluent-system/assets/icons/azure/assets/ai-plus-machine-learning--foundry-control-plane--6608ba6d5016.svg';
import kubernetesIcon from '../copilot-fluent-system/assets/icons/azure/assets/compute--kubernetes-services--e49c42fb6915.svg';
import sandboxIcon from '../copilot-fluent-system/assets/icons/azure/assets/compute--virtual-machine--7a04018565e2.svg';
import claimIcon from '../copilot-fluent-system/assets/icons/azure/assets/containers--container-instances--6758152c2043.svg';
import networkPolicyIcon from '../copilot-fluent-system/assets/icons/azure/assets/containers--aks-network-policy--40e4edaa0b02.svg';
import storageIcon from '../copilot-fluent-system/assets/icons/azure/assets/databases--azure-database-postgresql-server--b9a7d1fe6c8b.svg';
import artifactStorageIcon from '../copilot-fluent-system/assets/icons/azure/assets/general--storage-azure-files--afbe608e7cde.svg';
import templateIcon from '../copilot-fluent-system/assets/icons/azure/assets/general--templates--d92cf75dcd7e.svg';
import scalingIcon from '../copilot-fluent-system/assets/icons/azure/assets/menu--container-scale--af9400751605.svg';
import gatewayIcon from '../copilot-fluent-system/assets/icons/azure/assets/new-icons--ai-gateway--4d556fee8bc7.svg';
import identityIcon from '../copilot-fluent-system/assets/icons/azure/assets/identity--entra-identity--b9cba4f973b6.svg';

const NODE_WIDTH = 240;
const NODE_HEIGHT = 104;
const COLUMN_GAP = 76;

type NodeHealth = KubernetesTopologyNodeDto['health'];
type EdgeTone = 'default' | 'allowed';

interface TopologyFact {
  label: string;
  value: string;
}

interface PolicyDetail {
  name: string;
  selector: string;
  direction: string;
  effect: string;
  impact: string;
}

interface FunctionNode {
  id: string;
  layer: KubernetesTopologyLayer;
  title: string;
  kind: string;
  detail: string;
  health: NodeHealth;
  iconSrc: string;
  facts: TopologyFact[];
  resources: KubernetesTopologyNodeDto[];
  detailLayers: KubernetesTopologyLayer[];
  detailWarnings?: string[];
  policyBadges?: Array<{ label: string; tone: 'allowed' | 'blocked' }>;
  policies?: PolicyDetail[];
}

interface FunctionNodeData extends Record<string, unknown> {
  component: FunctionNode;
  onSelect: (id: string) => void;
}

const topologyIcons = {
  'public-entry': gatewayIcon,
  'service-targets': kubernetesIcon,
  'network-policies': networkPolicyIcon,
  'control-plane': controlPlaneIcon,
  'workload-pods': agentIcon,
  'agent-execution': agentIcon,
  'sandbox-template': templateIcon,
  'sandbox-pool': scalingIcon,
  'capacity-scaling': scalingIcon,
  'sandbox-claim': claimIcon,
  sandbox: sandboxIcon,
  'application-state': storageIcon,
  'session-artifacts': artifactStorageIcon,
  'secrets-identity': identityIcon,
} as const;

const useStyles = makeStyles({
  container: {
    display: 'grid',
    gap: tokens.spacingVerticalM,
  },
  layerStatus: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },
  graphViewport: {
    height: '520px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  graphCanvas: {
    height: '100%',
  },
  node: {
    width: `${NODE_WIDTH}px`,
    minHeight: `${NODE_HEIGHT}px`,
    boxSizing: 'border-box',
    padding: tokens.spacingHorizontalM,
    border: `2px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    cursor: 'pointer',
    ':focus-visible': {
      outline: `2px solid ${tokens.colorBrandStroke1}`,
      outlineOffset: '2px',
    },
  },
  nodeHeading: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalS,
  },
  nodeIcon: {
    width: '24px',
    height: '24px',
    flexShrink: 0,
  },
  nodeTitleGroup: {
    minWidth: 0,
    display: 'grid',
    gap: tokens.spacingVerticalXXS,
  },
  healthy: {
    borderTopColor: tokens.colorPaletteGreenBorder2,
    borderRightColor: tokens.colorPaletteGreenBorder2,
    borderBottomColor: tokens.colorPaletteGreenBorder2,
    borderLeftColor: tokens.colorPaletteGreenBorder2,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  attention: {
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
  kind: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.03em',
    textTransform: 'uppercase',
  },
  title: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
    wordBreak: 'break-word',
  },
  detail: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    overflowWrap: 'anywhere',
  },
  policyBadges: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXXS,
  },
  policyBadge: {
    width: 'fit-content',
    padding: `1px ${tokens.spacingHorizontalXS}`,
    borderRadius: tokens.borderRadiusSmall,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase100,
  },
  allowed: {
    color: tokens.colorPaletteGreenForeground1,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  blocked: {
    color: tokens.colorPaletteRedForeground1,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  inspector: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
    maxWidth: '720px',
    padding: tokens.spacingHorizontalM,
  },
  inspectorHeading: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalS,
  },
  inspectorTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
  },
  inspectorKind: {
    margin: 0,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  facts: {
    display: 'grid',
    gridTemplateColumns: 'minmax(130px, 180px) minmax(0, 1fr)',
    gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    margin: 0,
    fontSize: tokens.fontSizeBase200,
  },
  factLabel: {
    color: tokens.colorNeutralForeground3,
  },
  factValue: {
    margin: 0,
    overflowWrap: 'anywhere',
  },
  detailWarning: {
    padding: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorPaletteMarigoldBorderActive}`,
    borderRadius: tokens.borderRadiusSmall,
    color: tokens.colorPaletteMarigoldForeground2,
    backgroundColor: tokens.colorPaletteMarigoldBackground1,
    fontSize: tokens.fontSizeBase200,
  },
  attentionCallout: {
    padding: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorPaletteRedBorder2}`,
    borderRadius: tokens.borderRadiusSmall,
    color: tokens.colorPaletteRedForeground1,
    backgroundColor: tokens.colorPaletteRedBackground1,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
  },
  detailSection: {
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  detailSectionTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase300,
  },
  detailTable: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: tokens.fontSizeBase200,
  },
  detailCell: {
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalXS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    textAlign: 'left',
    verticalAlign: 'top',
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    overflowWrap: 'anywhere',
  },
  identifier: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    flexWrap: 'wrap',
  },
  emptyDetail: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  policyList: {
    listStyleType: 'none',
    display: 'grid',
    gap: tokens.spacingVerticalXS,
    margin: 0,
    padding: 0,
  },
  policyListItem: {
    display: 'grid',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    fontSize: tokens.fontSizeBase200,
  },
  policyMeta: {
    color: tokens.colorNeutralForeground3,
  },
});

function plural(count: number, singular: string): string {
  return `${count} ${singular}${count === 1 ? '' : 's'}`;
}

function aggregateHealth(resources: KubernetesTopologyNodeDto[]): NodeHealth {
  if (resources.some((resource) => resource.health === 'critical')) return 'critical';
  if (resources.some((resource) => resource.health === 'attention')) return 'attention';
  if (resources.length > 0 && resources.every((resource) => resource.health === 'healthy')) return 'healthy';
  return 'unknown';
}

function byType(
  resources: KubernetesTopologyNodeDto[],
  type: string,
): KubernetesTopologyNodeDto[] {
  return resources.filter((resource) => resource.type === type);
}

function component(
  id: keyof typeof topologyIcons,
  layer: KubernetesTopologyLayer,
  title: string,
  kind: string,
  resources: KubernetesTopologyNodeDto[],
  detail: string,
  facts: TopologyFact[],
): FunctionNode {
  return {
    id,
    layer,
    title,
    kind,
    detail,
    health: aggregateHealth(resources),
    iconSrc: topologyIcons[id],
    facts,
    resources,
    detailLayers: Array.from(new Set([layer, ...resources.map((resource) => resource.layer)])),
  };
}

function policyDetails(resources: KubernetesTopologyNodeDto[]): PolicyDetail[] {
  return resources.map((resource) => ({
    name: resource.name,
    selector: resource.details.selector ?? 'All pods in scope',
    direction: resource.details.direction ?? 'ingress',
    effect: resource.details.effect ?? 'allow',
    impact: resource.details.trafficImpact ?? 'gateway ingress',
  }));
}

function buildComponents(topology: KubernetesTopologyDto): FunctionNode[] {
  const resources = topology.nodes;
  const components: FunctionNode[] = [];
  const runtime = resources.filter((resource) => resource.layer === 'runtime');
  const pods = byType(runtime, 'Pod');
  const templates = byType(runtime, 'SandboxTemplate');
  const pools = byType(runtime, 'SandboxWarmPool');
  const claims = byType(runtime, 'SandboxClaim');
  const sandboxes = byType(runtime, 'Sandbox');

  if (runtime.length > 0) {
    components.push(component(
      'agent-execution',
      'runtime',
      'Sessions & agent execution',
      'Pod workload',
      claims.length > 0 ? claims : pods,
      `${plural(claims.length, 'active sandbox claim')} · ${plural(pods.length, 'runtime pod')}`,
      [
        { label: 'Active executions', value: String(claims.length) },
        { label: 'Runtime pods', value: String(pods.length) },
      ],
    ));
    components.push(component(
      'sandbox-template',
      'runtime',
      'Sandbox template',
      'SandboxTemplate',
      templates,
      plural(templates.length, 'template'),
      [],
    ));
    components.push(component(
      'sandbox-pool',
      'runtime',
      'Sandbox pool',
      'SandboxWarmPool · scaling',
      pools,
      plural(pools.length, 'warm pool'),
      [],
    ));
    components.push(component(
      'sandbox-claim',
      'runtime',
      'Sandbox claims',
      'SandboxClaim',
      claims,
      plural(claims.length, 'claim'),
      [],
    ));
    components.push(component(
      'sandbox',
      'runtime',
      'Sandboxes',
      'Sandbox pod workload',
      sandboxes.length > 0 ? sandboxes : claims,
      `${claims.filter((claim) => claim.details.boundSandbox).length} bound · ${claims.filter((claim) => !claim.details.boundSandbox).length} pending · ${sandboxes.length} sandboxes`,
      [{ label: 'Isolation', value: sandboxes.some((sandbox) => sandbox.details.runtimeClassName === 'kata-vm-isolation') ? 'Kata VM isolation' : 'Runtime class reported per sandbox' }],
    ));
  }

  const networking = resources.filter((resource) => resource.layer === 'networking');
  const gateways = byType(networking, 'Gateway');
  const services = byType(networking, 'Service');
  const policies = byType(networking, 'NetworkPolicy')
    .filter((policy) => policy.details.trafficImpact === 'gateway_ingress' ||
      policy.details.trafficImpact === 'default_deny_ingress');
  const gatewayPolicies = policies.filter((policy) => policy.details.trafficImpact === 'gateway_ingress');
  const defaultDenyPolicies = policies.filter((policy) => policy.details.trafficImpact === 'default_deny_ingress');
  const policyBadges = [
    ...(gatewayPolicies.length > 0
      ? [{ label: `${plural(gatewayPolicies.length, 'gateway allow policy')}`, tone: 'allowed' as const }]
      : []),
    ...(defaultDenyPolicies.length > 0
      ? [{ label: 'Other inbound traffic blocked', tone: 'blocked' as const }]
      : []),
  ];

  if (gateways.length > 0) {
    components.push({
      ...component(
        'public-entry',
        'networking',
        'Public entry & gateway',
        'Traffic flow',
        gateways,
        plural(gateways.length, 'public gateway'),
        [{ label: 'Traffic source', value: 'Browser and MCP clients' }],
      ),
      policyBadges,
    });
  }
  if (services.length > 0) {
    components.push(component(
      'service-targets',
      'networking',
      'Agentweaver service targets',
      'Service endpoints',
      services,
      plural(services.length, 'service target'),
      [{ label: 'Target function', value: 'Browser UI, orchestration API, and MCP' }],
    ));
  }
  if (policies.length > 0) {
    components.push({
      ...component(
        'network-policies',
        'networking',
        'Network policy guardrails',
        'NetworkPolicy',
        policies,
        `${plural(gatewayPolicies.length, 'gateway allow policy')} · ${defaultDenyPolicies.length} default ingress ${defaultDenyPolicies.length === 1 ? 'deny' : 'denies'}`,
        [
          { label: 'Gateway ingress allows', value: String(gatewayPolicies.length) },
          { label: 'Default ingress denies', value: String(defaultDenyPolicies.length) },
          { label: 'Scope', value: 'Public traffic to Agentweaver services' },
        ],
      ),
      policies: policyDetails(policies),
    });
  }

  const workloads = resources.filter((resource) => resource.layer === 'workloads');
  const deployments = byType(workloads, 'Deployment');
  if (deployments.length > 0) {
    components.push(component(
      'control-plane',
      'workloads',
      'Control plane & workers',
      'Deployment workload',
      deployments,
      plural(deployments.length, 'deployment workload'),
      [{ label: 'Rollouts', value: 'Replica and image detail available in the inspector' }],
    ));
  }
  if (deployments.length > 0 && pods.length > 0) {
    components.push(component(
      'workload-pods',
      'workloads',
      'Agentweaver workload pods',
      'Pod workload',
      pods,
      plural(pods.length, 'running pod'),
      [{ label: 'Restart signal', value: `${pods.reduce((sum, pod) => sum + numericDetail(pod, 'restartCount'), 0)} container restarts` }],
    ));
  }

  const storage = resources.filter((resource) => resource.layer === 'storage');
  const workspaceClaims = byType(storage, 'PersistentVolumeClaim')
    .filter((resource) => resource.name.includes('workspace'));
  const applicationClaims = byType(storage, 'PersistentVolumeClaim')
    .filter((resource) => !resource.name.includes('workspace'));
  if (applicationClaims.length > 0) {
    components.push(component(
      'application-state',
      'storage',
      'Application persistence',
      'State storage',
      applicationClaims,
      plural(applicationClaims.length, 'application storage function'),
      [{ label: 'Storage purpose', value: 'Application state where provisioned' }],
    ));
  }
  if (workspaceClaims.length > 0) {
    components.push(component(
      'session-artifacts',
      'storage',
      'Sandbox & session artifacts',
      'Shared workspace',
      workspaceClaims,
      plural(workspaceClaims.length, 'artifact workspace'),
      [{ label: 'Storage purpose', value: 'Run worktrees, sandbox outputs, and session artifacts' }],
    ));
  }

  const autoscaling = resources.filter((resource) => resource.layer === 'autoscaling');
  if (autoscaling.length > 0) {
    components.push(component(
      'capacity-scaling',
      'autoscaling',
      'Capacity scaling',
      'Scaling policy',
      autoscaling,
      plural(autoscaling.length, 'scaling policy'),
      [{ label: 'Scaling scope', value: 'Agentweaver workloads' }],
    ));
  }

  const layerByName = new Map(topology.layers.map((layer) => [layer.name, layer]));
  return components.map((item) => ({
    ...item,
    detailWarnings: item.detailLayers
      .map((layerName) => layerByName.get(layerName))
      .filter((layer) => layer && layer.status !== 'available' && layer.status !== 'not_requested')
      .map((layer) => `${capitalize(layer!.name)} detail fetch ${layer!.status === 'partial' ? 'partially failed' : 'failed'}: ${layer!.message}`),
  }));
}

function capitalize(value: string): string {
  return value.length === 0 ? value : value[0].toUpperCase() + value.slice(1);
}

function numericDetail(resource: KubernetesTopologyNodeDto, key: string): number {
  const parsed = Number.parseInt(resource.details[key] ?? '0', 10);
  return Number.isFinite(parsed) ? parsed : 0;
}

function isReadyShort(resource: KubernetesTopologyNodeDto): boolean {
  const ready = resource.details.ready;
  if (!ready) return false;
  const [current, desired] = ready.split('/').map((value) => Number.parseInt(value, 10));
  return Number.isFinite(current) && Number.isFinite(desired) && current < desired;
}

function attentionScore(resource: KubernetesTopologyNodeDto): number {
  return (resource.health === 'critical' ? 100 : resource.health === 'attention' ? 50 : 0)
    + (isReadyShort(resource) ? 25 : 0)
    + Math.min(numericDetail(resource, 'restartCount'), 20);
}

function sortAttentionFirst(resources: KubernetesTopologyNodeDto[]): KubernetesTopologyNodeDto[] {
  return [...resources].sort((left, right) =>
    attentionScore(right) - attentionScore(left) ||
    left.name.localeCompare(right.name));
}

function formatAgeFromDetails(resource: KubernetesTopologyNodeDto): string {
  const parsed = Number.parseInt(resource.details.ageSeconds ?? '', 10);
  if (!Number.isFinite(parsed)) return '—';
  if (parsed < 60) return `${parsed}s`;
  if (parsed < 3600) return `${Math.floor(parsed / 60)}m`;
  if (parsed < 86400) return `${Math.floor(parsed / 3600)}h`;
  return `${Math.floor(parsed / 86400)}d`;
}

function field(resource: KubernetesTopologyNodeDto, key: string, fallback = '—'): string {
  return resource.details[key] || fallback;
}

function formatTimestamp(value: string): string {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? new Date(parsed).toLocaleString() : value;
}

function topologyEdge(source: string, target: string, tone: EdgeTone = 'default'): Edge {
  const stroke = tone === 'allowed'
    ? 'var(--colorPaletteGreenBorder2)'
    : 'var(--colorNeutralStroke2)';
  return {
    id: `${source}->${target}`,
    source,
    target,
    type: 'spine',
    style: { stroke, strokeWidth: tone === 'allowed' ? 2 : 1.5 },
    markerEnd: { type: MarkerType.ArrowClosed, color: stroke, width: 12, height: 12 },
  };
}

function ResourceNode({ data }: NodeProps) {
  const styles = useStyles();
  const { component: resource } = data as FunctionNodeData;
  const handleStyle: CSSProperties = { opacity: 0, pointerEvents: 'none' };

  return (
    <article
      className={mergeClasses(styles.node, styles[resource.health])}
      role="button"
      tabIndex={0}
      aria-label={`Inspect ${resource.title}: ${resource.detail}`}
      data-testid={`cluster-topology-node-${resource.id}`}
      onClick={() => (data as FunctionNodeData).onSelect(resource.id)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          (data as FunctionNodeData).onSelect(resource.id);
        }
      }}
    >
      <Handle id="target-left" type="target" position={Position.Left} style={handleStyle} />
      <Handle id="target-top" type="target" position={Position.Top} style={handleStyle} />
      <Handle id="target-right" type="target" position={Position.Right} style={handleStyle} />
      <Handle id="target-bottom" type="target" position={Position.Bottom} style={handleStyle} />
      <div className={styles.nodeHeading}>
        <img
          className={styles.nodeIcon}
          src={resource.iconSrc}
          alt=""
          aria-hidden="true"
          data-icon-source="iconcloud"
          data-testid={`cluster-topology-icon-${resource.id}`}
        />
        <div className={styles.nodeTitleGroup}>
          <span className={styles.kind}>{resource.kind}</span>
          <span className={styles.title}>{resource.title}</span>
        </div>
      </div>
      <span className={styles.detail}>{resource.detail}</span>
      {resource.policyBadges ? (
        <div className={styles.policyBadges}>
          {resource.policyBadges.map((badge) => (
            <span key={badge.label} className={`${styles.policyBadge} ${styles[badge.tone]}`}>
              {badge.label}
            </span>
          ))}
        </div>
      ) : null}
      <Handle id="source-left" type="source" position={Position.Left} style={handleStyle} />
      <Handle id="source-top" type="source" position={Position.Top} style={handleStyle} />
      <Handle id="source-right" type="source" position={Position.Right} style={handleStyle} />
      <Handle id="source-bottom" type="source" position={Position.Bottom} style={handleStyle} />
    </article>
  );
}

const nodeTypes = { resource: ResourceNode };

function copyText(value: string): void {
  void globalThis.navigator?.clipboard?.writeText(value).catch(() => undefined);
}

function Identifier({
  value,
  label,
  href,
}: {
  value?: string | null;
  label: string;
  href?: string;
}) {
  const styles = useStyles();
  if (!value || value === '—') return <>—</>;
  const id = <code className={styles.mono}>{value}</code>;
  return (
    <span className={styles.identifier}>
      {href ? <a href={href}>{id}</a> : id}
      <Button
        appearance="subtle"
        size="small"
        onClick={() => copyText(value)}
        aria-label={`Copy ${label} ${value}`}
      >
        Copy
      </Button>
    </span>
  );
}

function DetailTable({
  label,
  columns,
  rows,
}: {
  label: string;
  columns: string[];
  rows: ReactNode[][];
}) {
  const styles = useStyles();
  return (
    <table className={styles.detailTable} aria-label={label}>
      <thead>
        <tr>
          {columns.map((column) => (
            <th key={column} className={styles.detailCell}>{column}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, index) => (
          <tr key={index}>
            {row.map((cell, cellIndex) => (
              <td key={cellIndex} className={styles.detailCell}>{cell}</td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function EmptyDetail({ title, children }: { title: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.emptyDetail}>
      <strong>{title}</strong>
      <div>{children}</div>
    </div>
  );
}

function restartCell(resource: KubernetesTopologyNodeDto): ReactNode {
  const restarts = numericDetail(resource, 'restartCount');
  return restarts > 0
    ? <Badge appearance="tint" color="danger">{restarts} restarts</Badge>
    : String(restarts);
}

function runHref(projectId: string | undefined, runId: string | undefined): string | undefined {
  return projectId && runId ? `/projects/${encodeURIComponent(projectId)}/orchestrations/${encodeURIComponent(runId)}` : undefined;
}

function WorkloadPodDetails({ component }: { component: FunctionNode }) {
  const styles = useStyles();
  const pods = sortAttentionFirst(component.resources.filter((resource) => resource.type === 'Pod'));
  const unhealthy = pods.filter((pod) => attentionScore(pod) > 0);
  const groups = pods.reduce<Record<string, KubernetesTopologyNodeDto[]>>((acc, pod) => {
    const deployment = field(pod, 'deployment', 'Unowned pods');
    (acc[deployment] ??= []).push(pod);
    return acc;
  }, {});

  if (pods.length === 0) {
    return <EmptyDetail title="No workload pods">No workload pods were returned for this topology snapshot.</EmptyDetail>;
  }

  return (
    <div className={styles.detailSection}>
      {unhealthy.length > 0 ? (
        <div role="alert" className={styles.attentionCallout}>
          {unhealthy.map((pod) => pod.name).join(', ')} need attention. Check readiness and restart counts first.
        </div>
      ) : null}
      {Object.entries(groups).map(([deployment, deploymentPods]) => (
        <section key={deployment} className={styles.detailSection}>
          <h4 className={styles.detailSectionTitle}>{deployment}</h4>
          <DetailTable
            label={`${deployment} pods`}
            columns={['Pod', 'Ready', 'Restarts', 'Age', 'Node', 'Runtime', 'Image']}
            rows={deploymentPods.map((pod) => [
              <Identifier value={pod.name} label="pod" />,
              field(pod, 'ready'),
              restartCell(pod),
              formatAgeFromDetails(pod),
              <Identifier value={field(pod, 'nodeName')} label="node" />,
              field(pod, 'runtimeClassName', 'runc'),
              field(pod, 'imageTags'),
            ])}
          />
        </section>
      ))}
    </div>
  );
}

function DeploymentDetails({ component }: { component: FunctionNode }) {
  const deployments = sortAttentionFirst(component.resources.filter((resource) => resource.type === 'Deployment'));
  if (deployments.length === 0) return <EmptyDetail title="No deployments">No deployment workloads were returned for this topology snapshot.</EmptyDetail>;
  return (
    <DetailTable
      label="Deployment rollout details"
      columns={['Deployment', 'Desired', 'Ready', 'Available', 'Image', 'Last rollout']}
      rows={deployments.map((deployment) => [
        <Identifier value={deployment.name} label="deployment" />,
        field(deployment, 'replicas', '0'),
        field(deployment, 'readyReplicas', '0'),
        field(deployment, 'availableReplicas', '0'),
        field(deployment, 'imageTags'),
        deployment.details.lastRolloutUtc ? formatTimestamp(deployment.details.lastRolloutUtc) : 'Not reported',
      ])}
    />
  );
}

function ClaimDetails({
  component,
  projectId,
  emptyTitle = 'No sandbox claims are active',
  emptyBody = 'Zero claims is a legitimate idle state: no run is currently waiting for or bound to a sandbox.',
}: {
  component: FunctionNode;
  projectId?: string;
  emptyTitle?: string;
  emptyBody?: string;
}) {
  const claims = sortAttentionFirst(component.resources.filter((resource) => resource.type === 'SandboxClaim'));
  if (claims.length === 0) return <EmptyDetail title={emptyTitle}>{emptyBody}</EmptyDetail>;
  return (
    <DetailTable
      label="Sandbox claim details"
      columns={['Run', 'Subtask', 'Claim', 'Bound pod', 'Phase', 'Age']}
      rows={claims.map((claim) => {
        const runId = field(claim, 'runId', '');
        return [
          <Identifier value={runId || undefined} label="run" href={runHref(projectId, runId)} />,
          field(claim, 'subtask'),
          <Identifier value={claim.name} label="claim" />,
          <Identifier value={field(claim, 'boundSandbox', '') || undefined} label="pod" />,
          field(claim, 'phase', claim.health),
          formatAgeFromDetails(claim),
        ];
      })}
    />
  );
}

function PoolDetails({ component }: { component: FunctionNode }) {
  const pools = sortAttentionFirst(component.resources.filter((resource) => resource.type === 'SandboxWarmPool'));
  if (pools.length === 0) return <EmptyDetail title="No sandbox warm pools">No SandboxWarmPool objects were returned for this topology snapshot.</EmptyDetail>;
  return (
    <DetailTable
      label="Sandbox warm pool details"
      columns={['Pool', 'Warm size', 'Available', 'Claimed', 'Template', 'Min / max', 'Recent scale event']}
      rows={pools.map((pool) => {
        const desired = field(pool, 'replicas', '0');
        const ready = field(pool, 'readyReplicas', '0');
        const available = Number.parseInt(field(pool, 'availableReplicas', ready), 10);
        const readyCount = Number.parseInt(ready, 10);
        const claimed = Number.isFinite(available) && Number.isFinite(readyCount)
          ? Math.max(0, readyCount - available)
          : 0;
        return [
          <Identifier value={pool.name} label="warm pool" />,
          `${ready}/${desired}`,
          field(pool, 'availableReplicas', ready),
          String(claimed),
          field(pool, 'template'),
          pool.details.minReplicas || pool.details.maxReplicas
            ? `${pool.details.minReplicas ?? '—'} / ${pool.details.maxReplicas ?? '—'}`
            : 'Not reported by SandboxWarmPool',
          pool.details.lastScaleEvent ? formatTimestamp(pool.details.lastScaleEvent) : 'No scale event reported',
        ];
      })}
    />
  );
}

function SandboxDetails({
  component,
  claims,
}: {
  component: FunctionNode;
  claims: KubernetesTopologyNodeDto[];
}) {
  const sandboxes = sortAttentionFirst(component.resources.filter((resource) => resource.type === 'Sandbox'));
  const boundNames = new Set(claims.map((claim) => claim.details.boundSandbox).filter(Boolean));
  const bound = sandboxes.filter((sandbox) => boundNames.has(sandbox.name) || boundNames.has(sandbox.details.podName));
  const pending = sandboxes.filter((sandbox) => sandbox.health !== 'healthy');
  if (sandboxes.length === 0) {
    return <EmptyDetail title="No sandboxes">No Sandbox objects were returned. If claims exist, their bound pod names are still shown in Sandbox claims.</EmptyDetail>;
  }
  return (
    <div>
      <p>{bound.length} bound · {pending.length} pending · {Math.max(0, sandboxes.length - bound.length - pending.length)} available</p>
      <DetailTable
        label="Sandbox runtime details"
        columns={['Sandbox', 'Pod', 'Node', 'Runtime class', 'Isolation backend', 'Containers', 'Status']}
        rows={sandboxes.map((sandbox) => [
          <Identifier value={sandbox.name} label="sandbox" />,
          <Identifier value={field(sandbox, 'podName', sandbox.name)} label="pod" />,
          <Identifier value={field(sandbox, 'nodeName')} label="node" />,
          field(sandbox, 'runtimeClassName', 'runc'),
          field(sandbox, 'isolationBackend', 'runc'),
          field(sandbox, 'containers'),
          boundNames.has(sandbox.name) || boundNames.has(sandbox.details.podName) ? 'bound' : field(sandbox, 'status', sandbox.health),
        ])}
      />
    </div>
  );
}

function TemplateDetails({ component }: { component: FunctionNode }) {
  const templates = component.resources.filter((resource) => resource.type === 'SandboxTemplate');
  if (templates.length === 0) return <EmptyDetail title="No sandbox template">No SandboxTemplate object was returned for this topology snapshot.</EmptyDetail>;
  return (
    <DetailTable
      label="Sandbox template details"
      columns={['Template', 'Image', 'Runtime class', 'Requests', 'Limits', 'Mounts', 'Policy']}
      rows={templates.map((template) => [
        <Identifier value={template.name} label="template" />,
        field(template, 'imageTags'),
        field(template, 'runtimeClassName', 'runc'),
        field(template, 'resourceRequests'),
        field(template, 'resourceLimits'),
        field(template, 'mounts'),
        field(template, 'policy'),
      ])}
    />
  );
}

function DetailContent({
  component,
  projectId,
  topology,
}: {
  component: FunctionNode;
  projectId?: string;
  topology: KubernetesTopologyDto;
}) {
  const runtimeClaims = topology.nodes.filter((node) => node.type === 'SandboxClaim');
  switch (component.id) {
    case 'workload-pods':
      return <WorkloadPodDetails component={component} />;
    case 'control-plane':
      return <DeploymentDetails component={component} />;
    case 'agent-execution':
      return (
        <ClaimDetails
          component={component}
          projectId={projectId}
          emptyTitle="No sessions are bound to sandbox claims"
          emptyBody="No run is currently occupying an AgentHost sandbox in this snapshot."
        />
      );
    case 'sandbox-pool':
      return <PoolDetails component={component} />;
    case 'sandbox-claim':
      return <ClaimDetails component={component} projectId={projectId} />;
    case 'sandbox':
      return <SandboxDetails component={component} claims={runtimeClaims} />;
    case 'sandbox-template':
      return <TemplateDetails component={component} />;
    default:
      return null;
  }
}

function TopologyInspector({
  component,
  topology,
  projectId,
  onClose,
}: {
  component: FunctionNode;
  topology: KubernetesTopologyDto;
  projectId?: string;
  onClose: () => void;
}) {
  const styles = useStyles();

  return (
    <Card className={styles.inspector} aria-label={`${component.title} resource details`}>
      <CardHeader
        image={<img className={styles.nodeIcon} src={component.iconSrc} alt="" aria-hidden="true" />}
        header={<strong>{component.title}</strong>}
        description={`${component.kind} · ${component.health}`}
        action={<Button appearance="subtle" size="small" onClick={onClose}>Close</Button>}
      />
      <p className={styles.inspectorKind}>{component.detail}</p>
      {component.detailWarnings?.map((warning) => (
        <div key={warning} role="alert" className={styles.detailWarning}>{warning}</div>
      ))}
      <dl className={styles.facts}>
        <div style={{ display: 'contents' }}>
          <dt className={styles.factLabel}>Last updated</dt>
          <dd className={styles.factValue}>{formatTimestamp(topology.generated_utc)}</dd>
        </div>
        {component.facts.map((fact) => (
          <div key={fact.label} style={{ display: 'contents' }}>
            <dt className={styles.factLabel}>{fact.label}</dt>
            <dd className={styles.factValue}>{fact.value}</dd>
          </div>
        ))}
      </dl>
      {component.policies ? (
        <ul className={styles.policyList} aria-label="Ingress network policy details">
          {component.policies.map((policy) => (
            <li key={policy.name} className={styles.policyListItem}>
              <strong>{policy.name}</strong>
              <span className={styles.policyMeta}>
                {policy.direction} · {policy.effect} · {policy.impact.replaceAll('_', ' ')}
              </span>
              <span className={styles.policyMeta}>Selector: {policy.selector}</span>
            </li>
          ))}
        </ul>
      ) : null}
      <DetailContent component={component} topology={topology} projectId={projectId} />
    </Card>
  );
}

export function ClusterTopologyGraph({
  topology,
  projectId,
}: {
  topology: KubernetesTopologyDto;
  projectId?: string;
}) {
  const styles = useStyles();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const components = useMemo(() => buildComponents(topology), [topology]);
  const selected = components.find((component) => component.id === selectedId) ?? null;

  const { nodes, edges } = useMemo(() => {
    const positions: Record<string, { x: number; y: number }> = {
      'public-entry': { x: 0, y: 0 },
      'network-policies': { x: 0, y: 140 },
      'service-targets': { x: NODE_WIDTH + COLUMN_GAP, y: 0 },
      'control-plane': { x: NODE_WIDTH + COLUMN_GAP, y: 140 },
      'workload-pods': { x: NODE_WIDTH + COLUMN_GAP, y: 280 },
      'agent-execution': { x: NODE_WIDTH + COLUMN_GAP, y: 400 },
      'application-state': { x: (NODE_WIDTH + COLUMN_GAP) * 2, y: 0 },
      'session-artifacts': { x: (NODE_WIDTH + COLUMN_GAP) * 2, y: 140 },
      'sandbox-template': { x: (NODE_WIDTH + COLUMN_GAP) * 3, y: 0 },
      'sandbox-pool': { x: (NODE_WIDTH + COLUMN_GAP) * 3, y: 140 },
      'capacity-scaling': { x: (NODE_WIDTH + COLUMN_GAP) * 2, y: 280 },
      'sandbox-claim': { x: (NODE_WIDTH + COLUMN_GAP) * 3, y: 280 },
      sandbox: { x: (NODE_WIDTH + COLUMN_GAP) * 3, y: 400 },
    };
    const visibleIds = new Set(components.map((component) => component.id));
    const hasGatewayAllow = components.find((item) => item.id === 'network-policies')
      ?.policies?.some((policy) => policy.impact === 'gateway_ingress') ?? false;
    const relationships: Array<{ source: string; target: string; tone?: EdgeTone }> = [
      { source: 'public-entry', target: 'service-targets', tone: hasGatewayAllow ? 'allowed' : 'default' },
      { source: 'service-targets', target: 'control-plane', tone: hasGatewayAllow ? 'allowed' : 'default' },
      { source: 'control-plane', target: 'workload-pods' },
      { source: 'control-plane', target: 'application-state' },
      { source: 'agent-execution', target: 'session-artifacts' },
      { source: 'agent-execution', target: 'sandbox-claim' },
      { source: 'sandbox-template', target: 'sandbox-pool' },
      { source: 'sandbox-pool', target: 'sandbox-claim' },
      { source: 'sandbox-claim', target: 'sandbox' },
    ];

    const nodes = components.map((component) => ({
        id: component.id,
        type: 'resource',
        data: { component, onSelect: setSelectedId },
        position: positions[component.id] ?? { x: 0, y: 0 },
      }) satisfies Node);
    const edges = relationships
      .filter(({ source, target }) => visibleIds.has(source) && visibleIds.has(target))
      .map(({ source, target, tone }) => topologyEdge(source, target, tone));
    return { nodes, edges: routeGridEdges(edges, nodes) };
  }, [components]);

  return (
    <div className={styles.container} data-testid="cluster-topology-graph">
      <div className={styles.layerStatus}>
        {topology.layers.filter((layer) => topology.requested_layers.includes(layer.name)).map((layer) => (
          <Badge
            key={layer.name}
            appearance="tint"
            color={layer.status === 'available' ? 'success' : layer.status === 'partial' ? 'warning' : 'danger'}
            title={layer.message}
          >
            {layer.name}: {layer.resource_count}
          </Badge>
        ))}
        {topology.truncated ? <Badge color="warning">Result truncated</Badge> : null}
      </div>
      <div className={styles.graphViewport} data-testid="cluster-topology-viewport">
        <ReactFlow
          className={styles.graphCanvas}
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          edgeTypes={workflowEdgeTypes}
          fitView
          fitViewOptions={{ padding: 0.15, maxZoom: 1 }}
          minZoom={0.15}
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
      {selected ? (
        <TopologyInspector
          component={selected}
          topology={topology}
          projectId={projectId}
          onClose={() => setSelectedId(null)}
        />
      ) : null}
    </div>
  );
}
