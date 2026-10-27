import { open, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createP1NativeProcessProvider } from './process-provider.mjs';
import { runP1NativeProbes } from './run.mjs';

const USAGE = 'node scripts\\azure\\p1-native-probes\\cli.mjs --execute --input <native-input.json> --provider-module <owner-providers.mjs> --output <new-native-envelope.json>';
const PROVIDERS = Object.freeze({
  authProvider: 'getValidatedToken',
  journeyAuthorizer: 'authorize',
  runtimeReadinessProvider: 'readCurrent',
});

function parseArguments(argv) {
  if (argv.length === 1 && argv[0] === '--help') return { help: true };
  const options = {};
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (Object.hasOwn(options, name)) throw new Error('duplicate_option');
    if (name === '--execute') {
      options[name] = true;
    } else if (['--input', '--provider-module', '--output'].includes(name)
      && typeof argv[index + 1] === 'string'
      && argv[index + 1].trim() && !argv[index + 1].startsWith('--')) {
      options[name] = argv[++index];
    } else {
      throw new Error('invalid_option');
    }
  }
  if (!options['--execute'] || !options['--input']
    || !options['--provider-module'] || !options['--output']) {
    throw new Error('explicit_execution_and_paths_required');
  }
  return options;
}

/** Load trusted owner adapters without permitting replacement of the fixed process or HTTP transport. */
export async function loadP1NativeProviders(input, modulePath) {
  const processProvider = createP1NativeProcessProvider({
    kubeContext: input?.pins?.context?.kubeContext,
    clusterIdentity: input?.pins?.context?.clusterIdentity,
  });
  const moduleUrl = pathToFileURL(resolve(modulePath));
  if (!moduleUrl.pathname.endsWith('.mjs')) throw new Error('provider_module_must_be_mjs');
  const module = await import(moduleUrl.href);
  if (typeof module.createP1NativeProviders !== 'function') {
    throw new Error('provider_factory_required');
  }
  // The factory receives a copy so it cannot rewrite the runner's evidence pins.
  const providers = await module.createP1NativeProviders({ input: structuredClone(input) });
  if (!providers || typeof providers !== 'object' || Array.isArray(providers)
    || Object.keys(providers).length !== Object.keys(PROVIDERS).length
    || Object.entries(PROVIDERS).some(([name, method]) =>
      !Object.hasOwn(providers, name) || typeof providers[name]?.[method] !== 'function')) {
    throw new Error('exact_owner_providers_required');
  }
  return { ...providers, fetch: globalThis.fetch, processProvider };
}

/** Execute once and preserve the runner's sealed, redacted envelope without rewriting its evidence. */
export async function runP1NativeProbeCli(argv, { stdout = process.stdout, stderr = process.stderr } = {}) {
  let output;
  let serialized;
  let stage = 'arguments';
  try {
    const options = parseArguments(argv);
    if (options.help) {
      stdout.write(`${USAGE}\n`);
      return 0;
    }
    stage = 'input_read';
    const input = JSON.parse(await readFile(resolve(options['--input']), 'utf8'));
    stage = 'output_reservation';
    output = await open(resolve(options['--output']), 'wx', 0o600);
    stage = 'provider_load';
    const dependencies = await loadP1NativeProviders(input, options['--provider-module']);
    stage = 'native_runner';
    const result = await runP1NativeProbes(input, dependencies);
    serialized = `${JSON.stringify(result, null, 2)}\n`;
    stage = 'output_write';
    await output.writeFile(serialized, 'utf8');
    await output.sync();
    await output.close();
    output = undefined;
    stdout.write(`P1 native probes: ${result.overallStatus}.\n`);
    return { passed: 0, failed: 1, blocked: 2 }[result.overallStatus];
  } catch {
    // Provider and filesystem errors can contain credentials or raw owner responses.
    stderr.write(`P1 native CLI blocked at ${stage}; no acceptance pass is claimed.\n`);
    if (stage === 'arguments') stderr.write(`${USAGE}\n`);
    if (serialized) {
      stderr.write('Envelope persistence is uncertain; preserve the envelope below and do not rerun mutations.\n');
      stdout.write(serialized);
    } else if (stage === 'native_runner') {
      stderr.write('The runner did not return an envelope; owner effects and cleanup require review before any retry.\n');
    }
    return 2;
  } finally {
    if (output) {
      try {
        await output.close();
      } catch {
        stderr.write('P1 native CLI could not close the reserved output file; inspect it before reuse.\n');
      }
    }
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = await runP1NativeProbeCli(process.argv.slice(2));
}
