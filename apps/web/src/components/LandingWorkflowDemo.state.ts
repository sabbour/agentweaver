import { SCENARIOS } from './landing/scenarios';
import { STAGE, type StageIndex } from './landing/types';

/** Characters revealed per typing tick in the run-token scheduler. */
const TYPE_STEP = 2;

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
