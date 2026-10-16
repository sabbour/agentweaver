import { runP1SurfaceJourney } from '../../harness-shared/p1-surface-runner.mjs';

/** Bind the shared P1 journey runner to the browser-observed UI surface. */
export function runP1UiJourney(input, dependencies) {
  return runP1SurfaceJourney('ui', input, dependencies);
}
