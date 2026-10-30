import { spawn } from 'node:child_process';
import { constants } from 'node:fs';
import { access, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

export async function runP1DotnetCli(options, env = process.env, io = console, spawnProcess = spawn) {
  const executable = env.AGENTWEAVER_CLI_PATH;
  if (typeof executable !== 'string' || executable.length === 0) {
    io.error?.('API acceptance requires the exact built apphost path in AGENTWEAVER_CLI_PATH.');
    return 2;
  }
  try {
    const info = await stat(executable);
    if (!info.isFile()) throw new Error();
    await access(executable, constants.X_OK);
  } catch {
    io.error?.('AGENTWEAVER_CLI_PATH must name an existing executable apphost.');
    return 2;
  }

  const args = ['test', '--input', options.input, '--output', options.output];
  if (options.nativeProbeEnvelope) args.push('--native-probe-envelope', options.nativeProbeEnvelope);
  if (options.dispatchRecoveryEnvelope) args.push('--dispatch-recovery-envelope', options.dispatchRecoveryEnvelope);
  if (options.dryRun) args.push('--dry-run');
  if (options.interactive) args.push('--interactive');
  const childEnv = { ...env };
  delete childEnv.AGENTWEAVER_P1_SETUP_TOKEN;
  let exitCode;
  try {
    const child = spawnProcess(executable, args, { env: childEnv, stdio: 'inherit', windowsHide: true });
    exitCode = await new Promise(resolveExit => {
      child.once('error', () => {
        io.error?.('The configured Agentweaver CLI apphost could not be started.');
        resolveExit(1);
      });
      child.once('exit', code => resolveExit(Number.isInteger(code) ? code : 1));
    });
  } catch {
    io.error?.('The configured Agentweaver CLI apphost could not be started.');
    exitCode = 1;
  }
  return exitCode;
}
