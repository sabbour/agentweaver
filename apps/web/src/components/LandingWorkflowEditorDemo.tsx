import { Badge, Button, FluentProvider, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { SparkleRegular } from '@fluentui/react-icons';
import { ReactFlow, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { createRoot } from 'react-dom/client';
import { useEffect, useMemo, useRef, useState } from 'react';
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

type WorkflowPreset = {
  id: string;
  name: string;
  description: string;
  schedule?: string;
  steps: WorkflowStep[];
};

const PRESETS: WorkflowPreset[] = [
  {
    id: 'pm-delivery',
    name: 'Product Management',
    description: 'Turn a product request into a reviewed, shippable plan.',
    schedule: '0 9 * * 1-5',
    steps: [
      { id: 'triage', label: 'Triage request', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
      { id: 'draft-spec', label: 'Draft spec', role: 'agent', nodeType: 'subtask', modelId: 'claude-opus-4.8' },
      { id: 'break-tasks', label: 'Break into tasks', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
      { id: 'build-test', label: 'Build & Test', role: 'build_test', nodeType: 'gate' },
      { id: 'rai', label: 'RAI check', role: 'rai', nodeType: 'gate' },
      { id: 'review', label: 'Human review', role: 'review', nodeType: 'gate' },
      { id: 'ship', label: 'Merge & ship', role: 'merge', nodeType: 'action' },
    ],
  },
  {
    id: 'content-author',
    name: 'Content Author',
    description: 'Research, write, review, and publish a finished story.',
    steps: [
      { id: 'research', label: 'Research brief', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
      { id: 'draft', label: 'Draft article', role: 'agent', nodeType: 'subtask', modelId: 'claude-opus-4.8' },
      { id: 'edit', label: 'Edit for voice', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
      { id: 'rai', label: 'RAI check', role: 'rai', nodeType: 'gate' },
      { id: 'review', label: 'Editorial review', role: 'review', nodeType: 'gate' },
      { id: 'publish', label: 'Publish', role: 'merge', nodeType: 'action' },
    ],
  },
  {
    id: 'bug-triage',
    name: 'Bug Triage',
    description: 'Route a customer report through reproduction, repair, and release.',
    schedule: '0 */4 * * *',
    steps: [
      { id: 'triage', label: 'Triage request', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
      { id: 'reproduce', label: 'Reproduce issue', role: 'agent', nodeType: 'subtask', modelId: 'claude-sonnet-5' },
      { id: 'fix', label: 'Implement fix', role: 'agent', nodeType: 'subtask', modelId: 'claude-opus-4.8' },
      { id: 'build-test', label: 'Build & Test', role: 'build_test', nodeType: 'gate' },
      { id: 'rai', label: 'RAI check', role: 'rai', nodeType: 'gate' },
      { id: 'review', label: 'Human review', role: 'review', nodeType: 'gate' },
      { id: 'merge', label: 'Merge', role: 'merge', nodeType: 'action' },
    ],
  },
];

const GENERATION_PROMPTS = [
  { prompt: 'When a customer files a bug, triage and route it.', presetId: 'bug-triage' },
  { prompt: 'Publish a blog post once it is reviewed.', presetId: 'content-author' },
  { prompt: 'Turn a product request into a spec, tasks, and a reviewed delivery plan.', presetId: 'pm-delivery' },
];

const COORDINATOR_STEP: WorkflowStep = {
  id: 'coordinator',
  label: 'Coordinator dispatch',
  role: 'coordinator',
  nodeType: 'action',
  modelId: 'claude-sonnet-5',
};

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', minWidth: 0, color: tokens.colorNeutralForeground1 },
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
  presetBar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  presetLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100, fontWeight: tokens.fontWeightSemibold },
  body: {
    display: 'grid',
    gridTemplateColumns: 'minmax(280px, 0.45fr) minmax(0, 1.55fr)',
    minHeight: '580px',
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
    fontSize: tokens.fontSizeBase200,
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
    fontSize: tokens.fontSizeBase200,
    lineHeight: '1.7',
    whiteSpace: 'pre',
  },
  generatedLine: { color: tokens.colorPaletteGreenForeground1, fontWeight: tokens.fontWeightSemibold },
  hint: { marginTop: tokens.spacingVerticalM, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: '1.5' },
  graphPane: {
    position: 'relative',
    minWidth: 0,
    minHeight: '580px',
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 760px)': { minHeight: '460px' },
  },
  graphLabel: {
    position: 'absolute',
    zIndex: 2,
    top: tokens.spacingVerticalM,
    left: tokens.spacingHorizontalL,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    pointerEvents: 'none',
  },
  coordinatorNote: {
    position: 'absolute',
    zIndex: 2,
    right: tokens.spacingHorizontalL,
    bottom: tokens.spacingVerticalM,
    maxWidth: '300px',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase100,
    lineHeight: '1.45',
    boxShadow: tokens.shadow4,
    pointerEvents: 'none',
  },
  graph: { width: '100%', height: '100%', '& .react-flow__pane': { cursor: 'default' } },
});

function gateKind(role: string): string | undefined {
  return {
    build_test: 'build-test',
    rai: 'rai',
    review: 'human-review',
  }[role];
}

function yamlLines(preset: WorkflowPreset, generatedPrompt: string | null): { text: string; generated?: boolean }[] {
  return [
    { text: `id: ${preset.id}` },
    { text: `name: ${preset.name}` },
    ...(preset.schedule ? [{ text: `schedule: "${preset.schedule}"` }] : []),
    ...(generatedPrompt ? [{ text: `# Generated for: ${generatedPrompt}`, generated: true }] : []),
    { text: 'nodes:' },
    ...preset.steps.flatMap((step) => [
      { text: `  - id: ${step.id}` },
      { text: `    label: ${step.label}` },
      { text: `    role: ${step.role}` },
      ...(gateKind(step.role) ? [{ text: `    gate_kind: ${gateKind(step.role)}` }] : []),
    ]),
  ];
}

export function WorkflowEditorPreview() {
  const styles = useStyles();
  const [presetId, setPresetId] = useState(PRESETS[0].id);
  const [generationIndex, setGenerationIndex] = useState(0);
  const [generatedPrompt, setGeneratedPrompt] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const preset = PRESETS.find((candidate) => candidate.id === presetId) ?? PRESETS[0];

  useEffect(() => () => {
    if (timeoutRef.current) clearTimeout(timeoutRef.current);
  }, []);

  const selectPreset = (id: string) => {
    if (timeoutRef.current) clearTimeout(timeoutRef.current);
    setGenerating(false);
    setPresetId(id);
    setGeneratedPrompt(null);
  };

  const generate = () => {
    if (generating) return;
    const sample = GENERATION_PROMPTS[generationIndex];
    setGenerating(true);
    setGeneratedPrompt(sample.prompt);
    timeoutRef.current = setTimeout(() => {
      setPresetId(sample.presetId);
      setGenerationIndex((index) => (index + 1) % GENERATION_PROMPTS.length);
      setGenerating(false);
      timeoutRef.current = undefined;
    }, 850);
  };

  const graphSteps = useMemo(() => [COORDINATOR_STEP, ...preset.steps], [preset]);
  const { nodes, edges } = useMemo(() => {
    const rawNodes: Node[] = graphSteps.map((step) => ({
      id: step.id,
      type: 'workflow',
      position: { x: 0, y: 0 },
      data: {},
    }));
    const sizeHints: Record<string, NodeSizeHint> = Object.fromEntries(
      graphSteps.map((step) => [step.id, workflowNodeSizeHint(step.nodeType)]),
    );
    const rawEdges = graphSteps.slice(1).map((step, index) => forwardEdge(
      `edge-${graphSteps[index].id}-${step.id}`,
      graphSteps[index].id,
      step.id,
    ));
    const laidOut = layoutDagStaircase(
      rawNodes,
      rawEdges,
      { rankdir: 'LR', rankSep: 52, nodeSep: 32, targetAspect: 1.6, minStepRanks: 3 },
      sizeHints,
    );
    const positions = new Map(laidOut.map((node) => [node.id, node.position]));

    return {
      nodes: graphSteps.map<Node<WorkflowNodeData>>((step) => ({
        id: step.id,
        type: 'workflow',
        position: positions.get(step.id) ?? { x: 0, y: 0 },
        initialWidth: sizeHints[step.id].width,
        initialHeight: sizeHints[step.id].height,
        data: {
          def: { key: step.role, label: step.label, roleDescription: roleDescForRole(step.role), Icon: iconForRole(step.role) },
          state: { status: step.id === COORDINATOR_STEP.id ? 'started' : 'completed' },
          modelId: step.modelId,
          nodeType: step.nodeType,
          dir: 'GRID',
        },
      })),
      edges: routeGridEdges(rawEdges, laidOut),
    };
  }, [graphSteps]);

  return (
    <FluentProvider theme={agentweaverLightTheme}>
      <div className={styles.root}>
        <div className={styles.header}>
          <div>
            <div className={styles.title}>{generating ? 'Generating workflow…' : preset.name}</div>
            <Text className={styles.subtitle}>{generating ? generatedPrompt : preset.description}</Text>
          </div>
          <div className={styles.headerMeta}>
            <Badge appearance="outline">{preset.steps.length} declared stages</Badge>
            <Button appearance="primary" size="small" icon={generating ? <Spinner size="extra-tiny" /> : <SparkleRegular />} disabled={generating} onClick={generate}>
              {generating ? 'Generating…' : 'Generate'}
            </Button>
          </div>
        </div>
        <div className={styles.presetBar} aria-label="Workflow presets">
          <span className={styles.presetLabel}>Templates</span>
          {PRESETS.map((candidate) => (
            <Button
              key={candidate.id}
              appearance={candidate.id === preset.id && !generatedPrompt ? 'primary' : 'secondary'}
              size="small"
              onClick={() => selectPreset(candidate.id)}
            >
              {candidate.name}
            </Button>
          ))}
        </div>
        <div className={styles.body}>
          <div className={styles.definition}>
            <div className={styles.paneLabel}>Workflow definition</div>
            <pre className={styles.yaml} aria-label="Workflow YAML">
              {yamlLines(preset, generatedPrompt).map((line) => (
                <span className={line.generated ? styles.generatedLine : undefined} key={line.text}>
                  {line.text}{'\n'}
                </span>
              ))}
            </pre>
            <Text className={styles.hint}>
              {generating ? 'Drafting a YAML workflow from the request…' : 'Select a template, or generate a draft from a plain-language request. This preview never calls an API.'}
            </Text>
          </div>
          <div className={styles.graphPane} aria-label="Rendered workflow graph">
            <div className={styles.graphLabel}>Declared topology</div>
            <ReactFlow
              className={styles.graph}
              nodes={nodes}
              edges={edges}
              nodeTypes={workflowNodeTypes}
              edgeTypes={workflowEdgeTypes}
              fitView
              fitViewOptions={{ padding: 0.2, maxZoom: 1.22 }}
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
            <div className={styles.coordinatorNote}>
              The Coordinator breaks this into tasks and dispatches them to your team automatically.
            </div>
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
