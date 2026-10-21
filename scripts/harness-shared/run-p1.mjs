import { pathToFileURL } from 'node:url';
import { dirname, isAbsolute, resolve } from 'node:path';
import { mkdir, open, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createHash, randomUUID } from 'node:crypto';
import {
  createP1AcceptanceResult,
  persistP1AcceptanceResult,
  validateP1NativeProbeResults,
} from './p1-acceptance-result.mjs';
import { prepareP1DeploymentEvidence } from './p1-deployment-evidence.mjs';
import { validateP1RunnerPlanInput } from './p1-journey-input.mjs';
import {
  createP1Fixture,
  createP1IdempotencyKey,
  summarizeP1FixtureCleanup,
} from './p1-fixture-setup.mjs';
import { createP1SetupApi, acquireP1RunIdentity, createP1SurfaceAdapters } from './p1-runtime-adapters.mjs';
import { runP1SurfaceJourney } from './p1-surface-runner.mjs';
import { sanitizeP1Failure } from './p1-surface-common.mjs';
import { redact } from './redaction.mjs';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from './safe-jsonl.mjs';

/** CLI orchestration for source-pinned P1 setup, surface journeys, cleanup, and JSONL evidence. */
const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

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
  return {
    executionPlanSha256: createHash('sha256')
      .update(stableJson({ validated, nativeProbeResults })).digest('hex'),
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
function createFailureResult(surface, error, sourceEvidence, nativeProbeResults = []) {
  return createP1AcceptanceResult({
    surface,
    surfaceStatus: 'blocked',
    sourceEvidence,
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
 * Validate inputs and source evidence, create an owned fixture, run one surface journey, and clean up.
 * Release evidence carries its exact tag into the source verifier; output stays append-only and sign-in is external.
 * One invocation and evidence file belong to one surface; the sign-in launcher coordinates multiple surfaces.
 */
export async function runP1Cli(argv = process.argv.slice(2), env = process.env, io = console, {
  prepareDeploymentEvidence = prepareP1DeploymentEvidence,
  createSetupApi = createP1SetupApi,
  createFixture = createP1Fixture,
  acquireRunIdentity = acquireP1RunIdentity,
  createSurfaceAdapters = createP1SurfaceAdapters,
  runSurfaceJourney = runP1SurfaceJourney,
  identityProvider,
} = {}) {
  let options;
  let surface = 'api';
  let outputPath;
  let inputPath;
  let result;
  let sourceEvidence;
  const nativeProbeResults = [];
  if (argv.includes('--help')) {
    io.log('Usage: node scripts/harness-shared/run-p1.mjs --surface api|mcp|ui --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs> [--page-module <authorized-page-provider.mjs>] [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>]');
    io.log('The runner uses the supplied setup token only after source and deployment evidence pass; it never signs in or requests consent.');
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
    const { pins, deploymentEvidence } = validated;
    const resolveRepositoryPath = file => resolve(isAbsolute(file) ? file : resolve(repositoryRoot, file));
    const publicationPath = resolveRepositoryPath(deploymentEvidence.publicationPath);
    const manifestPath = resolveRepositoryPath(deploymentEvidence.manifestPath);
    const agentHostArchivePath = resolveRepositoryPath(deploymentEvidence.agentHostArchivePath);
    const integrationDeploymentReceiptPath = deploymentEvidence.integrationDeploymentReceiptPath
      ? resolveRepositoryPath(deploymentEvidence.integrationDeploymentReceiptPath)
      : undefined;
    if ([inputPath, publicationPath, manifestPath, agentHostArchivePath, integrationDeploymentReceiptPath]
      .filter(Boolean)
      .some(path => pathIdentity(path) === pathIdentity(requestedOutputPath))) {
      outputPath = undefined;
      throw new Error('The evidence output path must not replace a P1 input or deployment artifact.');
    }
    if (surface === 'ui') {
      throw new Error('The P1 UI page provider is blocked on #1859 until the 0.x UI is restored onto v1.');
    }
    sourceEvidence = await prepareDeploymentEvidence({
      repoRoot: repositoryRoot,
      expectedSourceSha: pins.sourceReceipt.sourceSha,
      context: pins.context,
      namespace: pins.namespace,
      publicationPath,
      manifestPath,
      releaseTag: deploymentEvidence.releaseTag,
      agentHostArchivePath,
      integrationDeploymentReceiptPath,
      providerCatalog: {
        configMapName: pins.providerReceipt.configMapName,
        sourceCommit: pins.providerReceipt.sourceCommit,
      },
    });

    const invocationId = await getOrCreateInvocationId(
      outputPath, invocationBinding(validated, surface, nativeProbeResults));
    const setupToken = env.AGENTWEAVER_P1_SETUP_TOKEN;
    if (typeof setupToken !== 'string' || !setupToken) {
      throw new Error('The setup token is unavailable; set AGENTWEAVER_P1_SETUP_TOKEN after source evidence is ready.');
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
        });
        result = await runSurfaceJourney(surface, fixture.input, {
          ...adapters,
          sourceEvidence,
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
        result = createFailureResult(surface, error, sourceEvidence, nativeProbeResults);
      }
      try {
        cleanup = await fixture.cleanup();
      } catch (error) {
        cleanupError = error;
      }
      result = addFixtureCleanup(result, fixture.state, cleanup, nativeFence, cleanupError);
    } finally {
      if (!result) {
        result = createFailureResult(surface,
          new Error('P1 fixture setup did not produce an acceptance result.'),
          sourceEvidence, nativeProbeResults);
      }
    }
  } catch (error) {
    if (!['api', 'mcp', 'ui'].includes(surface)) surface = 'api';
    result = createFailureResult(surface, error, sourceEvidence, nativeProbeResults);
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
  const exitCode = await runP1Cli();
  process.exitCode = exitCode;
}
