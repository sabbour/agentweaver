import {
  Badge,
  FluentProvider,
  Text,
  Tree,
  TreeItem,
  TreeItemLayout,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  FlowchartRegular,
} from '@fluentui/react-icons';
import { MiniMap, Panel, ReactFlow, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { createRoot } from 'react-dom/client';
import { useMemo, useState } from 'react';
import { agentweaverLightTheme } from '../theme';
import { AgentAvatar } from './AgentAvatar';
import { GraphControls } from './CoordinatorTopologyGraph';
import {
  forwardEdge,
  iconForRole,
  roleDescForRole,
  workflowEdgeTypes,
  workflowNodeTypes,
  type StepStatus,
  type WorkflowNodeData,
} from './WorkflowGraphPanel';

type DemoNode = {
  id: string;
  label: string;
  role: string;
  status: StepStatus;
  statusLabel: string;
  agentName?: string;
  agentRoleTitle?: string;
  modelId?: string;
  pod?: string;
  position: { x: number; y: number };
};

const startedAt = Date.now() - 103_000;

const demoNodes: DemoNode[] = [
  {
    id: 'coordinator',
    label: 'Coordinator',
    role: 'coordinator',
    status: 'started',
    statusLabel: 'Running',
    agentRoleTitle: 'Coordinator',
    modelId: 'claude-sonnet-5',
    position: { x: 0, y: 190 },
  },
  {
    id: 'outcome',
    label: 'Outcome plan',
    role: 'outcome_plan',
    status: 'completed',
    statusLabel: 'Confirmed',
    agentRoleTitle: 'Planning gate',
    position: { x: 210, y: 100 },
  },
  {
    id: 'work-plan',
    label: 'Work plan',
    role: 'work_plan',
    status: 'completed',
    statusLabel: 'Completed',
    agentRoleTitle: 'Work planning',
    position: { x: 420, y: 100 },
  },
  {
    id: 'architecture',
    label: 'Design launch-control UX',
    role: 'agent',
    status: 'completed',
    statusLabel: 'Ready',
    agentName: 'Morpheus',
    agentRoleTitle: 'Lead Architect',
    modelId: 'claude-opus-4.8',
    position: { x: 640, y: 12 },
  },
  {
    id: 'implementation',
    label: 'Implement console and fixtures',
    role: 'agent',
    status: 'started',
    statusLabel: 'Running',
    agentName: 'Tank',
    agentRoleTitle: 'Core Implementer',
    modelId: 'claude-sonnet-5',
    pod: 'agentweaver-agent-host-bbkn5',
    position: { x: 860, y: 12 },
  },
  {
    id: 'tests',
    label: 'Verify readiness behavior',
    role: 'agent',
    status: 'pending',
    statusLabel: 'Pending',
    agentName: 'Tank',
    agentRoleTitle: 'Core Implementer',
    modelId: 'claude-sonnet-5',
    position: { x: 1080, y: -78 },
  },
  {
    id: 'docs',
    label: 'Write operator guidance',
    role: 'agent',
    status: 'pending',
    statusLabel: 'Pending',
    agentName: 'Link',
    agentRoleTitle: 'Docs Writer',
    modelId: 'claude-sonnet-5',
    position: { x: 1080, y: 106 },
  },
  {
    id: 'review',
    label: 'Human review',
    role: 'review',
    status: 'pending',
    statusLabel: 'Pending',
    agentRoleTitle: 'Human Review',
    position: { x: 1300, y: 12 },
  },
];

const demoEdges = [
  forwardEdge('coordinator-outcome', 'coordinator', 'outcome'),
  forwardEdge('outcome-work', 'outcome', 'work-plan'),
  forwardEdge('work-architecture', 'work-plan', 'architecture'),
  forwardEdge('architecture-implementation', 'architecture', 'implementation'),
  forwardEdge('implementation-tests', 'implementation', 'tests'),
  forwardEdge('implementation-docs', 'implementation', 'docs'),
  forwardEdge('tests-review', 'tests', 'review'),
  forwardEdge('docs-review', 'docs', 'review'),
];

const useStyles = makeStyles({
  root: {
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    overflow: 'hidden',
    boxShadow: '0 24px 70px rgba(15, 13, 12, 0.16)',
  },
  header: {
    minHeight: '68px',
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 600px)': {
      alignItems: 'flex-start',
      flexDirection: 'column',
    },
  },
  headingGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  title: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  headerMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexShrink: 0,
    '@media (max-width: 600px)': {
      flexWrap: 'wrap',
    },
  },
  body: {
    display: 'grid',
    gridTemplateColumns: '300px minmax(0, 1fr)',
    minHeight: '610px',
    '@media (max-width: 860px)': {
      gridTemplateColumns: '1fr',
      gridTemplateRows: '330px 720px',
      minHeight: '1050px',
    },
  },
  treeRail: {
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    overflow: 'hidden',
    '@media (max-width: 860px)': {
      borderRightStyle: 'none',
      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
      maxHeight: '330px',
    },
  },
  railHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: tokens.spacingVerticalM,
  },
  railTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  tree: {
    maxHeight: '500px',
    overflowY: 'auto',
    paddingRight: tokens.spacingHorizontalXS,
  },
  treeLayout: {
    minHeight: '52px',
    borderRadius: tokens.borderRadiusMedium,
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
  },
  treeSelected: {
    backgroundColor: tokens.colorNeutralBackground3,
  },
  treeBody: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    minWidth: 0,
  },
  treeCopy: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  treePrimary: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  treeMeta: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  graphPanel: {
    position: 'relative',
    minWidth: 0,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  graphHint: {
    position: 'absolute',
    zIndex: 8,
    left: tokens.spacingHorizontalL,
    top: tokens.spacingVerticalM,
    maxWidth: '420px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    pointerEvents: 'none',
  },
  graph: {
    width: '100%',
    height: '610px',
    '& .react-flow__pane': {
      cursor: 'grab',
    },
    '& .react-flow__pane:active': {
      cursor: 'grabbing',
    },
    '@media (max-width: 860px)': {
      height: '720px',
    },
  },
});

