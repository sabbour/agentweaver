import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';

export async function createStdioTransport({ command, args = [], target = 'http://localhost', allowProd = false, iUnderstandProd = false, env }) {
  assertTargetAllowed(target, { allowProd, confirmProduction: iUnderstandProd });
  if (typeof command !== 'string' || !command.trim()) throw new Error('A stdio server command is required');
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  return new StdioClientTransport({ command, args, env });
}
