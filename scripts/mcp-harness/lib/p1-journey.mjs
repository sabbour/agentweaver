import { runP1SurfaceJourney } from '../../harness-shared/p1-surface-runner.mjs';

export function runP1McpJourney(input, dependencies) {
  return runP1SurfaceJourney('mcp', input, dependencies);
}
