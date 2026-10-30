import { pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { mkdir, open, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createHash, randomUUID } from 'node:crypto';
import {
  createP1AcceptanceResult,
  persistP1AcceptanceResult,
  validateP1NativeProbeResults,
} from './p1-acceptance-result.mjs';
import { validateP1RunnerPlanInput } from './p1-journey-input.mjs';
import {
  createP1Fixture,
  createP1IdempotencyKey,
  summarizeP1FixtureCleanup,
} from './p1-fixture-setup.mjs';
import { createP1SetupApi, acquireP1RunIdentity, createP1SurfaceAdapters } from './p1-runtime-adapters.mjs';
import { runP1SurfaceJourney } from './p1-surface-runner.mjs';
import {
  attachSessionMaterialAcceptance,
  createSessionMaterialAcceptance,
} from './session-material-acceptance.mjs';
import { sanitizeP1Failure } from './p1-surface-common.mjs';
import { redact } from './redaction.mjs';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from './safe-jsonl.mjs';
import { runP1DotnetCli } from './p1-dotnet-cli.mjs';

/** CLI orchestration for P1 setup, surface journeys, cleanup, and JSONL results. */
/** Serialize an object with recursively sorted keys for a stable execution-plan fingerprint. */
function stableJson(value) {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key =>
      `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

/** Match resolved paths using the host filesystem's case rules. */
function pathIdentity(path) {
  return process.platform === 'win32' ? path.toLowerCase() : path;
}

/** Bind a persisted invocation to its plan, surface, and exact native probe envelopes. */
function invocationBinding(validated, surface, nativeProbeResults) {
  // Execute is the only admitted intent; omit it so existing invocation hashes remain resumable.
  const boundInput = { ...validated };
  delete boundInput.executionIntent;
  return {
    executionPlanSha256: createHash('sha256')
      .update(stableJson({ validated: boundInput, nativeProbeResults })).digest('hex'),
    surface,
  };
}

/** Parse supported CLI flags and reject duplicates or unknown options. */
function parseArguments(argv) {
  const result = {};
  const optionKeys = new Map([
    ['--surface', 'surface'],
    ['--input', 'input'],
    ['--output', 'output'],
    ['--page-module', 'pageModule'],
    ['--identity-module', 'identityModule'],
    ['--native-probe-envelope', 'nativeProbeEnvelope'],
    ['--dispatch-recovery-envelope', 'dispatchRecoveryEnvelope'],
  ]);
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === '--help') {
      result.help = true;
      continue;
    }
    if (name === '--dry-run' || name === '--interactive') {
      const key = name === '--dry-run' ? 'dryRun' : 'interactive';
      if (result[key]) throw new Error(`The ${name} option can only be used once.`);
      result[key] = true;
      continue;
    }
    if (!optionKeys.has(name)
      || typeof argv[index + 1] !== 'string' || argv[index + 1].startsWith('--')) {
      throw new Error('Use --surface api|mcp|ui --input <manifest.json> --output <evidence.jsonl> --identity-module <module.mjs> [--page-module <module.mjs>] [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>].');
    }

    const key = optionKeys.get(name);
    if (result[key] !== undefined) throw new Error(`The ${name} option can only be used once.`);
    result[key] = argv[++index];
  }
  return result;
}

export async function runP1CliEntrypoint(argv = process.argv.slice(2), env = process.env, io = console, {
  runDotnetCli = runP1DotnetCli,
  runLegacy = runP1Cli,
} = {}) {
  const requestsApi = argv.some((argument, index) =>
    argument === '--surface' && argv[index + 1]?.split(',').includes('api'));
  const requestsMcp = argv.some((argument, index) =>
    argument === '--surface' && argv[index + 1]?.split(',').includes('mcp'));
  if (requestsMcp) {
    io.error?.('The Agentweaver 1.x P1 harness does not support an MCP surface; use --surface api for .NET CLI acceptance.');
    return 2;
  }
  let options;
  try {
    options = parseArguments(argv);
  } catch (error) {
    if (requestsApi) {
      io.error?.(error.message);
      return 2;
    }
    return runLegacy(argv, env, io);
  }
  if (options.surface !== 'api') {
    if (options.surface !== 'ui') {
      io.error?.('Specify --surface api or ui; the API runner requires its configured .NET apphost.');
      return 2;
    }
    if (options.dryRun || options.interactive) {
      io.error?.('--dry-run and --interactive are supported only by the .NET API acceptance runner.');
      return 2;
    }
    return runLegacy(argv, env, io);
  }
  if (!options.input || !options.output) {
    io.error?.('The API surface requires --input <manifest.json> and --output <evidence.jsonl>.');
    return 2;
  }
  if (options.identityModule || options.pageModule) {
    io.error?.('The .NET API runner owns authentication and does not accept Node identity or page modules.');
    return 2;
  }
  return runDotnetCli(options, env, io);
}

/** Read and validate one exact output envelope without accepting a partial or scripted shape. */
async function readProbeEnvelope(path, expectedKind) {
  let envelope;
  try {
    envelope = JSON.parse(await readFile(path, 'utf8'));
  } catch {
    throw new Error(`The ${expectedKind} output envelope is missing or malformed JSON.`);
  }
  if (envelope?.kind !== expectedKind) {
    throw new Error(`The supplied probe output must be an exact ${expectedKind} envelope.`);
  }
  validateP1NativeProbeResults([envelope]);
  return envelope;
}

/** Build a blocked acceptance result while retaining every valid supplied probe envelope. */
function createFailureResult(surface, error, nativeProbeResults = []) {
  return createP1AcceptanceResult({
    surface,
    surfaceStatus: 'blocked',
    nativeProbeResults,
    observations: {
      cleanup: { intent: 'none', status: 'not-started' },
      adapterCleanup: { mcp: 'not-started', ui: surface === 'ui' ? 'not-started' : 'not-required' },
    },
    failure: sanitizeP1Failure(error),
  });
}

/**
 * Create the invocation record exclusively, or resume only a matching unfinished JSONL invocation.
 * Existing UI-action records or completed results block retry because owner state may have changed.
 */
async function getOrCreateInvocationId(outputPath, binding, createId = randomUUID) {
  await mkdir(dirname(outputPath), { recursive: true });
  try {
    const invocationId = createId();
    const handle = await open(outputPath, 'wx', 0o600);
    try {
      await handle.writeFile(`${serializeRedactedJsonLine({
        kind: 'p1-setup-invocation',
        schemaVersion: 2,
        invocationId,
        ...binding,
      })}\n`, 'utf8');
    } finally {
      await handle.close();
    }
    return invocationId;
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
  }

  let lines;
  try {
    lines = (await readFile(outputPath, 'utf8')).split(/\r?\n/).filter(Boolean);
  } catch {
    throw new Error('The existing P1 evidence output cannot be resumed safely.');
  }
  let records;
  try {
    records = lines.map(line => JSON.parse(line));
  } catch {
    throw new Error('The existing P1 evidence output is not valid JSON Lines.');
  }
  const intents = records.filter(record => record?.kind === 'p1-setup-invocation');
  if (intents.length !== 1 || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
    .test(intents[0].invocationId ?? '')
    || records.some(record => record?.kind === 'p1-acceptance-result')) {
    throw new Error('The existing P1 evidence output is not an unfinished invocation that can be resumed.');
  }
  if (records.some(record => record?.kind === 'p1-ui-action-started')) {
    throw new Error('The existing P1 evidence output may contain an attempted UI write; reconcile its owner state before retrying.');
  }
  const existingBinding = intents[0];
  if (existingBinding.schemaVersion !== 2
    || !/^[a-f0-9]{64}$/.test(existingBinding.executionPlanSha256 ?? '')
    || !['api', 'mcp', 'ui'].includes(existingBinding.surface)) {
    throw new Error('The existing P1 invocation record has no complete validated-plan and surface binding and cannot be resumed safely.');
  }
  if (existingBinding.executionPlanSha256 !== binding.executionPlanSha256) {
    throw new Error('The existing P1 invocation is bound to a different validated execution plan.');
  }
  if (existingBinding.surface !== binding.surface) {
    throw new Error('The existing P1 invocation is bound to a different selected surface.');
  }
  return intents[0].invocationId;
}

/** Add cleanup evidence and promote uncertain or failed cleanup to a failed acceptance result. */
function addFixtureCleanup(result, state, cleanup, nativeFence, cleanupError) {
  const fixtureSetup = summarizeP1FixtureCleanup(state, cleanup, nativeFence);
  result.observations = { ...result.observations, fixtureSetup };
  if (fixtureSetup.cleanup?.state === 'indeterminate') {
    const message = 'A fixture setup or cleanup request may have completed, but its response was not verified; reconcile the invocation before retrying.';
    result.failure = result.failure
      ? { message: `${result.failure.message}; ${message}` }
      : { message };
    result.surfaceStatus = 'failed';
    result.fullP1Status = 'failed';
    result.exitCode = 1;
  }
  if (cleanupError) {
    const error = sanitizeP1Failure(cleanupError);
    result.failure = result.failure
      ? { message: `${result.failure.message}; setup cleanup failed: ${error.message}` }
      : error;
    result.surfaceStatus = 'failed';
    result.fullP1Status = 'failed';
    result.exitCode = 1;
  }
  return result;
}

/**
 * Validate inputs, create an owned fixture, run one surface journey, and clean up.
 * Product acceptance does not require image publication or source-provenance artifacts.
 * One invocation and evidence file belong to one surface; the sign-in launcher coordinates multiple surfaces.
 */
export async function runP1Cli(argv = process.argv.slice(2), env = process.env, io = console, {
  createSetupApi = createP1SetupApi,
  createFixture = createP1Fixture,
  acquireRunIdentity = acquireP1RunIdentity,
  createSurfaceAdapters = createP1SurfaceAdapters,
  pageProvider,
  runSurfaceJourney = runP1SurfaceJourney,
  identityProvider,
  attachSessionMaterialAcceptance: attachSessionMaterial = attachSessionMaterialAcceptance,
} = {}) {
  let options;
  let surface = 'api';
  let outputPath;
  let inputPath;
  let result;
  const nativeProbeResults = [];
  if (argv.includes('--help')) {
    io.log('Usage: npm run harness:p1 -- --surface api --input <manifest.json> --output <evidence.jsonl> [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>] [--dry-run] [--interactive]');
    io.log('Legacy direct-runner usage: node scripts/harness-shared/run-p1.mjs --surface mcp|ui --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs> [--page-module <authorized-page-provider.mjs>] [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>]');
    io.log('The API surface delegates to the exact AGENTWEAVER_CLI_PATH apphost; authentication is non-interactive unless --interactive is explicit.');
    io.log('The legacy direct runner is not a supported Agentweaver 1.x acceptance surface; use the .NET CLI API surface for 1.x acceptance.');
    io.log('Only exact output envelopes from the P1 probe runners are aggregated; missing probe envelopes remain blocked.');
    return 0;
  }

  try {
    options = parseArguments(argv);
    if (!['api', 'mcp', 'ui'].includes(options.surface) || !options.input || !options.output) {
      throw new Error('A P1 surface, input manifest, and evidence output path are required.');
    }
    surface = options.surface;
    if (surface !== 'ui' && !options.identityModule && typeof identityProvider !== 'function') {
      throw new Error('The selected API identity provider module is required.');
    }
    inputPath = resolve(options.input);
    const requestedOutputPath = resolve(options.output);
    if (pathIdentity(inputPath) === pathIdentity(requestedOutputPath)) {
      throw new Error('The evidence output path must not replace the input manifest.');
    }
    outputPath = requestedOutputPath;
    const envelopeInputs = [
      [options.nativeProbeEnvelope, 'p1-native-acceptance'],
      [options.dispatchRecoveryEnvelope, 'p1-dispatch-recovery-acceptance'],
    ].filter(([path]) => path !== undefined).map(([path, kind]) => [resolve(path), kind]);
    const protectedPaths = new Set([inputPath, outputPath].map(pathIdentity));
    for (const [path] of envelopeInputs) {
      const identity = pathIdentity(path);
      if (protectedPaths.has(identity)) {
        // Do not append the failure receipt to an envelope that was also selected as output.
        if (identity === pathIdentity(requestedOutputPath)) outputPath = undefined;
        throw new Error('Probe envelope paths must not replace or duplicate another P1 input or the evidence output.');
      }
      protectedPaths.add(identity);
    }
    for (const [path, kind] of envelopeInputs) {
      nativeProbeResults.push(await readProbeEnvelope(path, kind));
    }

    let input;
    try {
      input = JSON.parse(await readFile(inputPath, 'utf8'));
    } catch {
      throw new Error('The P1 input manifest is missing or malformed JSON.');
    }
    let validated;
    try {
      validated = validateP1RunnerPlanInput(input);
    } catch (error) {
      throw new Error(`P1 preflight blocked before network activity: ${error.message}`);
    }
    const { pins } = validated;
    // Refuse to create a live fixture if the UI has no authorized page source.
    if (surface === 'ui' && !options.pageModule && typeof pageProvider !== 'function') {
      throw new Error('The UI surface requires the built-in sign-in launcher or an authorized page provider.');
    }
    const invocationId = await getOrCreateInvocationId(
      outputPath, invocationBinding(validated, surface, nativeProbeResults));
    const setupToken = env.AGENTWEAVER_P1_SETUP_TOKEN;
    if (typeof setupToken !== 'string' || !setupToken) {
      throw new Error('The setup token is unavailable; set AGENTWEAVER_P1_SETUP_TOKEN before execution.');
    }
    const setupApi = createSetupApi({ environment: validated.environment, setupToken });
    let fixture;
    let cleanup;
    let cleanupError;
    const nativeFence = {
      status: 'not-started',
      reason: 'This runner never calls an Orchestrator dispatch operation.',
    };
    try {
      fixture = await createFixture(validated, {
        api: setupApi,
        invocationId,
        acquireRunIdentity: async binding => {
          const identity = await acquireRunIdentity({
            modulePath: options.identityModule ? resolve(options.identityModule) : undefined,
            environment: validated.environment,
            projectId: binding.projectId,
            runId: binding.runId,
            ...(validated.fixturePlan.deniedActor
              ? { deniedActor: validated.fixturePlan.deniedActor }
              : {}),
            provideIdentity: identityProvider
              ? providerInput => identityProvider({
                ...providerInput,
                ...(validated.fixturePlan.deniedActor
                  ? { deniedActor: validated.fixturePlan.deniedActor }
                  : {}),
              })
              : undefined,
          });
          return {
            ...identity,
            ...identity.createClients({ deniedActor: validated.fixturePlan.deniedActor }),
          };
        },
      });
      try {
        const adapters = await createSurfaceAdapters({
          surface,
          environment: validated.environment,
          identity: fixture.identity,
          deniedActor: validated.fixturePlan.deniedActor,
          pageModulePath: options.pageModule ? resolve(options.pageModule) : undefined,
          pageProvider,
        });
        result = await runSurfaceJourney(surface, fixture.input, {
          ...adapters,
          nativeProbeResults,
          createIdempotencyKey: () => createP1IdempotencyKey(invocationId),
          beforeUiAction: async ({ journey }) => appendRedactedJsonLine(outputPath, {
            kind: 'p1-ui-action-started',
            schemaVersion: 1,
            invocationId,
            surface,
            journey,
          }),
        });
      } catch (error) {
        result = createFailureResult(surface, error, nativeProbeResults);
      }
      try {
        cleanup = await fixture.cleanup();
      } catch (error) {
        cleanupError = error;
      }
      result = addFixtureCleanup(result, fixture.state, cleanup, nativeFence, cleanupError);
      // A failed evidence attachment must not bypass fixture cleanup or erase its receipt.
      try {
        result = attachSessionMaterial(
          result,
          createSessionMaterialAcceptance({
            environment: validated.environment,
            fixtureInput: fixture.input,
          }),
        );
      } catch (error) {
        const failure = sanitizeP1Failure(error);
        result.failure = result.failure
          ? { message: `${result.failure.message}; session-material acceptance failed: ${failure.message}` }
          : { message: `Session-material acceptance failed: ${failure.message}` };
        result.fullP1Status = 'failed';
        result.exitCode = 1;
      }
    } finally {
      if (!result) {
        result = createFailureResult(surface,
          new Error('P1 fixture setup did not produce an acceptance result.'),
          nativeProbeResults);
      }
    }
  } catch (error) {
    if (!['api', 'mcp', 'ui'].includes(surface)) surface = 'api';
    result = createFailureResult(surface, error, nativeProbeResults);
    if (error.p1SetupState) {
      let cleanup;
      let cleanupError;
      try {
        cleanup = await error.p1SetupCleanup?.();
      } catch (cleanupFailure) {
        cleanupError = cleanupFailure;
      }
      result = addFixtureCleanup(result, error.p1SetupState, cleanup,
        { status: 'not-started', reason: 'This runner never calls an Orchestrator dispatch operation.' },
        cleanupError);
    }
  }

  if (outputPath) {
    try {
      await persistP1AcceptanceResult(outputPath, result);
    } catch (error) {
      io.error(`Could not persist P1 evidence: ${sanitizeP1Failure(error).message}`);
      io.log(JSON.stringify(redact(result)));
      return 1;
    }
  }
  io.log(JSON.stringify(redact(result)));
  return result.exitCode;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const exitCode = await runP1CliEntrypoint();
  process.exitCode = exitCode;
}
