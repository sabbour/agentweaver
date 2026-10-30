import { pathToFileURL } from 'node:url';
import { readFile } from 'node:fs/promises';
import { extname, resolve } from 'node:path';
import {
  authorizeP1InBrowser,
  P1_RUN_SCOPES,
  P1_SETUP_SCOPES,
  P1_SIGNIN_PROFILE_ROOT,
  openP1AuthorizedUiPage,
  readP1SignInInput,
  validateP1BrokerToken,
  withFreshP1BrowserContext,
} from './p1-signin.mjs';
import { createP1SetupApi } from './p1-runtime-adapters.mjs';
import { persistP1ReconcileEvidence, reconcileP1Run, reserveP1EvidenceOutput } from './p1-reconcile.mjs';
import { isValidP1RecoveryEvidence, recoverP1RunCleanup } from './p1-recovery-cleanup.mjs';
import { redact } from './redaction.mjs';
import { runP1DotnetCli } from './p1-dotnet-cli.mjs';
import { runP1Cli } from './run-p1.mjs';

const OPTION_NAMES = new Set([
  '--reconcile',
  '--recover-cleanup',
  '--reconcile-evidence',
  '--surface',
  '--input',
  '--output',
  '--native-probe-envelope',
  '--dispatch-recovery-envelope',
]);

export function parseP1SigninArguments(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 1) {
    const option = argv[index];
    if (option === '--help') {
      result.help = true;
      continue;
    }
    if (option === '--dry-run') {
      if (result.dryRun) throw new Error('The --dry-run option can only be used once.');
      result.dryRun = true;
      continue;
    }
    if (option === '--interactive') {
      if (result.interactive) throw new Error('The --interactive option can only be used once.');
      result.interactive = true;
      continue;
    }
    if (option === '--identity-module') {
      throw new Error('The sign-in launcher supplies its built-in identity provider; --identity-module is not accepted.');
    }
    if (!OPTION_NAMES.has(option)
      || typeof argv[index + 1] !== 'string'
      || argv[index + 1].startsWith('--')) {
      throw new Error('Use --surface api|mcp|ui --input <manifest.json> --output <evidence.jsonl> [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>] [--interactive], --reconcile <invocation-id> --input <manifest.json> --output <reconciliation.json>, or --recover-cleanup <invocation-id> --input <manifest.json> --reconcile-evidence <reconciliation.json> --output <cleanup.json>.');
    }
    const key = option.slice(2).replaceAll('-', '');
    if (result[key] !== undefined) throw new Error(`The ${option} option can only be used once.`);
    result[key] = argv[++index];
  }
  return result;
}

function printP1OperatorSteps(io, { dryRun = false } = {}) {
  io.log?.('Operator steps:');
  io.log?.('1. Confirm the manifest matches one exact deployed release and source, publication and AgentHost archive measurements, live provider/runtime/storage receipts, and the current selection revision.');
  io.log?.('2. Supply independent native and recovery evidence for any checks that need it. This one-account launcher does not create a second identity or invent missing evidence.');
  io.log?.('3. Complete one interactive sign-in in the headed Chrome window with the approved account. Review and approve only the scopes shown for the registered client.');
  io.log?.('4. Keep the redacted evidence output. The harness creates an invocation-owned project, grants Owner and Orchestrator, runs the selected journey, then revokes Orchestrator, archives the project, and revokes Owner.');
  io.log?.('5. If setup or cleanup is indeterminate, stop and follow the evidence diagnosis. Do not retry mutations or remove unrelated resources.');
  io.log?.('No token path, token environment variable, provider module, second account, or second sign-in is required.');
  if (dryRun) {
    io.log?.('Dry run only: the manifest shape is checked. No browser, network request, evidence file, project, or assignment is created.');
  }
}

function printP1ReconcileSteps(io, { dryRun = false } = {}) {
  io.log?.('Read-only P1 reconciliation: inspect one invocation before considering another live journey.');
  io.log?.('The mode signs in once with the registered agentweaver-web client and setup scopes.');
  io.log?.('It reads the invocation-named project and the setup actor\'s effective authority, then writes one redacted JSON evidence file.');
  io.log?.('It never changes grants or fixtures, performs cleanup, starts or admits a journey, or approves cleanup.');
  if (dryRun) {
    io.log?.('Dry run only: the manifest and invocation ID are checked. No browser, network request, or evidence file is created.');
  }
}

function printP1RecoveryCleanupSteps(io, { dryRun = false } = {}) {
  io.log?.('Guarded recovery cleanup: verify the saved assessment against a fresh read in one sign-in.');
  io.log?.('The signed-in actor must still be a tenant admin and the project, actor, assignments, revisions, and no-revocation audit must match exactly.');
  io.log?.('The harness revokes Orchestrator, archives the project, then revokes Owner. Each write uses its current expected revision and is sent once.');
  io.log?.('Any failed or unverified write stops further writes as indeterminate; the harness then performs read-only reconciliation and writes a redacted receipt.');
  if (dryRun) {
    io.log?.('Dry run only: local manifest and saved evidence are checked. No browser, network request, target write, or evidence output is created.');
  }
}

