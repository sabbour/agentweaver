import { runP1SurfaceJourney } from '../../harness-shared/p1-surface-runner.mjs';

export function runP1UiJourney(input, dependencies) {
  return runP1SurfaceJourney('ui', input, dependencies);
}
