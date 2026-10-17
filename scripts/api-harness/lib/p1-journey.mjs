import { runP1SurfaceJourney } from '../../harness-shared/p1-surface-runner.mjs';

/** Bind the shared P1 journey runner to the Gateway API surface. */
export function runP1ApiJourney(input, dependencies) {
  return runP1SurfaceJourney('api', input, dependencies);
}
