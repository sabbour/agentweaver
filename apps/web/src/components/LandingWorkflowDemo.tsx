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

const DISCLAIMER =
  'Illustrative simulated runs. Outputs are authored examples, not professional advice, autonomous publishing, or production actions.';

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
function specialistCount(scenario: Scenario): number {
  return scenario.nodes.filter((n) => n.dispatchOrder != null).length;
}

function nodeStatus(
  node: ScenarioNode,
  stage: StageIndex,
  dispatchStep: number,
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
  // Specialist agents dispatch during the DISPATCH stage in dispatchOrder.
  const order = node.dispatchOrder ?? 99;
  // Show the authored duration as subtext on completed agent nodes (replaces generic "Finished").
  if (stage >= STAGE.ARTIFACT) return { status: 'completed', statusLabel: 'Ready', message: node.duration };
  if (stage < STAGE.DISPATCH) return { status: 'pending', statusLabel: 'Queued' };
  if (order <= dispatchStep) return { status: 'completed', statusLabel: 'Ready', message: node.duration };
  if (order === dispatchStep + 1) return { status: 'started', statusLabel: 'Running', message: 'Executing in an isolated sandbox' };
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
  body: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 360px) minmax(0, 1fr)',
    minWidth: 0,
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' },
  },
  narrative: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalL,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    minWidth: 0,
    '@media (max-width: 900px)': {
      borderRight: 'none',
      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    },
  },
  composer: {
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    padding: tokens.spacingHorizontalM,
    minWidth: 0,
  },
  composerLabel: {
    fontSize: tokens.fontSizeBase100,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
  },
  composerText: {
    marginTop: '6px',
    fontSize: tokens.fontSizeBase300,
    lineHeight: '22px',
    color: tokens.colorNeutralForeground1,
    minHeight: '44px',
    fontFamily: tokens.fontFamilyMonospace,
  },
  caret: {
    display: 'inline-block',
    width: '2px',
    height: '15px',
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
  },
  card: {
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    padding: tokens.spacingHorizontalM,
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
    marginBottom: tokens.spacingVerticalS,
  },
  outcomeGoal: { fontSize: tokens.fontSizeBase300, lineHeight: '20px', marginBottom: tokens.spacingVerticalS },
  metaLabel: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    marginTop: tokens.spacingVerticalXS,
  },
  list: { margin: '2px 0 0', paddingLeft: '18px', display: 'flex', flexDirection: 'column', gap: '3px' },
  listItem: { fontSize: tokens.fontSizeBase200, lineHeight: '17px', color: tokens.colorNeutralForeground2 },
  reviewLine: {
    marginTop: tokens.spacingVerticalS,
    padding: '6px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    display: 'flex',
    gap: '6px',
    alignItems: 'flex-start',
  },
  planItem: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    padding: '7px 0',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  planIcon: { flexShrink: 0, marginTop: '1px', color: tokens.colorNeutralForeground3 },
  planIconDone: { color: tokens.colorPaletteGreenForeground1 },
  planCopy: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  planTitle: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1 },
  planDetail: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, lineHeight: '15px' },
  planOwner: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3 },
  graphWrap: {
    position: 'relative',
    minWidth: 0,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  graphHint: {
    position: 'absolute',
    zIndex: 8,
    left: tokens.spacingHorizontalL,
    top: tokens.spacingVerticalM,
    maxWidth: '360px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    pointerEvents: 'none',
  },
  graph: {
    width: '100%',
    height: '480px',
    '@media (max-width: 900px)': { height: '420px' },
    '@media (max-width: 480px)': { height: '360px' },
    '& .react-flow__pane': { cursor: 'grab' },
    '& .react-flow__pane:active': { cursor: 'grabbing' },
  },
  artifactRegion: {
    padding: tokens.spacingHorizontalL,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  artifactPending: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '120px',
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    textAlign: 'center',
    padding: tokens.spacingHorizontalL,
  },
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
    const specialists = specialistCount(scenario);
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
      if (state.dispatchStep < specialists) {
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

  // ---- Derived graph nodes/edges -------------------------------------------
  const nodes = useMemo<Node<WorkflowNodeData>[]>(
    () =>
      scenario.nodes.map((node) => {
        const runtime = nodeStatus(node, state.stage, state.dispatchStep);
        return {
          id: node.id,
          type: 'workflow',
          position: { x: node.col * 210, y: node.row * 108 },
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
    [scenario, state.stage, state.dispatchStep],
  );

  const edges = useMemo<Edge[]>(() => {
    const startedIds = new Set(
      scenario.nodes
        .filter((n) => nodeStatus(n, state.stage, state.dispatchStep).status === 'started')
        .map((n) => n.id),
    );
    return scenario.edges.map(([id, source, target]) =>
      forwardEdge(id, source, target, startedIds.has(target)),
    );
  }, [scenario, state.stage, state.dispatchStep]);

  const badge = phaseBadge(state.phase);
  const typed = scenario.goal.slice(0, state.typedLen);
  const showCaret = state.stage === STAGE.TYPING;
  const panelId = 'aw-theater-panel';

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

          <div className={styles.body}>
            <aside className={styles.narrative} aria-label="Run narrative">
              <div className={styles.composer}>
                <div className={styles.composerLabel}>Goal</div>
                <div className={styles.composerText}>
                  {typed}
                  {showCaret && <span className={styles.caret} aria-hidden="true" />}
                </div>
              </div>

              {state.stage >= STAGE.OUTCOME && (
                <div className={styles.card}>
                  <div className={styles.cardTitle}>Outcome spec</div>
                  <div className={styles.outcomeGoal}>{scenario.outcome.goal}</div>
                  <div className={styles.metaLabel}>Scope</div>
                  <ul className={styles.list}>
                    {scenario.outcome.scope.map((item) => (
                      <li className={styles.listItem} key={item}>{item}</li>
                    ))}
                  </ul>
                  <div className={styles.metaLabel}>Assumptions</div>
                  <ul className={styles.list}>
                    {scenario.outcome.assumptions.map((item) => (
                      <li className={styles.listItem} key={item}>{item}</li>
                    ))}
                  </ul>
                  {scenario.outcome.review.map((item) => (
                    <div className={styles.reviewLine} key={item}>
                      <span aria-hidden="true">🔒</span>
                      <span>{item}</span>
                    </div>
                  ))}
                </div>
              )}

              {state.stage >= STAGE.PLAN && (
                <div className={styles.card}>
                  <div className={styles.cardTitle}>Work plan</div>
                  {scenario.plan.map((item, index) => {
                    const done =
                      state.stage > STAGE.DISPATCH ||
                      (state.stage === STAGE.DISPATCH && state.dispatchStep > index);
                    return (
                      <div className={styles.planItem} key={item.id}>
                        <span className={mergeClasses(styles.planIcon, done && styles.planIconDone)}>
                          {done ? <CheckmarkCircleFilled /> : <CircleRegular />}
                        </span>
                        <span className={styles.planCopy}>
                          <span className={styles.planTitle}>{item.title}</span>
                          <span className={styles.planDetail}>{item.detail}</span>
                          <span className={styles.planOwner}>Owner · {item.owner}</span>
                        </span>
                      </div>
                    );
                  })}
                </div>
              )}
            </aside>

            <section
              className={styles.graphWrap}
              aria-label="Run tree"
              onPointerDownCapture={pauseFromInteraction}
              onWheelCapture={pauseFromInteraction}
            >
              <Text className={styles.graphHint}>
                Drag to pan · use the controls to zoom. Interacting pauses the simulated playback.
              </Text>
              <ReactFlow
                key={scenario.id}
                className={styles.graph}
                nodes={nodes}
                edges={edges}
                nodeTypes={workflowNodeTypes}
                edgeTypes={workflowEdgeTypes}
                fitView
                fitViewOptions={{ padding: 0.18 }}
                minZoom={0.3}
                maxZoom={1.6}
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
                    width: 108,
                    height: 72,
                    border: '1px solid var(--colorNeutralStroke2)',
                    borderRadius: 8,
                  }}
                />
              </ReactFlow>
            </section>
          </div>

          <div className={styles.artifactRegion}>
            {state.stage >= STAGE.ARTIFACT ? (
              <ArtifactFrame label={scenario.artifactLabel} caption={scenario.artifactCaption}>
                <scenario.Artifact />
              </ArtifactFrame>
            ) : (
              <div className={styles.artifactPending}>
                The illustrative output appears here once the run reaches its final stage.
              </div>
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
