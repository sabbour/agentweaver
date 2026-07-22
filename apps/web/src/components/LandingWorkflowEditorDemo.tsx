import { Badge, Button, FluentProvider, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ReactFlow, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { createRoot } from 'react-dom/client';
import { useMemo, useState } from 'react';
import { agentweaverLightTheme } from '../theme';
import {
  forwardEdge,
  iconForRole,
  roleDescForRole,
  workflowEdgeTypes,
  workflowNodeTypes,
  type WorkflowNodeData,
} from './WorkflowGraphPanel';
import {
  layoutDagStaircase,
  routeGridEdges,
  workflowNodeSizeHint,
  type NodeSizeHint,
} from '../utils/dagLayout';

type WorkflowStep = {
  id: string;
  label: string;
  role: string;
  nodeType: 'subtask' | 'gate' | 'action';
  modelId?: string;
};

const BASE_STEPS: WorkflowStep[] = [
  { id: 'implement', label: 'Implement change', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
  { id: 'build-test', label: 'Build & Test', role: 'build_test', nodeType: 'gate' },
  { id: 'rai', label: 'RAI check', role: 'rai', nodeType: 'gate' },
  { id: 'review', label: 'Human review', role: 'review', nodeType: 'gate' },
  { id: 'merge', label: 'Merge', role: 'merge', nodeType: 'action' },
];

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    color: tokens.colorNeutralForeground1,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 580px)': { alignItems: 'flex-start', flexDirection: 'column' },
  },
  title: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  subtitle: { display: 'block', marginTop: '2px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  headerMeta: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  body: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 0.66fr) minmax(0, 1.34fr)',
    minHeight: '430px',
    '@media (max-width: 760px)': { display: 'flex', flexDirection: 'column' },
  },
  definition: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    padding: tokens.spacingHorizontalL,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 760px)': { borderRight: 'none', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  },
  paneLabel: {
    marginBottom: tokens.spacingVerticalS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
  },
  yaml: {
    flex: 1,
    margin: 0,
    padding: tokens.spacingVerticalM,
    overflow: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    lineHeight: '1.65',
    whiteSpace: 'pre',
  },
  changedLine: { color: tokens.colorPaletteGreenForeground1, fontWeight: tokens.fontWeightSemibold },
  hint: { marginTop: tokens.spacingVerticalM, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100, lineHeight: '1.5' },
  graphPane: {
    position: 'relative',
    minWidth: 0,
    minHeight: '430px',
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 760px)': { minHeight: '370px' },
  },
  graphLabel: {
    position: 'absolute',
    zIndex: 2,
    top: tokens.spacingVerticalM,
    left: tokens.spacingHorizontalL,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    pointerEvents: 'none',
  },
  graph: { width: '100%', height: '100%', '& .react-flow__pane': { cursor: 'default' } },
});

function yamlLines(includeRai: boolean): { text: string; changed?: boolean }[] {
  return [
    { text: 'id: delivery-pipeline' },
    { text: 'schedule: "0 9 * * 1-5"' },
    { text: 'nodes:' },
    { text: '  - id: implement' },
    { text: '    role: agent' },
    { text: '  - id: build-test' },
    { text: '    gate_kind: build-test' },
    ...(includeRai
      ? [{ text: '  - id: rai', changed: true }, { text: '    gate_kind: rai', changed: true }]
      : []),
    { text: '  - id: review' },
    { text: '    gate_kind: human-review' },
    { text: '  - id: merge' },
  ];
}

export function WorkflowEditorPreview() {
  const styles = useStyles();
  const [includeRai, setIncludeRai] = useState(true);
  const steps = useMemo(
    () => BASE_STEPS.filter((step) => includeRai || step.id !== 'rai'),
    [includeRai],
  );

  const { nodes, edges } = useMemo(() => {
    const rawNodes: Node[] = steps.map((step) => ({
      id: step.id,
      type: 'workflow',
      position: { x: 0, y: 0 },
      data: {},
    }));
    const sizeHints: Record<string, NodeSizeHint> = Object.fromEntries(
      steps.map((step) => [step.id, workflowNodeSizeHint(step.nodeType)]),
    );
    const rawEdges = steps.slice(1).map((step, index) => forwardEdge(
      `edge-${steps[index].id}-${step.id}`,
      steps[index].id,
      step.id,
    ));
    const laidOut = layoutDagStaircase(
      rawNodes,
      rawEdges,
      { rankdir: 'LR', rankSep: 40, nodeSep: 20, targetAspect: 1.45, minStepRanks: 3 },
      sizeHints,
    );
    const positions = new Map(laidOut.map((node) => [node.id, node.position]));

    return {
      nodes: steps.map<Node<WorkflowNodeData>>((step) => ({
        id: step.id,
        type: 'workflow',
        position: positions.get(step.id) ?? { x: 0, y: 0 },
        initialWidth: sizeHints[step.id].width,
        initialHeight: sizeHints[step.id].height,
        data: {
          def: { key: step.role, label: step.label, roleDescription: roleDescForRole(step.role), Icon: iconForRole(step.role) },
          state: { status: 'completed' },
          modelId: step.modelId,
          nodeType: step.nodeType,
          dir: 'GRID',
        },
      })),
      edges: routeGridEdges(rawEdges, laidOut),
    };
  }, [steps]);

  return (
    <FluentProvider theme={agentweaverLightTheme}>
      <div className={styles.root}>
        <div className={styles.header}>
          <div>
            <div className={styles.title}>Delivery pipeline</div>
            <Text className={styles.subtitle}>workflow.yaml · saved locally in this preview</Text>
          </div>
          <div className={styles.headerMeta}>
            <Badge appearance="outline">{steps.length} stages</Badge>
            <Button appearance="secondary" size="small" onClick={() => setIncludeRai((value) => !value)}>
              {includeRai ? 'Remove RAI check' : 'Add RAI check'}
            </Button>
          </div>
        </div>
        <div className={styles.body}>
          <div className={styles.definition}>
            <div className={styles.paneLabel}>Workflow definition</div>
            <pre className={styles.yaml} aria-label="Workflow YAML">
              {yamlLines(includeRai).map((line) => (
                <span className={line.changed ? styles.changedLine : undefined} key={line.text}>
                  {line.text}{'\n'}
                </span>
              ))}
            </pre>
            <Text className={styles.hint}>
              Toggle the RAI gate to see the declared topology update. This preview never calls an API.
            </Text>
          </div>
          <div className={styles.graphPane} aria-label="Rendered workflow graph">
            <div className={styles.graphLabel}>Rendered graph</div>
            <ReactFlow
              className={styles.graph}
              nodes={nodes}
              edges={edges}
              nodeTypes={workflowNodeTypes}
              edgeTypes={workflowEdgeTypes}
              fitView
              fitViewOptions={{ padding: 0.18, maxZoom: 1.1 }}
              minZoom={0.2}
              maxZoom={1.5}
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
            />
          </div>
        </div>
      </div>
    </FluentProvider>
  );
}

// This non-component export is the VitePress lazy-mount boundary.
// eslint-disable-next-line react-refresh/only-export-components
export function mountLandingWorkflowEditorDemo(element: HTMLElement) {
  const root = createRoot(element);
  root.render(<WorkflowEditorPreview />);
  return () => root.unmount();
}
