import { runP1SurfaceJourney } from '../../harness-shared/p1-surface-runner.mjs';

/** Bind the shared P1 journey runner to the live MCP tool surface. */
export function runP1McpJourney(input, dependencies) {
  return runP1SurfaceJourney('mcp', input, dependencies);
}
