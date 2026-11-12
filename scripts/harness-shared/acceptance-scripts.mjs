import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const readOnlyGatewayCase = fileURLToPath(
  new URL('../acceptance/api/read-only-gateway.mjs', import.meta.url),
);

export async function runP1AcceptanceScripts(
  options,
  env = process.env,
  io = console,
  spawnProcess = spawn,
) {
  if (!options.input || !options.output) {
    io.error?.('The API case requires --input <case.json> and --output <new-evidence-directory>.');
    return 2;
  }
  if (options.interactive) {
    io.error?.('This API case does not open a browser. Sign in with `agentweaver auth login` before you run it.');
    return 2;
  }
  if (options.nativeProbeEnvelope || options.dispatchRecoveryEnvelope
    || options.identityModule || options.pageModule) {
    io.error?.('Legacy runner modules and proof envelopes are not supported by the API case scripts.');
    return 2;
  }

  const args = [readOnlyGatewayCase, '--input', options.input, '--output', options.output];
  if (options.dryRun) args.push('--dry-run');
  return new Promise(resolveExit => {
    let child;
    try {
      child = spawnProcess(process.execPath, args, {
        env,
        shell: false,
        stdio: 'inherit',
        windowsHide: true,
      });
    } catch {
      io.error?.('The API acceptance case script could not be started.');
      resolveExit(1);
      return;
    }

    child.once('error', () => {
      io.error?.('The API acceptance case script could not be started.');
      resolveExit(1);
    });
    child.once('exit', code => resolveExit(Number.isInteger(code) ? code : 1));
  });
}
