import { runP1SurfaceJourney } from '../../harness-shared/p1-surface-runner.mjs';

export function runP1ApiJourney(input, dependencies) {
  return runP1SurfaceJourney('api', input, dependencies);
}