function statusIcon(status: StepStatus) {
  if (status === 'completed') return <CheckmarkCircleRegular aria-hidden="true" />;
  if (status === 'started') return <ArrowSyncRegular aria-hidden="true" />;
  return <CircleRegular aria-hidden="true" />;
}

function LandingWorkflowDemo() {
  const styles = useStyles();
  const [selectedId, setSelectedId] = useState('implementation');

  const nodes = useMemo<Node<WorkflowNodeData>[]>(
    () =>
      demoNodes.map((node) => ({
        id: node.id,
        type: 'workflow',
        selected: node.id === selectedId,
        position: node.position,
        data: {
          def: {
            key: node.role,
            label: node.label,
            roleDescription: roleDescForRole(node.role),
            Icon: iconForRole(node.role),
          },
          state: {
            status: node.status,
            startedAt: node.status === 'started' ? startedAt : undefined,
            message: node.status === 'started' ? 'Executing in an isolated sandbox' : undefined,
          },
          agentName: node.agentName,
          agentRoleTitle: node.agentRoleTitle,
          modelId: node.modelId,
          executionId: node.id,
          executionPodName: node.pod,
          dir: 'LR',
        },
      })),
    [selectedId],
  );

  return (
    <FluentProvider theme={agentweaverLightTheme}>
      <div className={styles.root}>
        <div className={styles.header}>
          <div className={styles.headingGroup}>
            <div className={styles.titleRow}>
              <FlowchartRegular aria-hidden="true" />
              <span className={styles.title}>Northstar Launch Control</span>
              <Badge appearance="filled" color="informative">Dispatching</Badge>
            </div>
            <Text className={styles.subtitle}>Software delivery · supervised orchestration</Text>
          </div>
          <div className={styles.headerMeta}>
            <Badge appearance="outline">8 nodes</Badge>
            <Badge appearance="outline">1 running</Badge>
            <Badge appearance="outline">18.6 AI credits</Badge>
          </div>
        </div>

        <div className={styles.body}>
          <aside className={styles.treeRail} aria-label="Interactive run tree">
            <div className={styles.railHeader}>
              <span className={styles.railTitle}>Run tree</span>
              <Text size={200}>8 nodes</Text>
            </div>
            <Tree
              aria-label="Run tree"
              className={styles.tree}
            >
              {demoNodes.map((node) => (
                <TreeItem
                  key={node.id}
                  value={node.id}
                  itemType="leaf"
                  aria-current={node.id === selectedId ? 'true' : undefined}
                  onClick={() => setSelectedId(node.id)}
                >
                  <TreeItemLayout
                    className={mergeClasses(
                      styles.treeLayout,
                      node.id === selectedId && styles.treeSelected,
                    )}
                    iconBefore={statusIcon(node.status)}
                  >
                    <span className={styles.treeBody}>
                      <AgentAvatar name={node.agentName ?? node.label} size={22} circle />
                      <span className={styles.treeCopy}>
                        <span className={styles.treePrimary}>{node.label}</span>
                        <span className={styles.treeMeta}>
                          {node.statusLabel}
                          {node.agentName ? ` · ${node.agentName} (${node.agentRoleTitle})` : ''}
                        </span>
                      </span>
                    </span>
                  </TreeItemLayout>
                </TreeItem>
              ))}
            </Tree>
          </aside>

          <section className={styles.graphPanel} aria-label="Interactive workflow graph">
            <Text className={styles.graphHint}>
              Drag to pan. Use the controls to zoom. Hover or focus a node to inspect its agent,
              model, duration, and sandbox.
            </Text>
            <ReactFlow
              className={styles.graph}
              nodes={nodes}
              edges={demoEdges}
              nodeTypes={workflowNodeTypes}
              edgeTypes={workflowEdgeTypes}
              defaultViewport={{ x: 42, y: 165, zoom: 0.78 }}
              minZoom={0.3}
              maxZoom={1.6}
              nodesDraggable={false}
              nodesConnectable={false}
              panOnDrag
              panOnScroll
              zoomOnPinch
              zoomOnScroll={false}
              onNodeClick={(_, node) => setSelectedId(node.id)}
              proOptions={{ hideAttribution: true }}
            >
              <Panel position="top-right">
                <GraphControls orderedNodeIds={demoNodes.map((node) => node.id)} />
              </Panel>
              <MiniMap
                pannable
                zoomable
                nodeStrokeWidth={0}
                nodeColor={(node) =>
                  node.id === selectedId
                    ? '#8a4b01'
                    : (node.data as WorkflowNodeData).state.status === 'completed'
                      ? '#16a149'
                      : '#b8afa8'
                }
                style={{
                  width: 112,
                  height: 76,
                  border: '1px solid var(--colorNeutralStroke2)',
                  borderRadius: 8,
                }}
              />
            </ReactFlow>
          </section>
        </div>
      </div>
    </FluentProvider>
  );
}

export function mountLandingWorkflowDemo(element: HTMLElement) {
  const root = createRoot(element);
  root.render(<LandingWorkflowDemo />);
  return () => root.unmount();
}