async function readP1RecoveryEvidence(file) {
  let content;
  try {
    content = await readFile(file, 'utf8');
  } catch {
    throw new Error('The saved reconciliation evidence file could not be read.');
  }
  try {
    return JSON.parse(content);
  } catch {
    throw new Error('The saved reconciliation evidence is not valid JSON.');
  }
}

function tokenRequirements(environment, actorId, scopes, binding) {
  return {
    issuer: environment.issuer,
    audience: environment.audience,
    actorId,
    ...(binding ?? {}),
    requiredScopes: scopes.filter(scope =>
      !['openid', 'profile', 'email', 'offline_access'].includes(scope)),
  };
}

/** Built-in run identity provider reuses the one interactive account for the selected run. */
export function createP1SigninIdentityProvider({
  environment,
  signIn,
  context,
  signal,
  io,
} = {}) {
  return async ({ baseUrl, actorId, tenantId, issuer, audience, projectId, runId }) => {
    const selectedEnvironment = {
      ...environment,
      baseUrl,
      actorId,
      tenantId,
      issuer,
      audience,
    };
    const token = await signIn({
      environment: selectedEnvironment,
      actorId,
      projectId,
      runId,
      scopes: P1_RUN_SCOPES,
      context,
      signal,
      io,
    });
    validateP1BrokerToken(token, tokenRequirements(
      selectedEnvironment, actorId, P1_RUN_SCOPES, { projectId, runId },
    ));
    return { token };
  };
}

/** Keep each surface's resumable invocation separate while sharing one sign-in session. */
export function p1SigninSurfaceArguments(argv, surface, output, multiple) {
  const result = [...argv];
  result[result.indexOf('--surface') + 1] = surface;
  const extension = extname(output);
  result[result.indexOf('--output') + 1] = multiple
    ? `${output.slice(0, output.length - extension.length)}.${surface}${extension}`
    : output;
  return result;
}

