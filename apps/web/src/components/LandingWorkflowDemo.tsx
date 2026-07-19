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
import { ReactFlow, type Edge, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { createRoot } from 'react-dom/client';
import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { agentweaverLightTheme } from '../theme';
import { ArtifactFrame } from './artifacts/ArtifactFrame';
import { SCENARIOS } from './landing/scenarios';
import { STAGE, type Scenario, type ScenarioNode, type StageIndex } from './landing/types';
import { computeDispatchWaves, maxDispatchWave, planItemWave } from './landing/waves';
import { buildScenarioGraph } from './landing/graph';
import { runAutoScroll } from './landing/autoScroll';
import {
  iconForRole,
  roleDescForRole,
  workflowEdgeTypes,
  workflowNodeTypes,
  type StepStatus,
  type WorkflowNodeData,
} from './WorkflowGraphPanel';

// ---------------------------------------------------------------------------
// Timing. Every value below is consumed only by the single run-token scheduler,
// EXCEPT the two artifact-scroll values, which drive a separate rAF animator.
// ---------------------------------------------------------------------------
const TYPE_STEP = 2; // characters revealed per typing tick
const TYPE_MS = 34;
const STAGE_MS = 620;
const OUTCOME_MS = 1050;
const PLAN_MS = 950;
const DISPATCH_MS = 640;
/** How long the artifact holds (auto-scrolling) before the carousel advances. */
export const ARTIFACT_HOLD_MS = 5200;
/** Quiet beat before simulated scrolling begins, so the top of the result reads first. */
const ARTIFACT_SCROLL_START_MS = 500;
/** Travel time for the simulated scroll, comfortably inside ARTIFACT_HOLD_MS. */
const ARTIFACT_SCROLL_DURATION_MS = 3600;

// Node positions come from the SHARED production staircase layout via
// buildScenarioGraph (utils/dagLayout.layoutDagStaircase + routeGridEdges).
// Nothing here reimplements a landing-only graph algorithm.

// ---------------------------------------------------------------------------
// Run-token state machine
// ---------------------------------------------------------------------------
export type Phase = 'idle' | 'running';

export interface RunState {
  activeId: string;
  stage: StageIndex;
  typedLen: number;
  dispatchStep: number;
  phase: Phase;
  /** Monotonic token; every restart/selection/advance bumps it so any in-flight
   *  timeout that still fires is ignored by the double-guard. */
  token: number;
}

export type RunAction =
  | { type: 'SELECT'; id: string }
  | { type: 'REPLAY' }
  | { type: 'ADVANCE_SCENARIO' }
  | { type: 'PLAY_IF_IDLE' }
  | { type: 'TYPE_TICK'; goalLen: number }
  | { type: 'ADVANCE' }
  | { type: 'DISPATCH_TICK' };

export function initialRunState(): RunState {
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

/** Id of the scenario after `id`, wrapping from the last back to the first. */
export function nextScenarioId(id: string): string {
  const index = SCENARIOS.findIndex((s) => s.id === id);
  const next = (index + 1 + SCENARIOS.length) % SCENARIOS.length;
  return SCENARIOS[next].id;
}

export function runReducer(state: RunState, action: RunAction): RunState {
  switch (action.type) {
    case 'SELECT':
      // Selecting a tab ALWAYS starts that scenario immediately (running), even
      // if it is the current one (re-runs it). There is no idle/paused waiting.
      return freshRun(action.id, 'running', state.token + 1);
    case 'REPLAY':
      return freshRun(state.activeId, 'running', state.token + 1);
    case 'ADVANCE_SCENARIO':
      // Carousel step: advance to the next scenario (wrapping 8 → 1) and start it.
      return freshRun(nextScenarioId(state.activeId), 'running', state.token + 1);
    case 'PLAY_IF_IDLE':
      return state.phase === 'idle' ? { ...state, phase: 'running', token: state.token + 1 } : state;
    case 'TYPE_TICK':
      return { ...state, typedLen: Math.min(action.goalLen, state.typedLen + TYPE_STEP) };
    case 'ADVANCE':
      return { ...state, stage: Math.min(STAGE.ARTIFACT, state.stage + 1) as StageIndex };
    case 'DISPATCH_TICK':
      return { ...state, dispatchStep: state.dispatchStep + 1 };
    default:
      return state;
  }
}

// ---------------------------------------------------------------------------
// Node status derivation (pure, no timers)
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
  // Every specialist in the same wave transitions together. The scheduler steps
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
    display: 'flex',
    flexDirection: 'column',
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
  // The full-height run body. During Goal/Outcome/Plan/Dispatch it is a desktop
  // split (OutcomeSpec + Work Plan on the left, the stepped graph on the right).
  // At the artifact beat the whole body is replaced by the result. On mobile the
  // panes stack (left first) with no horizontal overflow.
  body: {
    display: 'flex',
    minWidth: 0,
    height: 'clamp(468px, 64vh, 648px)',
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 720px)': {
      flexDirection: 'column',
      height: 'auto',
    },
  },
  leftPane: {
    display: 'flex',
    flexDirection: 'column',
    flexShrink: 0,
    minWidth: 0,
    width: 'clamp(300px, 34%, 400px)',
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 720px)': {
      width: 'auto',
      maxHeight: '46%',
      borderRight: 'none',
      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    },
  },
  leftScroll: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalL,
    overflowY: 'auto',
    minHeight: 0,
    flex: 1,
    '@media (max-width: 720px)': { padding: tokens.spacingHorizontalM },
  },
  graphPane: {
    position: 'relative',
    flex: 1,
    minWidth: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 720px)': { minHeight: '324px', height: '324px' },
  },
  graph: {
    width: '100%',
    height: '100%',
    // Non-interactive canvas: never present a pan/grab affordance.
    '& .react-flow__pane': { cursor: 'default' },
  },
  // Full-body artifact takeover, replaces BOTH panes at the final beat.
  takeover: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minWidth: 0,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    animationName: {
      from: { opacity: 0, transform: 'translateY(6px)' },
      to: { opacity: 1, transform: 'translateY(0)' },
    },
    animationDuration: '320ms',
    animationTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
    '@media (max-width: 720px)': { padding: tokens.spacingHorizontalM, minHeight: '520px' },
  },
  // The goal composer that caps the left pane.
  goalBlock: {
    display: 'flex',
    flexDirection: 'column',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
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
  reveal: {
    animationName: { from: { opacity: 0, transform: 'translateY(6px)' }, to: { opacity: 1, transform: 'translateY(0)' } },
    animationDuration: '260ms',
    animationTimingFunction: 'cubic-bezier(0.16, 1, 0.3, 1)',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
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

function stageAnnouncement(scenario: Scenario, stage: StageIndex): string {
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
      return `${scenario.artifactLabel} ready.`;
    default:
      return '';
  }
}

// ---------------------------------------------------------------------------
// Player
// ---------------------------------------------------------------------------
export function ScenarioTheater() {
  const styles = useStyles();
  const [state, dispatch] = useReducer(runReducer, undefined, initialRunState);
  const [inView, setInView] = useState(false);
  const [reducedMotion, setReducedMotion] = useState(false);

  const scenario = useMemo(
    () => SCENARIOS.find((s) => s.id === state.activeId) ?? SCENARIOS[0],
    [state.activeId],
  );

  // Latest token/phase/visibility for the scheduler's double-guard.
  const tokenRef = useRef(state.token);
  const phaseRef = useRef(state.phase);
  const inViewRef = useRef(inView);
  tokenRef.current = state.token;
  phaseRef.current = state.phase;
  inViewRef.current = inView;

  const rootRef = useRef<HTMLDivElement | null>(null);
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const artifactScrollRef = useRef<HTMLDivElement | null>(null);

  // ---- Single run-token scheduler: owns EVERY timeout in the player. --------
  // Gated on `inView`: leaving the viewport silently suspends scheduling (no
  // visible "paused" state) and re-entry resumes the SAME beat because the effect
  // re-runs from the unchanged run state.
  useEffect(() => {
    if (state.phase !== 'running') return;
    if (!inView) return;
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
      // ARTIFACT stage: hold the result long enough to inspect / auto-scroll,
      // then advance the carousel to the next scenario (wrapping 8 → 1).
      delay = ARTIFACT_HOLD_MS;
      action = { type: 'ADVANCE_SCENARIO' };
    }

    // Use the global timer (not window.*) so a single owner schedules every
    // tick and fake-timer test harnesses can drive it deterministically.
    const id = setTimeout(() => {
      // Double-guard: ignore this tick if a newer token superseded it, the run
      // is no longer running, or it drifted out of view since it was scheduled.
      if (tokenRef.current !== scheduledToken) return;
      if (phaseRef.current !== 'running') return;
      if (!inViewRef.current) return;
      dispatch(action);
    }, delay);

    return () => clearTimeout(id);
  }, [state.phase, state.stage, state.typedLen, state.dispatchStep, state.token, scenario, inView]);

  // ---- Lazy autoplay + out-of-view IntersectionObserver --------------------
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

  // First time it scrolls into view, kick the idle run into motion. Leaving the
  // viewport does NOT dispatch anything. Scheduling simply suspends (see above).
  useEffect(() => {
    if (inView && state.phase === 'idle') {
      dispatch({ type: 'PLAY_IF_IDLE' });
    }
  }, [inView, state.phase]);

  // Track prefers-reduced-motion so the artifact takeover uses a static result.
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
    const sync = () => setReducedMotion(mq.matches);
    sync();
    if (typeof mq.addEventListener === 'function') {
      mq.addEventListener('change', sync);
      return () => mq.removeEventListener('change', sync);
    }
    mq.addListener(sync);
    return () => mq.removeListener(sync);
  }, []);

  const showArtifact = state.stage >= STAGE.ARTIFACT;

  // ---- Simulated artifact scroll (separate rAF owner, fully cancellable) ----
  // Runs only while the artifact is on-screen and in view. Robustly cancelled on
  // scenario change, replay, out-of-view, reduced-motion and unmount via the
  // effect's dependency list + cleanup. Reduced motion → instant static result.
  useEffect(() => {
    if (!showArtifact || !inView) return;
    const el = artifactScrollRef.current;
    if (!el) return;
    if (reducedMotion) {
      el.scrollTop = 0;
      return;
    }
    const handle = runAutoScroll(el, {
      durationMs: ARTIFACT_SCROLL_DURATION_MS,
      startDelayMs: ARTIFACT_SCROLL_START_MS,
    });
    return () => handle.cancel();
  }, [showArtifact, inView, reducedMotion, state.activeId, state.token]);

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

  // Dependency waves are derived once per scenario from its edges. The single
  // source of truth for which specialists run concurrently.
  const waves = useMemo(() => computeDispatchWaves(scenario), [scenario]);

  // Ordered agent node IDs matching the plan items array (plan[i] ↔ agentNodeIds[i]).
  const agentNodeIds = useMemo(
    () => scenario.nodes.filter((n) => n.role === 'agent').map((n) => n.id),
    [scenario],
  );

  // ---- Derived graph nodes/edges (SHARED production staircase layout) -------
  // Deterministic geometry from utils/dagLayout via buildScenarioGraph. Positions
  // + routed edges are static per scenario; only runtime status/animation change.
  const graph = useMemo(() => buildScenarioGraph(scenario), [scenario]);

  const nodes = useMemo<Node<WorkflowNodeData>[]>(
    () =>
      scenario.nodes.map((node) => {
        const runtime = nodeStatus(node, state.stage, state.dispatchStep, waves);
        const pos = graph.positions.get(node.id) ?? { x: 0, y: 0 };
        const hint = graph.sizeHints[node.id];
        return {
          id: node.id,
          type: 'workflow',
          position: { x: pos.x, y: pos.y },
          initialWidth: hint?.width,
          initialHeight: hint?.height,
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
            // GRID renders all eight handles so the shared routeGridEdges handle
            // selection (left/right/top/bottom bows) always has a target.
            dir: 'GRID',
          },
        };
      }),
    [scenario, state.stage, state.dispatchStep, waves, graph],
  );

  const edges = useMemo<Edge[]>(() => {
    const startedIds = new Set(
      scenario.nodes
        .filter((n) => nodeStatus(n, state.stage, state.dispatchStep, waves).status === 'started')
        .map((n) => n.id),
    );
    // Reuse the SHARED routed edges (handles + stepped flowDirection); only flip
    // the animated flow to the currently-executing targets.
    return graph.routedEdges.map((edge) => ({
      ...edge,
      animated: startedIds.has(edge.target),
    }));
  }, [graph, scenario, state.stage, state.dispatchStep, waves]);

  const typed = scenario.goal.slice(0, state.typedLen);
  const showCaret = state.stage === STAGE.TYPING;
  const panelId = 'aw-theater-panel';
  const showOutcome = state.stage >= STAGE.OUTCOME;
  const showPlan = state.stage >= STAGE.PLAN;

  return (
    <FluentProvider theme={agentweaverLightTheme}>
      <div className={styles.root} ref={rootRef}>
        <div
          role="tablist"
          aria-label="Example runs"
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

          <div className={styles.body} aria-label="Run surface">
            {showArtifact ? (
              <div className={styles.takeover} aria-label="Run artifact">
                <ArtifactFrame
                  label={scenario.artifactLabel}
                  caption={scenario.artifactCaption}
                  fill
                  scrollRef={artifactScrollRef}
                >
                  <scenario.Artifact />
                </ArtifactFrame>
              </div>
            ) : (
              <>
                <div className={styles.leftPane}>
                  <div className={styles.leftScroll}>
                    <div className={styles.goalBlock}>
                      <span className={styles.composerLabel}>Goal</span>
                      <div className={styles.composerText}>
                        {typed}
                        {showCaret && <span className={styles.caret} aria-hidden="true" />}
                      </div>
                    </div>

                    {showOutcome && (
                      <div className={styles.reveal}>
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

                    {showPlan && (
                      <div className={styles.reveal}>
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
                </div>

                <div className={styles.graphPane} aria-label="Run tree">
                  <ReactFlow
                    key={scenario.id}
                    className={styles.graph}
                    nodes={nodes}
                    edges={edges}
                    nodeTypes={workflowNodeTypes}
                    edgeTypes={workflowEdgeTypes}
                    fitView
                    fitViewOptions={{ padding: 0.14, maxZoom: 1.15 }}
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
              </>
            )}
          </div>
        </div>

        <div className={styles.srOnly} role="status" aria-live="polite">
          {stageAnnouncement(scenario, state.stage)}
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
