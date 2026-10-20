import { pathToFileURL } from 'node:url';
import { extname, resolve } from 'node:path';
import {
  authorizeP1InBrowser,
  P1_RUN_SCOPES,
  P1_SETUP_SCOPES,
  P1_SIGNIN_PROFILE_ROOT,
  readP1SignInInput,
  validateP1BrokerToken,
  withFreshP1BrowserContext,
} from './p1-signin.mjs';
import { runP1Cli } from './run-p1.mjs';

const OPTION_NAMES = new Set([
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
    if (option === '--identity-module') {
      throw new Error('The sign-in launcher supplies its built-in identity provider; --identity-module is not accepted.');
    }
    if (!OPTION_NAMES.has(option)
      || typeof argv[index + 1] !== 'string'
      || argv[index + 1].startsWith('--')) {
      throw new Error('Use --surface api|mcp|api,mcp|ui --input <manifest.json> --output <evidence.jsonl> [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>].');
    }
    const key = option.slice(2).replaceAll('-', '');
    if (result[key] !== undefined) throw new Error(`The ${option} option can only be used once.`);
    result[key] = argv[++index];
  }
  return result;
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
  signIn = authorizeP1InBrowser,
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
    io.log?.('Usage: npm run harness:p1:signin-run -- --surface api|mcp|api,mcp|ui --input <manifest.json> --output <evidence.jsonl> [--native-probe-envelope <native.json>] [--dispatch-recovery-envelope <recovery.json>]');
    io.log?.('The launcher signs in with the registered agentweaver-web client. Broker tokens stay in memory and are not written to evidence.');
    io.log?.('Multiple surfaces run sequentially with separate evidence files: evidence.api.jsonl and evidence.mcp.jsonl for --output evidence.jsonl. All surfaces run even if one returns a blocked or failed result.');
    return 0;
  }
  const surfaces = options.surface?.split(',') ?? [];
  if (surfaces.length === 0 || new Set(surfaces).size !== surfaces.length
    || surfaces.some(surface => !['api', 'mcp', 'ui'].includes(surface))
    || (surfaces.includes('ui') && surfaces.length > 1)
    || !options.input || !options.output) {
    io.error?.('Select api, mcp, api,mcp, or ui and supply an input manifest and evidence output path.');
    return 2;
  }
  if (options.surface === 'ui') {
    return runP1(argv, env, io);
  }

  let setupToken;
  try {
    const validated = await readP1SignInInput(argv);
    if (Object.hasOwn(validated.fixturePlan, 'deniedActor')) {
      io.error?.('This launcher uses one Broker account only. Omit fixturePlan.deniedActor; distinct-actor coverage remains unavailable without a second account.');
      return 2;
    }
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
        const result = await runP1(surfaceArgv, {
          ...env,
          AGENTWEAVER_P1_SETUP_TOKEN: setupToken,
        }, io, { identityProvider });
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
