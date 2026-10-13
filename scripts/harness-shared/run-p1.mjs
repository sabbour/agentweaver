import { pathToFileURL } from 'node:url';
import { dirname, isAbsolute, resolve } from 'node:path';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createP1AcceptanceResult, persistP1AcceptanceResult } from './p1-acceptance-result.mjs';
import { prepareP1DeploymentEvidence } from './p1-deployment-evidence.mjs';
import { validateP1JourneyInput } from './p1-journey-input.mjs';
import { sanitizeP1Failure } from './p1-surface-common.mjs';
import { redact } from './redaction.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

function parseArguments(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === '--help') {
      result.help = true;
      continue;
    }
    if (!['--surface', '--input', '--output', '--page-module'].includes(name)
      || typeof argv[index + 1] !== 'string' || argv[index + 1].startsWith('--')) {
      throw new Error('Use --surface api|mcp|ui --input <manifest.json> --output <evidence.jsonl> [--page-module <module.mjs>].');
    }
    const key = name === '--page-module' ? 'pageModule' : name.slice(2);
    if (result[key] !== undefined) throw new Error(`The ${name} option can only be used once.`);
    result[key] = argv[++index];
  }
  return result;
}

function createFailureResult(surface, error, sourceEvidence) {
  return createP1AcceptanceResult({
    surface,
    surfaceStatus: 'blocked',
    sourceEvidence,
    observations: {
      cleanup: { intent: 'none', status: 'not-started' },
      adapterCleanup: { mcp: 'not-started', ui: surface === 'ui' ? 'not-started' : 'not-required' },
    },
    failure: sanitizeP1Failure(error),
  });
}

export async function runP1Cli(argv = process.argv.slice(2), env = process.env, io = console, {
  prepareDeploymentEvidence = prepareP1DeploymentEvidence,
} = {}) {
  let options;
  let surface = 'api';
  let outputPath;
  let inputPath;
  let result;
  let sourceEvidence;
  if (argv.includes('--help')) {
    io.log('Usage: node scripts/harness-shared/run-p1.mjs --surface api|mcp|ui --input <manifest.json> --output <evidence.jsonl> [--page-module <authorized-page-provider.mjs>]');
    io.log('This CLI currently returns a blocked result before reading credentials or opening a surface adapter.');
    return 0;
  }

  try {
    options = parseArguments(argv);
    if (!['api', 'mcp', 'ui'].includes(options.surface) || !options.input || !options.output) {
      throw new Error('A P1 surface, input manifest, and evidence output path are required.');
    }
    surface = options.surface;
    if (surface === 'ui' && !options.pageModule) {
      throw new Error('The UI surface requires an already-authorized page provider module.');
    }
    inputPath = resolve(options.input);
    const requestedOutputPath = resolve(options.output);
    if (inputPath === requestedOutputPath) throw new Error('The evidence output path must not replace the input manifest.');
    outputPath = requestedOutputPath;

    let input;
    try {
      input = JSON.parse(await readFile(inputPath, 'utf8'));
    } catch {
      throw new Error('The P1 input manifest is missing or malformed JSON.');
    }
    let validated;
    try {
      validated = validateP1JourneyInput(input);
    } catch (error) {
      throw new Error(`P1 preflight blocked before network activity: ${error.message}`);
    }
    const { pins, fixture, deploymentEvidence } = validated;
    if (!fixture.allowedJourneys.includes(input.journey)) {
      throw new Error('The selected journey is not authorized by its invocation-owned fixture.');
    }
    const resolveRepositoryPath = file => resolve(isAbsolute(file) ? file : resolve(repositoryRoot, file));
    const publicationPath = resolveRepositoryPath(deploymentEvidence.publicationPath);
    const manifestPath = resolveRepositoryPath(deploymentEvidence.manifestPath);
    const agentHostArchivePath = resolveRepositoryPath(deploymentEvidence.agentHostArchivePath);
    if ([inputPath, publicationPath, manifestPath, agentHostArchivePath].includes(requestedOutputPath)) {
      outputPath = undefined;
      throw new Error('The evidence output path must not replace a P1 input or deployment artifact.');
    }
    sourceEvidence = await prepareDeploymentEvidence({
      repoRoot: repositoryRoot,
      expectedSourceSha: pins.sourceReceipt.sourceSha,
      context: pins.context,
      namespace: pins.namespace,
      publicationPath,
      manifestPath,
      agentHostArchivePath,
      providerCatalog: {
        configMapName: pins.providerReceipt.configMapName,
        sourceCommit: pins.providerReceipt.sourceCommit,
      },
    });
    throw new Error('P1 acceptance is blocked before API, MCP, or UI access: caller-supplied ownership receipts do not prove that this invocation created the fixture, and no supported same-invocation setup and cleanup path is available.');
  } catch (error) {
    if (!['api', 'mcp', 'ui'].includes(surface)) surface = 'api';
    result = createFailureResult(surface, error, sourceEvidence);
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
