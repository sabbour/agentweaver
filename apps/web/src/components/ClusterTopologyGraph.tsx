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
import type { CSSProperties } from 'react';
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
      [{ label: 'Function', value: 'Defines the isolated AgentHost sandbox pod shape' }],
    ));
    components.push(component(
      'sandbox-pool',
      'runtime',
      'Sandbox pool',
      'SandboxWarmPool · scaling',
      pools,
      plural(pools.length, 'warm pool'),
      [{ label: 'Function', value: 'Keeps sandbox capacity ready for new executions' }],
    ));
    components.push(component(
      'sandbox-claim',
      'runtime',
      'Sandbox claims',
      'SandboxClaim',
      claims,
      plural(claims.length, 'claim'),
      [{ label: 'Function', value: 'Binds a session execution to an available sandbox' }],
    ));
    components.push(component(
      'sandbox',
      'runtime',
      'Sandboxes',
      'Sandbox pod workload',
      claims,
      `${claims.length} bound or pending ${claims.length === 1 ? 'sandbox' : 'sandboxes'}`,
      [{ label: 'Function', value: 'Runs isolated AgentHost execution' }],
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
      [{ label: 'Function', value: 'Coordinates platform services and agent execution' }],
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
      [{ label: 'Function', value: 'Runs the selected Agentweaver deployment workloads' }],
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
      [{ label: 'Function', value: 'Retains Agentweaver application state where provisioned' }],
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
      [{ label: 'Function', value: 'Retains run worktrees, sandbox outputs, and session artifacts' }],
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
      [{ label: 'Function', value: 'Adjusts capacity for Agentweaver workloads' }],
    ));
  }

  return components;
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

function TopologyInspector({
  component,
  onClose,
}: {
  component: FunctionNode;
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
      <dl className={styles.facts}>
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
    </Card>
  );
}

export function ClusterTopologyGraph({ topology }: { topology: KubernetesTopologyDto }) {
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
      {selected ? <TopologyInspector component={selected} onClose={() => setSelectedId(null)} /> : null}
    </div>
  );
}
