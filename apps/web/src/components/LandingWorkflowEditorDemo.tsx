import { Badge, Button, FluentProvider, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { SparkleRegular } from '@fluentui/react-icons';
import { ReactFlow, type Edge, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { createRoot } from 'react-dom/client';
import { useEffect, useMemo, useRef, useState } from 'react';
import { agentweaverLightTheme } from '../theme';
import {
  forwardEdge,
  loopbackEdge,
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

type WorkflowNodeType = 'prompt' | 'peer_review' | 'check' | 'build_test' | 'merge' | 'open_pull_request' | 'scribe' | 'terminal';

type WorkflowStep = {
  id: string;
  type: WorkflowNodeType;
  label: string;
  role: string;
  kind: 'live' | 'gate' | 'terminal' | 'action' | 'agent';
  agent?: string;
  prompt?: string;
  gateKind?: string;
  branches?: string[];
  modelId?: string;
};

type WorkflowEdge = {
  from: string;
  to: string;
  when?: string;
};

type WorkflowPreset = {
  id: string;
  name: string;
  description: string;
  version: string;
  start: string;
  steps: WorkflowStep[];
  edges: WorkflowEdge[];
};

// eslint-disable-next-line react-refresh/only-export-components
export const WORKFLOW_PRESETS: WorkflowPreset[] = [
  {
    id: 'pm-delivery',
    name: 'Product Management',
    description: 'Turn a product request into a reviewed, shippable plan.',
    version: '1.0',
    start: 'triage',
    steps: [
      { id: 'triage', type: 'prompt', label: 'Triage request', role: 'agent', kind: 'live', agent: 'product-manager', prompt: 'Clarify the request, user value, and delivery constraints.' },
      { id: 'draft-spec', type: 'prompt', label: 'Draft spec', role: 'agent', kind: 'live', agent: 'product-manager', prompt: 'Write an actionable product specification with acceptance criteria.' },
      { id: 'break-tasks', type: 'prompt', label: 'Break into tasks', role: 'agent', kind: 'live', agent: 'product-manager', prompt: 'Break the approved specification into independent delivery tasks.' },
      { id: 'review-gate', type: 'check', label: 'Plan review', role: 'review', kind: 'gate', gateKind: 'human-review', branches: ['approved', 'request-changes', 'declined'] },
      { id: 'terminal-declined', type: 'terminal', label: 'Declined', role: 'plumbing', kind: 'terminal' },
      { id: 'done', type: 'terminal', label: 'Done', role: 'plumbing', kind: 'terminal' },
    ],
    edges: [
      { from: 'triage', to: 'draft-spec' },
      { from: 'draft-spec', to: 'break-tasks' },
      { from: 'break-tasks', to: 'review-gate' },
      { from: 'review-gate', to: 'done', when: 'approved' },
      { from: 'review-gate', to: 'draft-spec', when: 'request-changes' },
      { from: 'review-gate', to: 'terminal-declined', when: 'declined' },
    ],
  },
  {
    id: 'content-author',
    name: 'Content Author',
    description: 'Research, write, review, and publish a finished story.',
    version: '1.0',
    start: 'research',
    steps: [
      { id: 'research', type: 'prompt', label: 'Research brief', role: 'agent', kind: 'live', agent: 'content-author', prompt: 'Research the topic and prepare a factual brief.' },
      { id: 'draft', type: 'prompt', label: 'Draft article', role: 'agent', kind: 'live', agent: 'content-author', prompt: 'Write the article from the approved research brief.' },
      { id: 'editorial-review', type: 'peer_review', label: 'Editorial review', role: 'review', kind: 'live', agent: 'editor', prompt: 'Review the draft for accuracy, voice, and publication readiness.' },
      { id: 'rai-check', type: 'check', label: 'RAI check', role: 'review', kind: 'gate', gateKind: 'rai', branches: ['revise', 'safety-failed', 'review'] },
      { id: 'publish', type: 'open_pull_request', label: 'Publish', role: 'action', kind: 'live' },
      { id: 'scribe', type: 'scribe', label: 'Record publication', role: 'scribe', kind: 'agent' },
      { id: 'terminal-declined', type: 'terminal', label: 'Declined', role: 'plumbing', kind: 'terminal' },
      { id: 'terminal-safety-failed', type: 'terminal', label: 'Safety Failed', role: 'plumbing', kind: 'terminal' },
      { id: 'done', type: 'terminal', label: 'Done', role: 'plumbing', kind: 'terminal' },
    ],
    edges: [
      { from: 'research', to: 'draft' },
      { from: 'draft', to: 'editorial-review' },
      { from: 'editorial-review', to: 'rai-check', when: 'approved' },
      { from: 'editorial-review', to: 'draft', when: 'request-changes' },
      { from: 'editorial-review', to: 'terminal-declined', when: 'declined' },
      { from: 'rai-check', to: 'draft', when: 'revise' },
      { from: 'rai-check', to: 'terminal-safety-failed', when: 'safety-failed' },
      { from: 'rai-check', to: 'publish', when: 'review' },
      { from: 'publish', to: 'scribe' },
      { from: 'scribe', to: 'done' },
    ],
  },
  {
    id: 'bug-triage',
    name: 'Bug Triage',
    description: 'Route a customer report through reproduction, repair, and release.',
    version: '1.0',
    start: 'triage',
    steps: [
      { id: 'triage', type: 'prompt', label: 'Triage request', role: 'agent', kind: 'live', agent: 'bug-triager', prompt: 'Reproduce the issue and identify its root cause.' },
      { id: 'fix', type: 'prompt', label: 'Implement fix', role: 'agent', kind: 'live', agent: 'software-engineer', prompt: 'Implement the smallest safe fix with regression coverage.' },
      { id: 'verify', type: 'peer_review', label: 'Verify fix', role: 'review', kind: 'live', agent: 'qa-engineer', prompt: 'Verify the fix resolves the defect without regressions.' },
      { id: 'rai-check', type: 'check', label: 'RAI check', role: 'review', kind: 'gate', gateKind: 'rai', branches: ['revise', 'safety-failed', 'review'] },
      { id: 'build-test', type: 'build_test', label: 'Build & Test', role: 'build_test', kind: 'live', agent: 'qa-engineer' },
      { id: 'terminal-safety-failed', type: 'terminal', label: 'Safety Failed', role: 'plumbing', kind: 'terminal' },
      { id: 'done', type: 'terminal', label: 'Done', role: 'plumbing', kind: 'terminal' },
    ],
    edges: [
      { from: 'triage', to: 'fix' },
      { from: 'fix', to: 'verify' },
      { from: 'verify', to: 'rai-check', when: 'approved' },
      { from: 'verify', to: 'fix', when: 'request-changes' },
      { from: 'rai-check', to: 'fix', when: 'revise' },
      { from: 'rai-check', to: 'terminal-safety-failed', when: 'safety-failed' },
      { from: 'rai-check', to: 'build-test', when: 'review' },
      { from: 'build-test', to: 'done', when: 'approved' },
      { from: 'build-test', to: 'fix', when: 'request-changes' },
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
  type: 'scribe',
  label: 'Coordinator dispatch',
  role: 'coordinator',
  kind: 'agent',
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

function graphNodeType(step: WorkflowStep): 'subtask' | 'gate' | 'action' | 'terminal' {
  if (step.type === 'check' || step.type === 'build_test') return 'gate';
  if (step.type === 'terminal') return 'terminal';
  if (step.type === 'merge' || step.type === 'open_pull_request' || step.type === 'scribe') return 'action';
  return 'subtask';
}

// eslint-disable-next-line react-refresh/only-export-components
export function yamlLines(preset: WorkflowPreset, generatedPrompt: string | null): { text: string; generated?: boolean }[] {
  return [
    { text: `id: ${preset.id}` },
    { text: `name: ${preset.name}` },
    { text: `description: ${preset.description}` },
    { text: `version: "${preset.version}"` },
    { text: `start: ${preset.start}` },
    ...(generatedPrompt ? [{ text: `# Generated for: ${generatedPrompt}`, generated: true }] : []),
    { text: 'nodes:' },
    ...preset.steps.flatMap((step) => [
      { text: `  - id: ${step.id}` },
      { text: `    type: ${step.type}` },
      { text: `    label: ${step.label}` },
      { text: `    role: ${step.role}` },
      { text: `    kind: ${step.kind}` },
      ...(step.gateKind ? [{ text: `    gate_kind: ${step.gateKind}` }] : []),
      ...(step.branches ? [{ text: '    branches:' }, ...step.branches.map((branch) => ({ text: `      - ${branch}` }))] : []),
      ...(step.agent ? [{ text: `    agent: ${step.agent}` }] : []),
      ...(step.prompt ? [{ text: `    prompt: ${step.prompt}` }] : []),
    ]),
    { text: 'edges:' },
    ...preset.edges.flatMap((edge) => [
      { text: `  - from: ${edge.from}` },
      { text: `    to: ${edge.to}` },
      ...(edge.when ? [{ text: `    when: ${edge.when}` }] : []),
    ]),
  ];
}

// eslint-disable-next-line react-refresh/only-export-components
export function workflowGraphEdges(preset: WorkflowPreset): Edge[] {
  const order = new Map([COORDINATOR_STEP, ...preset.steps].map((step, index) => [step.id, index]));
  return [
    forwardEdge(`edge-${COORDINATOR_STEP.id}-${preset.start}`, COORDINATOR_STEP.id, preset.start),
    ...preset.edges.map((edge) => {
      const id = `edge-${edge.from}-${edge.to}-${edge.when ?? 'next'}`;
      const isLoopback = (order.get(edge.to) ?? 0) <= (order.get(edge.from) ?? 0);
      return isLoopback
        ? loopbackEdge(id, edge.from, edge.to, edge.when ?? 'return')
        : { ...forwardEdge(id, edge.from, edge.to), label: edge.when };
    }),
  ];
}

export function WorkflowEditorPreview() {
  const styles = useStyles();
  const [presetId, setPresetId] = useState(WORKFLOW_PRESETS[0].id);
  const [generationIndex, setGenerationIndex] = useState(0);
  const [generatedPrompt, setGeneratedPrompt] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const preset = WORKFLOW_PRESETS.find((candidate) => candidate.id === presetId) ?? WORKFLOW_PRESETS[0];

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
      graphSteps.map((step) => [step.id, workflowNodeSizeHint(graphNodeType(step))]),
    );
    const rawEdges = workflowGraphEdges(preset);
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
          nodeType: graphNodeType(step),
          dir: 'GRID',
        },
      })),
      edges: routeGridEdges(rawEdges, laidOut),
    };
  }, [graphSteps, preset]);

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
          {WORKFLOW_PRESETS.map((candidate) => (
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