/** Run selected surfaces sequentially; tokens and the shared browser session stay in memory. */
export async function runP1SigninCli(argv = process.argv.slice(2), env = process.env, io = console, {
  runP1 = runP1Cli,
  runDotnetCli = runP1DotnetCli,
  readInput = readP1SignInInput,
  signIn = authorizeP1InBrowser,
  createSetupApi = createP1SetupApi,
  reconcile = reconcileP1Run,
  recoverCleanup = recoverP1RunCleanup,
  persistEvidence = persistP1ReconcileEvidence,
  reserveEvidenceOutput = reserveP1EvidenceOutput,
  chromium,
  signal,
  loadChromium = async () => (await import('playwright')).chromium,
  withBrowserContext = withFreshP1BrowserContext,
  authRoot = P1_SIGNIN_PROFILE_ROOT,
} = {}) {
  let options;
  try {
    options = parseP1SigninArguments(argv);
  } catch (error) {
    io.error?.(error.message);
    return 2;
  }
  if (options.help) {
    io.log?.('Usage: npm run harness:p1:signin-run -- --surface api|mcp|ui --input <manifest.json> --output <evidence.jsonl> [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>] [--dry-run] [--interactive]');
    io.log?.('Usage: npm run harness:p1:signin-run -- --reconcile <invocation-id> --input <manifest.json> --output <reconciliation.json> [--dry-run]');
    io.log?.('Usage: npm run harness:p1:signin-run -- --recover-cleanup <invocation-id> --input <manifest.json> --reconcile-evidence <reconciliation.json> --output <cleanup.json> [--dry-run]');
    io.log?.('The .NET API apphost is selected only through AGENTWEAVER_CLI_PATH; it uses non-interactive auth unless --interactive is explicit.');
    io.log?.('MCP and UI remain legacy browser surfaces using the registered agentweaver-web client; Broker tokens stay in memory.');
    io.log?.('Mixed api,mcp runs are rejected; run API separately from MCP or UI.');
    printP1OperatorSteps(io);
    return 0;
  }
  if (options.reconcile !== undefined) {
    if (!options.input || !options.output
      || options.surface || Object.hasOwn(options, 'nativeprobeenvelope')
      || Object.hasOwn(options, 'dispatchrecoveryenvelope')
      || options.recovercleanup || options.interactive || Object.hasOwn(options, 'reconcileevidence')
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(options.reconcile)) {
      io.error?.('Read-only reconciliation requires --reconcile <invocation-id>, --input <manifest.json>, and --output <reconciliation.json>; journey and probe options are not accepted.');
      return 2;
    }
    let validated;
    try {
      validated = await readP1SignInInput(argv);
    } catch (error) {
      io.error?.(error.message);
      return 2;
    }
    if (options.dryRun) {
      printP1ReconcileSteps(io, { dryRun: true });
      return 0;
    }

    let setupToken;
    try {
      printP1ReconcileSteps(io);
      const browser = chromium ?? await loadChromium();
      return await withBrowserContext({
        chromium: browser,
        authRoot,
        signal,
      }, async context => {
        // Keep the setup actor unbound and reuse the same registered client, scopes, and browser session.
        setupToken = await signIn({
          environment: validated.environment,
          actorId: validated.environment.actorId,
          scopes: P1_SETUP_SCOPES,
          chromium: browser,
          context,
          signal,
          io,
        });
        validateP1BrokerToken(setupToken, tokenRequirements(
          validated.environment,
          validated.environment.actorId,
          P1_SETUP_SCOPES,
        ));

        // Only the reconciler receives the setup-scoped API client; it whitelists the two documented GET operations.
        const api = createSetupApi({
          environment: validated.environment,
          setupToken,
        });
        const evidence = await reconcile({
          api,
          environment: validated.environment,
          invocationId: options.reconcile,
          signal,
        });
        await persistEvidence(options.output, evidence);
        io.log?.(`Reconciliation verdict: ${evidence.verdict}. No cleanup, fixture write, grant change, or new journey was performed.`);
        return evidence.verdict === 'already clean' ? 0 : 1;
      });
    } catch {
      io.error?.('P1 reconciliation could not be completed. No token values were written or displayed.');
      return 1;
    } finally {
      setupToken = undefined;
    }
  }
  if (options.recovercleanup !== undefined) {
    if (!options.input || !options.output || !options.reconcileevidence || options.surface
      || options.reconcile || Object.hasOwn(options, 'nativeprobeenvelope')
      || Object.hasOwn(options, 'dispatchrecoveryenvelope') || options.interactive
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(options.recovercleanup)) {
      io.error?.('Guarded recovery cleanup requires --recover-cleanup <invocation-id>, --input <manifest.json>, --reconcile-evidence <reconciliation.json>, and --output <cleanup.json>; journey and probe options are not accepted.');
      return 2;
    }
    let validated;
    let savedEvidence;
    try {
      validated = await readP1SignInInput(argv);
      savedEvidence = await readP1RecoveryEvidence(options.reconcileevidence);
      if (!isValidP1RecoveryEvidence(savedEvidence, {
        invocationId: options.recovercleanup,
        environment: validated.environment,
      })) {
        throw new Error('The saved reconciliation evidence does not authorize recovery for this invocation and setup actor.');
      }
    } catch (error) {
      io.error?.(error.message);
      return 2;
    }
    if (options.dryRun) {
      printP1RecoveryCleanupSteps(io, { dryRun: true });
      return 0;
    }

    let evidenceOutput;
    try {
      evidenceOutput = await reserveEvidenceOutput(options.output);
    } catch {
      io.error?.('P1 recovery cleanup requires a new output path; no sign-in or target writes were attempted.');
      return 2;
    }

    let setupToken;
    try {
      printP1RecoveryCleanupSteps(io);
      const browser = chromium ?? await loadChromium();
      return await withBrowserContext({
        chromium: browser,
        authRoot,
        signal,
      }, async context => {
        setupToken = await signIn({
          environment: validated.environment,
          actorId: validated.environment.actorId,
          scopes: P1_SETUP_SCOPES,
          chromium: browser,
          context,
          signal,
          io,
        });
        validateP1BrokerToken(setupToken, tokenRequirements(
          validated.environment,
          validated.environment.actorId,
          P1_SETUP_SCOPES,
        ));

        const api = createSetupApi({
          environment: validated.environment,
          setupToken,
        });
        const evidence = await recoverCleanup({
          api,
          environment: validated.environment,
          invocationId: options.recovercleanup,
          savedEvidence,
          signal,
          reconcile,
        });
        try {
          await evidenceOutput.write(evidence);
        } catch {
          io.error?.('The recovery result could not be saved. The redacted result follows; do not retry an indeterminate outcome.');
          io.error?.(JSON.stringify(redact(evidence), null, 2));
          return 1;
        }
        io.log?.(`Recovery cleanup outcome: ${evidence.outcome}; readback verdict: ${evidence.readback?.verdict ?? 'not performed'}.`);
        return evidence.outcome === 'completed' ? 0 : 1;
      });
    } catch {
      io.error?.('P1 recovery cleanup could not be completed. No token values were written or displayed.');
      return 1;
    } finally {
      setupToken = undefined;
      try {
        await evidenceOutput.release();
      } catch {
        io.error?.('The reserved recovery output could not be closed or removed; inspect that exact path before another attempt.');
      }
    }
  }
  const surfaces = options.surface?.split(',') ?? [];
  if (surfaces.length === 0 || new Set(surfaces).size !== surfaces.length
    || surfaces.some(surface => !['api', 'mcp', 'ui'].includes(surface))
    || (surfaces.includes('ui') && surfaces.length > 1)
    || !options.input || !options.output || options.reconcile
    || options.recovercleanup || Object.hasOwn(options, 'reconcileevidence')) {
    io.error?.('Select api, mcp, api,mcp, or ui and supply an input manifest and evidence output path.');
    return 2;
  }
  if (surfaces.includes('api')) {
    if (surfaces.length !== 1) {
      io.error?.('The .NET API runner does not share a sign-in session with the legacy MCP or UI runners; run the API surface separately.');
      return 2;
    }
    let validated;
    try {
      validated = await readInput(argv);
    } catch (error) {
      io.error?.(error.message);
      return 2;
    }
    if (Object.hasOwn(validated.fixturePlan, 'deniedActor')) {
      io.error?.('This launcher uses one Broker account only. Omit fixturePlan.deniedActor; distinct-actor coverage remains unavailable with one account.');
      return 2;
    }
    return runDotnetCli({
      input: options.input,
      output: options.output,
      nativeProbeEnvelope: options.nativeprobeenvelope,
      dispatchRecoveryEnvelope: options.dispatchrecoveryenvelope,
      dryRun: options.dryRun,
      interactive: options.interactive === true,
    }, env, io);
  }
  if (options.interactive) {
    io.error?.('--interactive is supported only by the .NET API acceptance runner.');
    return 2;
  }
  let setupToken;
  if (options.dryRun) {
    let validated;
    try {
      validated = await readP1SignInInput(argv);
    } catch (error) {
      io.error?.(error.message);
      return 2;
    }
    if (Object.hasOwn(validated.fixturePlan, 'deniedActor')) {
      io.error?.('This launcher uses one Broker account only. Omit fixturePlan.deniedActor; distinct-actor coverage remains unavailable with one account.');
      return 2;
    }
    printP1OperatorSteps(io, { dryRun: true });
    return 0;
  }

  try {
    const validated = await readP1SignInInput(argv);
    if (Object.hasOwn(validated.fixturePlan, 'deniedActor')) {
      io.error?.('This launcher uses one Broker account only. Omit fixturePlan.deniedActor; distinct-actor coverage remains unavailable with one account.');
      return 2;
    }
    printP1OperatorSteps(io);
    const browser = chromium ?? await loadChromium();
    return await withBrowserContext({
      chromium: browser,
      authRoot,
      signal,
    }, async context => {
      const interactiveSignIn = request => signIn({
        ...request,
        chromium: browser,
        context,
        signal,
        io,
      });
      setupToken = await interactiveSignIn({
        environment: validated.environment,
        actorId: validated.environment.actorId,
        scopes: P1_SETUP_SCOPES,
      });
      validateP1BrokerToken(setupToken, tokenRequirements(
        validated.environment,
        validated.environment.actorId,
        P1_SETUP_SCOPES,
      ));

      const identityProvider = createP1SigninIdentityProvider({
        environment: validated.environment,
        signIn: interactiveSignIn,
        context,
        signal,
        io,
      });
      let exitCode = 0;
      for (const surface of surfaces) {
        if (signal?.aborted) throw new Error('P1 sign-in was cancelled.');
        const surfaceArgv = p1SigninSurfaceArguments(
          argv, surface, options.output, surfaces.length > 1,
        );
        // Resolve the UI page only after source checks and owned fixture setup have succeeded.
        const pageProvider = surface === 'ui'
          ? async ({ baseUrl }) => openP1AuthorizedUiPage({
            context,
            baseUrl,
            signal,
            io,
          })
          : undefined;
        const result = await runP1(surfaceArgv, {
          ...env,
          AGENTWEAVER_P1_SETUP_TOKEN: setupToken,
        }, io, { identityProvider, pageProvider });
        exitCode = Math.max(exitCode, result);
      }
      return exitCode;
    });
  } catch {
    io.error?.('P1 sign-in could not be completed. No token values were written or displayed.');
    return 1;
  } finally {
    setupToken = undefined;
  }
}

async function main() {
  const controller = new AbortController();
  const abort = () => controller.abort();
  process.once('SIGINT', abort);
  process.once('SIGTERM', abort);
  try {
    process.exitCode = await runP1SigninCli(process.argv.slice(2), process.env, console, {
      signal: controller.signal,
    });
  } finally {
    process.removeListener('SIGINT', abort);
    process.removeListener('SIGTERM', abort);
  }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  await main();
}
