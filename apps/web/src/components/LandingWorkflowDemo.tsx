import {
  Badge,
  Button,
  FluentProvider,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CheckmarkCircleFilled,
  CircleRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import { MiniMap, Panel, ReactFlow, type Edge, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { createRoot } from 'react-dom/client';
import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { agentweaverLightTheme } from '../theme';
import { ArtifactFrame } from './artifacts/ArtifactFrame';
import { GraphControls } from './CoordinatorTopologyGraph';
import { SCENARIOS } from './landing/scenarios';
import { STAGE, type Scenario, type ScenarioNode, type StageIndex } from './landing/types';
import { computeDispatchWaves, maxDispatchWave, planItemWave } from './landing/waves';
import { layoutScenarioGraph } from './landing/layout';
import {
  forwardEdge,
  iconForRole,
  roleDescForRole,
  workflowEdgeTypes,
  workflowNodeTypes,
  type StepStatus,
  type WorkflowNodeData,
} from './WorkflowGraphPanel';

// ---------------------------------------------------------------------------
// Timing — every value below is consumed only by the single run-token scheduler.
// ---------------------------------------------------------------------------
const TYPE_STEP = 2; // characters revealed per typing tick
const TYPE_MS = 34;
const STAGE_MS = 620;
const OUTCOME_MS = 1050;
const PLAN_MS = 950;
const DISPATCH_MS = 640;
const ARTIFACT_MS = 520;

// Node positions come from the deterministic layered-DAG layout helper
// (see landing/layout.ts). Nothing here multiplies hand-authored grid indices.

const DISCLAIMER =
  'Illustrative simulated runs. Outputs are authored examples, not professional advice or real actions.';

// ---------------------------------------------------------------------------
// Run-token state machine
// ---------------------------------------------------------------------------
type Phase = 'idle' | 'running' | 'paused' | 'complete';

interface RunState {
  activeId: string;
  stage: StageIndex;
  typedLen: number;
  dispatchStep: number;
  phase: Phase;
  /** Monotonic token; every restart/selection/pause bumps it so any in-flight
   *  timeout that still fires is ignored by the double-guard. */
  token: number;
}

type RunAction =
  | { type: 'SELECT'; id: string }
  | { type: 'REPLAY' }
  | { type: 'PLAY_IF_IDLE' }
  | { type: 'PAUSE' }
  | { type: 'TYPE_TICK'; goalLen: number }
  | { type: 'ADVANCE' }
  | { type: 'DISPATCH_TICK' }
  | { type: 'COMPLETE' };

function initialState(): RunState {
  return {
    activeId: SCENARIOS[0].id,
    stage: STAGE.TYPING,
    typedLen: 0,
    dispatchStep: 0,
    phase: 'idle',
    token: 1,
  };
}

function freshRun(activeId: string, phase: Phase, token: number): RunState {
  return { activeId, stage: STAGE.TYPING, typedLen: 0, dispatchStep: 0, phase, token };
}

function runReducer(state: RunState, action: RunAction): RunState {
  switch (action.type) {
    case 'SELECT':
      if (action.id === state.activeId) return state;
      return freshRun(action.id, 'idle', state.token + 1);
    case 'REPLAY':
      return freshRun(state.activeId, 'running', state.token + 1);
    case 'PLAY_IF_IDLE':
      return state.phase === 'idle' ? { ...state, phase: 'running', token: state.token + 1 } : state;
    case 'PAUSE':
      return state.phase === 'running' ? { ...state, phase: 'paused', token: state.token + 1 } : state;
    case 'TYPE_TICK':
      return { ...state, typedLen: Math.min(action.goalLen, state.typedLen + TYPE_STEP) };
    case 'ADVANCE':
      return { ...state, stage: Math.min(STAGE.ARTIFACT, state.stage + 1) as StageIndex };
    case 'DISPATCH_TICK':
      return { ...state, dispatchStep: state.dispatchStep + 1 };
    case 'COMPLETE':
      return { ...state, phase: 'complete' };
    default:
      return state;
  }
}

// ---------------------------------------------------------------------------
// Node status derivation (pure — no timers)
// ---------------------------------------------------------------------------
function nodeStatus(
  node: ScenarioNode,
  stage: StageIndex,
  dispatchStep: number,
  waves: Map<string, number>,
): { status: StepStatus; statusLabel: string; message?: string } {
  if (node.isReviewGate) {
    return { status: 'pending', statusLabel: 'Awaiting human review' };
  }
  if (node.role === 'coordinator') {
    // Coordinator carries no authored duration in any scenario.
    if (stage >= STAGE.ARTIFACT) return { status: 'completed', statusLabel: 'Done' };
    if (stage >= STAGE.OUTCOME) return { status: 'started', statusLabel: 'Coordinating', message: 'Directing the run' };
    return { status: 'pending', statusLabel: 'Queued' };
  }
  if (node.role === 'outcome_plan') {
    // Show the authored duration as subtext once the node is done.
    if (stage > STAGE.OUTCOME) return { status: 'completed', statusLabel: 'Confirmed', message: node.duration };
    if (stage === STAGE.OUTCOME) return { status: 'started', statusLabel: 'Drafting', message: 'Writing the outcome spec' };
    return { status: 'pending', statusLabel: 'Queued' };
  }
  if (node.role === 'work_plan') {
    if (stage > STAGE.PLAN) return { status: 'completed', statusLabel: 'Ready', message: node.duration };
    if (stage === STAGE.PLAN) return { status: 'started', statusLabel: 'Planning', message: 'Breaking down the work' };
    return { status: 'pending', statusLabel: 'Queued' };
  }
  // Specialist agents dispatch during the DISPATCH stage by dependency wave.
  // Every specialist in the same wave transitions together — the scheduler steps
  // one wave per tick, so concurrent nodes light up (and complete) as a group.
  const wave = waves.get(node.id) ?? Number.POSITIVE_INFINITY;
  // Show the authored duration as subtext on completed agent nodes (replaces generic "Finished").
  if (stage >= STAGE.ARTIFACT) return { status: 'completed', statusLabel: 'Ready', message: node.duration };
  if (stage < STAGE.DISPATCH) return { status: 'pending', statusLabel: 'Queued' };
  if (wave <= dispatchStep) return { status: 'completed', statusLabel: 'Ready', message: node.duration };
  if (wave === dispatchStep + 1) return { status: 'started', statusLabel: 'Running', message: 'Executing in an isolated sandbox' };
  return { status: 'pending', statusLabel: 'Queued' };
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------
const useStyles = makeStyles({
  root: {
    color: tokens.colorNeutralForeground1,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  disclaimer: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: '18px',
    margin: 0,
  },
  disclaimerMark: {
    flexShrink: 0,
    marginTop: '1px',
    color: tokens.colorNeutralForeground3,
  },
  tablist: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    overflowX: 'auto',
    scrollbarWidth: 'thin',
    padding: '4px',
    margin: '-4px',
    WebkitOverflowScrolling: 'touch',
  },
  tab: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '1px',
    flexShrink: 0,
    minWidth: '128px',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
    cursor: 'pointer',
    textAlign: 'left',
    fontFamily: 'inherit',
    transitionProperty: 'background-color, border-color, color',
    transitionDuration: tokens.durationNormal,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground2Hover,
      border: `1px solid ${tokens.colorNeutralStroke1}`,
    },
  },
  tabSelected: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
    border: `1px solid ${tokens.colorNeutralForeground1}`,
    color: tokens.colorNeutralForeground1,
    boxShadow: `inset 0 -2px 0 0 ${tokens.colorNeutralForeground1}`,
  },
  tabLabel: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    whiteSpace: 'nowrap',
  },
  tabHint: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
    whiteSpace: 'nowrap',
  },
  panel: {
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    overflow: 'hidden',
    boxShadow: '0 24px 70px rgba(15, 13, 12, 0.16)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 640px)': {
      flexDirection: 'column',
      alignItems: 'stretch',
    },
  },
  headingGroup: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  titleRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  title: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase500 },
  subtitle: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  headerMeta: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexShrink: 0, flexWrap: 'wrap' },
  stepper: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    overflowX: 'auto',
    scrollbarWidth: 'none',
  },
  step: { display: 'flex', alignItems: 'center', gap: '5px', flexShrink: 0 },
  stepDot: {
    width: '16px',
    height: '16px',
    borderRadius: '50%',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '11px',
    color: tokens.colorNeutralForeground3,
  },
  stepDotActive: { color: tokens.colorNeutralForeground1 },
  stepDotDone: { color: tokens.colorPaletteGreenForeground1 },
  stepLabel: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  stepLabelActive: { color: tokens.colorNeutralForeground1, fontWeight: tokens.fontWeightSemibold },
  stepArrow: { color: tokens.colorNeutralStroke1, fontSize: '11px', flexShrink: 0 },
  // The run surface: one stage that frames the graph as a centred horizontal
  // band. A compact goal strip caps the top and a compact plan panel anchors the
  // bottom-left, so the space around a wide-short DAG reads as intentional
  // composition rather than blank quadrants. The artifact floats over this in a
  // bounded window at the final beat — the graph never unmounts.
  stage: {
    position: 'relative',
    minWidth: 0,
    height: 'clamp(420px, 50vh, 512px)',
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
    '@media (max-width: 720px)': { height: '540px' },
    '@media (max-width: 380px)': { height: '516px' },
  },
  // The graph layer fills the stage and dims to settled context under the
  // artifact window (still visible, no longer the focus).
  graphLayer: {
    position: 'absolute',
    inset: 0,
    minWidth: 0,
    transitionProperty: 'opacity, filter',
    transitionDuration: '360ms',
    transitionTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '1ms' },
  },
  graphSettled: {
    opacity: 0.42,
    filter: 'saturate(0.72)',
  },
  graph: {
    width: '100%',
    height: '100%',
    '& .react-flow__pane': { cursor: 'grab' },
    '& .react-flow__pane:active': { cursor: 'grabbing' },
  },
  graphHint: {
    position: 'absolute',
    zIndex: 6,
    right: tokens.spacingHorizontalM,
    bottom: tokens.spacingVerticalS,
    maxWidth: '220px',
    textAlign: 'right',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    lineHeight: '15px',
    pointerEvents: 'none',
    '@media (max-width: 900px)': { display: 'none' },
  },
  // Compact, integrated goal typing surface pinned to the top of the stage.
  goalStrip: {
    position: 'absolute',
    zIndex: 7,
    top: 0,
    left: 0,
    right: 0,
    display: 'flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    backgroundColor: 'color-mix(in srgb, var(--colorNeutralBackground1) 88%, transparent)',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    transitionProperty: 'opacity, transform',
    transitionDuration: '280ms',
    transitionTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '1ms' },
    '@media (max-width: 720px)': { padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}` },
  },
  goalTag: {
    flexShrink: 0,
    fontSize: tokens.fontSizeBase100,
    textTransform: 'uppercase',
    letterSpacing: '0.07em',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
  },
  goalText: {
    minWidth: 0,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: '18px',
    color: tokens.colorNeutralForeground1,
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
  },
  // Compact, non-modal plan panel docked to the lower-left. Never more than a
  // corner of the stage — the graph stays visually dominant.
  planPanel: {
    position: 'absolute',
    zIndex: 7,
    left: tokens.spacingHorizontalM,
    bottom: tokens.spacingVerticalM,
    width: 'clamp(226px, 27%, 296px)',
    maxHeight: '42%',
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: '0 10px 28px rgba(39, 35, 32, 0.16)',
    overflow: 'hidden',
    transitionProperty: 'opacity, transform',
    transitionDuration: '300ms',
    transitionTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '1ms' },
    '@media (max-width: 720px)': {
      left: tokens.spacingHorizontalM,
      right: tokens.spacingHorizontalM,
      bottom: tokens.spacingVerticalM,
      width: 'auto',
      maxHeight: '46%',
    },
  },
  overlayHidden: {
    opacity: 0,
    transform: 'translateY(8px)',
    pointerEvents: 'none',
    visibility: 'hidden',
  },
  // Scrim behind the bounded artifact window — a soft focus cue, NOT a modal
  // trap. It is inert (aria-hidden, pointer-events none) so the graph, tabs and
  // replay stay reachable.
  artifactScrim: {
    position: 'absolute',
    inset: 0,
    zIndex: 8,
    backgroundColor: 'rgba(39, 35, 32, 0.28)',
    pointerEvents: 'none',
    animationName: { from: { opacity: 0 }, to: { opacity: 1 } },
    animationDuration: '360ms',
    animationTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
  },
  // Bounded artifact window floating over the settled graph. Authored to ~66%w
  // / 74%h on desktop, near-full width on mobile with a visible stage edge.
  artifactWindow: {
    position: 'absolute',
    zIndex: 9,
    top: '50%',
    left: '50%',
    transform: 'translate(-50%, -50%)',
    width: '66%',
    height: '74%',
    maxWidth: '760px',
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground2,
    boxShadow: '0 24px 60px rgba(39, 35, 32, 0.30)',
    overflow: 'hidden',
    animationName: {
      from: { opacity: 0, transform: 'translate(-50%, -46%) scale(0.985)' },
      to: { opacity: 1, transform: 'translate(-50%, -50%) scale(1)' },
    },
    animationDuration: '380ms',
    animationTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
    '@media (max-width: 900px)': { width: '78%', height: '76%' },
    '@media (max-width: 720px)': { width: '92%', height: '80%', maxWidth: 'none' },
  },
  artifactWindowBody: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    flex: 1,
    overflow: 'hidden',
  },
  planScroll: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalM,
    overflowY: 'auto',
    minHeight: 0,
  },
  composerLabel: {
    fontSize: tokens.fontSizeBase100,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
  },
  composerText: {
    marginTop: '4px',
    fontSize: tokens.fontSizeBase200,
    lineHeight: '19px',
    color: tokens.colorNeutralForeground1,
    minHeight: '38px',
    fontFamily: tokens.fontFamilyMonospace,
    wordBreak: 'break-word',
  },
  caret: {
    display: 'inline-block',
    width: '2px',
    height: '13px',
    marginLeft: '1px',
    backgroundColor: tokens.colorNeutralForeground1,
    verticalAlign: 'text-bottom',
    animationName: {
      '0%, 45%': { opacity: 1 },
      '50%, 95%': { opacity: 0 },
      '100%': { opacity: 1 },
    },
    animationDuration: '1s',
    animationIterationCount: 'infinite',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', opacity: 1 },
  },
  consoleBlock: {
    paddingTop: tokens.spacingVerticalS,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  cardTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: tokens.fontSizeBase100,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    marginBottom: tokens.spacingVerticalXS,
  },
  outcomeGoal: { fontSize: tokens.fontSizeBase200, lineHeight: '18px', marginBottom: '6px', color: tokens.colorNeutralForeground1 },
  metaLabel: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    marginTop: '5px',
  },
  list: { margin: '2px 0 0', paddingLeft: '16px', display: 'flex', flexDirection: 'column', gap: '2px' },
  listItem: { fontSize: tokens.fontSizeBase100, lineHeight: '15px', color: tokens.colorNeutralForeground2 },
  reviewLine: {
    marginTop: '6px',
    padding: '5px 8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    fontSize: tokens.fontSizeBase100,
    lineHeight: '15px',
    color: tokens.colorNeutralForeground2,
    display: 'flex',
    gap: '6px',
    alignItems: 'flex-start',
  },
  planItem: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    padding: '5px 0',
    ':not(:first-child)': { borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  },
  planIcon: { flexShrink: 0, marginTop: '1px', color: tokens.colorNeutralForeground3, fontSize: '14px', display: 'inline-flex' },
  planIconDone: { color: tokens.colorPaletteGreenForeground1 },
  planCopy: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  planTitle: { fontSize: tokens.fontSizeBase100, fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1, lineHeight: '15px' },
  planDetail: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, lineHeight: '14px' },
  planOwner: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3 },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    padding: 0,
    margin: '-1px',
    overflow: 'hidden',
    clip: 'rect(0, 0, 0, 0)',
    whiteSpace: 'nowrap',
    border: 0,
  },
});

const STEP_LABELS = ['Goal', 'Outcome spec', 'Work plan', 'Dispatch', 'Artifact'];

function phaseBadge(phase: Phase): { label: string; color: 'informative' | 'success' | 'warning' | 'subtle' } {
  switch (phase) {
    case 'running':
      return { label: 'Simulated playback', color: 'informative' };
    case 'paused':
      return { label: 'Paused', color: 'warning' };
    case 'complete':
      return { label: 'Complete', color: 'success' };
    default:
      return { label: 'Ready', color: 'subtle' };
  }
}

function stageAnnouncement(scenario: Scenario, stage: StageIndex, phase: Phase): string {
  if (phase === 'paused') return 'Simulated playback paused. Use Replay to run it again.';
  switch (stage) {
    case STAGE.TYPING:
      return 'Typing the goal into the composer.';
    case STAGE.OUTCOME:
      return 'Coordinator produced the outcome spec.';
    case STAGE.PLAN:
      return 'Work plan is ready.';
    case STAGE.DISPATCH:
      return 'Dispatching specialists on the run tree.';
    case STAGE.ARTIFACT:
      return `Illustrative output ready: ${scenario.artifactLabel}.`;
    default:
      return '';
  }
}

// ---------------------------------------------------------------------------
// Player
// ---------------------------------------------------------------------------
export function ScenarioTheater() {
  const styles = useStyles();
  const [state, dispatch] = useReducer(runReducer, undefined, initialState);
  const [inView, setInView] = useState(false);
  const [compact, setCompact] = useState(false);

  const scenario = useMemo(
    () => SCENARIOS.find((s) => s.id === state.activeId) ?? SCENARIOS[0],
    [state.activeId],
  );

  // Latest token/phase for the scheduler's double-guard.
  const tokenRef = useRef(state.token);
  const phaseRef = useRef(state.phase);
  tokenRef.current = state.token;
  phaseRef.current = state.phase;

  const rootRef = useRef<HTMLDivElement | null>(null);
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);

  // ---- Single run-token scheduler: owns EVERY timeout in the player. --------
  useEffect(() => {
    if (state.phase !== 'running') return;
    const goalLen = scenario.goal.length;
    const waveCount = maxDispatchWave(scenario);
    const scheduledToken = state.token;

    let delay = STAGE_MS;
    let action: RunAction = { type: 'ADVANCE' };

    if (state.stage === STAGE.TYPING) {
      if (state.typedLen < goalLen) {
        delay = TYPE_MS;
        action = { type: 'TYPE_TICK', goalLen };
      } else {
        delay = STAGE_MS;
        action = { type: 'ADVANCE' };
      }
    } else if (state.stage === STAGE.OUTCOME) {
      delay = OUTCOME_MS;
    } else if (state.stage === STAGE.PLAN) {
      delay = PLAN_MS;
    } else if (state.stage === STAGE.DISPATCH) {
      // One tick per dependency WAVE (not per specialist): every concurrent node
      // in the wave advances together, and a scenario never spends a tick on an
      // empty wave because waves are contiguous 1..waveCount.
      if (state.dispatchStep < waveCount) {
        delay = DISPATCH_MS;
        action = { type: 'DISPATCH_TICK' };
      } else {
        delay = STAGE_MS;
        action = { type: 'ADVANCE' };
      }
    } else {
      // ARTIFACT stage: settle into the terminal complete phase.
      delay = ARTIFACT_MS;
      action = { type: 'COMPLETE' };
    }

    // Use the global timer (not window.*) so a single owner schedules every
    // tick and fake-timer test harnesses can drive it deterministically.
    const id = setTimeout(() => {
      // Double-guard: ignore this tick if a newer token superseded it or the
      // run is no longer running (paused / out of view / unmounted / remounted).
      if (tokenRef.current !== scheduledToken) return;
      if (phaseRef.current !== 'running') return;
      dispatch(action);
    }, delay);

    return () => clearTimeout(id);
  }, [state.phase, state.stage, state.typedLen, state.dispatchStep, state.token, scenario]);

  // ---- Lazy autoplay + out-of-view pause via IntersectionObserver ----------
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof IntersectionObserver === 'undefined') {
      // No observer (older engines / test env): treat as in view so the run can play.
      setInView(true);
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        const entry = entries[0];
        setInView(Boolean(entry?.isIntersecting));
      },
      { rootMargin: '0px', threshold: 0.25 },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (inView && state.phase === 'idle') {
      dispatch({ type: 'PLAY_IF_IDLE' });
    } else if (!inView && state.phase === 'running') {
      dispatch({ type: 'PAUSE' });
    }
  }, [inView, state.phase]);

  // Track the compact breakpoint so the planning console can switch from a left
  // rail to a bottom sheet and fitView can reserve the right region of padding.
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mq = window.matchMedia('(max-width: 720px)');
    const sync = () => setCompact(mq.matches);
    sync();
    // addEventListener is the modern API; fall back for older Safari engines.
    if (typeof mq.addEventListener === 'function') {
      mq.addEventListener('change', sync);
      return () => mq.removeEventListener('change', sync);
    }
    mq.addListener(sync);
    return () => mq.removeListener(sync);
  }, []);

  // ---- Tab keyboard handling (roving tabindex) -----------------------------
  const selectByIndex = useCallback((index: number) => {
    const clamped = (index + SCENARIOS.length) % SCENARIOS.length;
    const target = SCENARIOS[clamped];
    dispatch({ type: 'SELECT', id: target.id });
    tabRefs.current[clamped]?.focus();
  }, []);

  const activeIndex = SCENARIOS.findIndex((s) => s.id === state.activeId);

  const onTabKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLDivElement>) => {
      switch (event.key) {
        case 'ArrowRight':
        case 'ArrowDown':
          event.preventDefault();
          selectByIndex(activeIndex + 1);
          break;
        case 'ArrowLeft':
        case 'ArrowUp':
          event.preventDefault();
          selectByIndex(activeIndex - 1);
          break;
        case 'Home':
          event.preventDefault();
          selectByIndex(0);
          break;
        case 'End':
          event.preventDefault();
          selectByIndex(SCENARIOS.length - 1);
          break;
        default:
          break;
      }
    },
    [activeIndex, selectByIndex],
  );

  const pauseFromInteraction = useCallback(() => {
    if (phaseRef.current === 'running') dispatch({ type: 'PAUSE' });
  }, []);

  // Dependency waves are derived once per scenario from its edges — the single
  // source of truth for which specialists run concurrently.
  const waves = useMemo(() => computeDispatchWaves(scenario), [scenario]);

  // Ordered agent node IDs matching the plan items array (plan[i] ↔ agentNodeIds[i]).
  const agentNodeIds = useMemo(
    () => scenario.nodes.filter((n) => n.role === 'agent').map((n) => n.id),
    [scenario],
  );

  // ---- Derived graph nodes/edges -------------------------------------------
  // Deterministic layered-DAG positions derived from roles + dependency waves.
  const layout = useMemo(() => layoutScenarioGraph(scenario), [scenario]);

  const nodes = useMemo<Node<WorkflowNodeData>[]>(
    () =>
      scenario.nodes.map((node) => {
        const runtime = nodeStatus(node, state.stage, state.dispatchStep, waves);
        const pos = layout.positions.get(node.id) ?? { x: 0, y: 0 };
        return {
          id: node.id,
          type: 'workflow',
          position: { x: pos.x, y: pos.y },
          data: {
            def: {
              key: node.role,
              label: node.label,
              roleDescription: node.agentRoleTitle ?? roleDescForRole(node.role),
              Icon: iconForRole(node.role),
            },
            // NOTE: startedAt is intentionally omitted so ElapsedTimer never
            // starts an interval; the scheduler is the sole owner of time.
            state: { status: runtime.status, message: runtime.message },
            agentName: node.agentName,
            agentRoleTitle: node.agentRoleTitle,
            modelId: node.modelId,
            executionId: node.id,
            executionPodName: node.pod,
            dir: 'LR',
          },
        };
      }),
    [scenario, state.stage, state.dispatchStep, waves, layout],
  );

  const edges = useMemo<Edge[]>(() => {
    const startedIds = new Set(
      scenario.nodes
        .filter((n) => nodeStatus(n, state.stage, state.dispatchStep, waves).status === 'started')
        .map((n) => n.id),
    );
    return scenario.edges.map(([id, source, target]) =>
      forwardEdge(id, source, target, startedIds.has(target)),
    );
  }, [scenario, state.stage, state.dispatchStep, waves]);

  const badge = phaseBadge(state.phase);
  const typed = scenario.goal.slice(0, state.typedLen);
  const showCaret = state.stage === STAGE.TYPING;
  const panelId = 'aw-theater-panel';
  // At the final beat the artifact floats in a bounded window over the graph,
  // which stays mounted and dims back as settled context — never a full-stage
  // takeover and never a modal focus trap.
  const showArtifact = state.stage >= STAGE.ARTIFACT;
  const showPlanPanel = state.stage >= STAGE.OUTCOME && !showArtifact;
  // The graph is framed as a horizontal band: the goal strip caps the top and
  // the plan panel + hint anchor the bottom, so fitView keeps the run tree in
  // the centre of the stage rather than letting it drift into a corner.
  const fitPadding = compact
    ? ({ top: '13%', right: '5%', bottom: '42%', left: '5%' } as const)
    : ({ top: '11%', right: '5%', bottom: '30%', left: '6%' } as const);

  return (
    <FluentProvider theme={agentweaverLightTheme}>
      <div className={styles.root} ref={rootRef}>
        <p className={styles.disclaimer}>
          <CircleRegular className={styles.disclaimerMark} aria-hidden="true" fontSize={14} />
          <span>{DISCLAIMER}</span>
        </p>

        <div
          role="tablist"
          aria-label="Scenario theater"
          aria-orientation="horizontal"
          className={styles.tablist}
          onKeyDown={onTabKeyDown}
        >
          {SCENARIOS.map((s, index) => {
            const selected = s.id === state.activeId;
            return (
              <button
                key={s.id}
                ref={(el) => {
                  tabRefs.current[index] = el;
                }}
                type="button"
                role="tab"
                id={`aw-tab-${s.id}`}
                aria-selected={selected}
                aria-controls={panelId}
                tabIndex={selected ? 0 : -1}
                className={mergeClasses(styles.tab, selected && styles.tabSelected)}
                onClick={() => dispatch({ type: 'SELECT', id: s.id })}
              >
                <span className={styles.tabLabel}>{s.tabLabel}</span>
                <span className={styles.tabHint}>{s.tabHint}</span>
              </button>
            );
          })}
        </div>

        <div
          className={styles.panel}
          role="tabpanel"
          id={panelId}
          aria-labelledby={`aw-tab-${scenario.id}`}
          tabIndex={0}
        >
          <div className={styles.header}>
            <div className={styles.headingGroup}>
              <div className={styles.titleRow}>
                <span className={styles.title}>{scenario.title}</span>
                <Badge appearance="tint" color={badge.color}>
                  {badge.label}
                </Badge>
              </div>
              <Text className={styles.subtitle}>{scenario.subtitle}</Text>
            </div>
            <div className={styles.headerMeta}>
              <Badge appearance="outline">{scenario.nodes.length} nodes</Badge>
              <Badge appearance="outline">{scenario.credits}</Badge>
              <Button
                appearance="secondary"
                size="small"
                icon={<ArrowClockwiseRegular />}
                onClick={() => dispatch({ type: 'REPLAY' })}
              >
                Replay
              </Button>
            </div>
          </div>

          <div className={styles.stepper} aria-hidden="true">
            {STEP_LABELS.map((label, index) => {
              const done =
                state.stage > index ||
                (index === STAGE.ARTIFACT && state.stage === STAGE.ARTIFACT && state.phase === 'complete');
              const active = state.stage === index;
              return (
                <div className={styles.step} key={label}>
                  {index > 0 && <span className={styles.stepArrow}>›</span>}
                  <span
                    className={mergeClasses(
                      styles.stepDot,
                      active && styles.stepDotActive,
                      done && styles.stepDotDone,
                    )}
                  >
                    {done ? <CheckmarkCircleFilled /> : <CircleRegular />}
                  </span>
                  <span className={mergeClasses(styles.stepLabel, active && styles.stepLabelActive)}>{label}</span>
                </div>
              );
            })}
          </div>

          <div className={styles.stage} aria-label="Run surface">
            <section
              className={mergeClasses(styles.graphLayer, showArtifact && styles.graphSettled)}
              aria-label="Run tree"
              aria-hidden={showArtifact}
              onPointerDownCapture={pauseFromInteraction}
              onWheelCapture={pauseFromInteraction}
            >
              <ReactFlow
                key={`${scenario.id}:${compact ? 'c' : 'w'}`}
                className={styles.graph}
                nodes={nodes}
                edges={edges}
                nodeTypes={workflowNodeTypes}
                edgeTypes={workflowEdgeTypes}
                fitView
                fitViewOptions={{ padding: fitPadding, maxZoom: 1.15 }}
                minZoom={0.3}
                maxZoom={1.5}
                nodesDraggable={false}
                nodesConnectable={false}
                panOnDrag
                panOnScroll
                zoomOnPinch
                zoomOnScroll={false}
                proOptions={{ hideAttribution: true }}
              >
                <Panel position="top-right">
                  <GraphControls orderedNodeIds={scenario.nodes.map((n) => n.id)} />
                </Panel>
                <MiniMap
                  pannable
                  zoomable
                  nodeStrokeWidth={0}
                  nodeColor={(node) =>
                    (node.data as WorkflowNodeData).state.status === 'completed'
                      ? '#16a149'
                      : (node.data as WorkflowNodeData).state.status === 'started'
                        ? '#8a4b01'
                        : '#b8afa8'
                  }
                  style={{
                    width: 92,
                    height: 58,
                    border: '1px solid var(--colorNeutralStroke2)',
                    borderRadius: 8,
                  }}
                />
              </ReactFlow>
              <Text className={styles.graphHint}>
                Drag to pan · zoom with the controls. Interacting pauses playback.
              </Text>
            </section>

            <div
              className={mergeClasses(styles.goalStrip, showArtifact && styles.overlayHidden)}
              aria-hidden={showArtifact}
            >
              <span className={styles.goalTag}>Goal</span>
              <span className={styles.goalText}>
                {typed}
                {showCaret && <span className={styles.caret} aria-hidden="true" />}
              </span>
            </div>

            <aside
              className={mergeClasses(styles.planPanel, !showPlanPanel && styles.overlayHidden)}
              aria-label="Run plan"
              aria-hidden={!showPlanPanel}
            >
              <div className={styles.planScroll}>
                {state.stage >= STAGE.OUTCOME && state.stage < STAGE.PLAN && (
                  <div>
                    <div className={styles.cardTitle}>Outcome spec</div>
                    <div className={styles.outcomeGoal}>{scenario.outcome.goal}</div>
                    <div className={styles.metaLabel}>Scope</div>
                    <ul className={styles.list}>
                      {scenario.outcome.scope.map((item) => (
                        <li className={styles.listItem} key={item}>{item}</li>
                      ))}
                    </ul>
                    {scenario.outcome.review.map((item) => (
                      <div className={styles.reviewLine} key={item}>
                        <LockClosedRegular aria-hidden="true" fontSize={13} />
                        <span>{item}</span>
                      </div>
                    ))}
                  </div>
                )}

                {state.stage >= STAGE.PLAN && (
                  <div>
                    <div className={styles.cardTitle}>Work plan</div>
                    {scenario.plan.map((item, index) => {
                      const wave = planItemWave(waves, agentNodeIds, index);
                      const done =
                        state.stage > STAGE.DISPATCH ||
                        (state.stage === STAGE.DISPATCH && wave <= state.dispatchStep);
                      return (
                        <div className={styles.planItem} key={item.id}>
                          <span className={mergeClasses(styles.planIcon, done && styles.planIconDone)}>
                            {done ? <CheckmarkCircleFilled /> : <CircleRegular />}
                          </span>
                          <span className={styles.planCopy}>
                            <span className={styles.planTitle}>{item.title}</span>
                            <span className={styles.planOwner}>Owner · {item.owner}</span>
                          </span>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </aside>

            {showArtifact && (
              <>
                <div className={styles.artifactScrim} aria-hidden="true" />
                <div className={styles.artifactWindow} aria-label="Run artifact">
                  <div className={styles.artifactWindowBody}>
                    <ArtifactFrame label={scenario.artifactLabel} caption={scenario.artifactCaption}>
                      <scenario.Artifact />
                    </ArtifactFrame>
                  </div>
                </div>
              </>
            )}
          </div>
        </div>

        <div className={styles.srOnly} role="status" aria-live="polite">
          {stageAnnouncement(scenario, state.stage, state.phase)}
        </div>
      </div>
    </FluentProvider>
  );
}

export function mountLandingWorkflowDemo(element: HTMLElement) {
  const root = createRoot(element);
  root.render(<ScenarioTheater />);
  return () => root.unmount();
}
