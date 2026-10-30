import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { constants } from 'node:fs';
import { access, open, readFile, rm, stat } from 'node:fs/promises';
import { dirname, isAbsolute, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const proofPathNames = [
  'publicationPath',
  'manifestPath',
  'agentHostArchivePath',
  'integrationDeploymentReceiptPath',
];

async function prepareCliInput(input, output) {
  if (typeof input !== 'string' || typeof output !== 'string') return { input };
  const inputPath = resolve(input);
  const outputPath = resolve(output);
  if (process.platform === 'win32'
    ? inputPath.toUpperCase() === outputPath.toUpperCase()
    : inputPath === outputPath) return { input };
  let manifest;
  try {
    manifest = JSON.parse(await readFile(inputPath, 'utf8'));
  } catch {
    return { input };
  }
  const evidence = manifest?.deploymentEvidence;
  if (!evidence || typeof evidence !== 'object' || Array.isArray(evidence)) return { input };

  const adaptedEvidence = { ...evidence };
  let changed = false;
  for (const name of proofPathNames) {
    if (typeof evidence[name] === 'string' && !isAbsolute(evidence[name])) {
      adaptedEvidence[name] = resolve(repositoryRoot, evidence[name]);
      changed = true;
    }
  }
  if (!changed) return { input };

  const temporaryPath = `${inputPath}.cli-${randomUUID()}.json`;
  let file;
  try {
    file = await open(temporaryPath, 'wx', 0o600);
    await file.writeFile(JSON.stringify({ ...manifest, deploymentEvidence: adaptedEvidence }));
    await file.close();
  } catch (error) {
    await file?.close().catch(() => {});
    if (file) await rm(temporaryPath, { force: true }).catch(() => {});
    throw error;
  }
  return { input: temporaryPath, temporaryPath };
}

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

  let preparedInput;
  try {
    preparedInput = await prepareCliInput(options.input, options.output);
  } catch {
    io.error?.('The legacy P1 manifest could not be prepared for the CLI apphost.');
    return 2;
  }
  const args = ['test', '--input', preparedInput.input, '--output', options.output];
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
  if (preparedInput.temporaryPath) {
    try {
      await rm(preparedInput.temporaryPath);
    } catch {
      io.error?.('The temporary CLI manifest could not be removed.');
      exitCode = 1;
    }
  }
  return exitCode;
}
