import { keyedLocator } from './browser.mjs';
import { dragOptionsFromArgs, performPointerDrag } from './drag.mjs';
import { captureTurn } from './evidence.mjs';
import { DEFAULT_READINESS_TIMEOUT_MS, waitForAppReadiness } from './readiness.mjs';

function readinessTarget(args) {
  if (args['ready-test-id']) return { testId: args['ready-test-id'] };
  if (args['ready-role'] || args['ready-name']) {
    if (!args['ready-role'] || !args['ready-name']) {
      throw new Error('--ready-role and --ready-name must be provided together');
    }
    return { role: args['ready-role'], name: args['ready-name'] };
  }
  return null;
}

export function readinessOptions(args) {
  const timeout = Number(args['readiness-timeout'] ?? DEFAULT_READINESS_TIMEOUT_MS);
  if (!Number.isFinite(timeout) || timeout < 0) {
    throw new Error('--readiness-timeout must be a non-negative number');
  }
  return { timeout, target: readinessTarget(args) };
}

export async function navigateForAppEvidence(runtime, destination, options) {
  await runtime.goto(destination);
  return waitForAppReadiness(runtime.page, options);
}

export function approvalInScope(adapterText, gate) {
  const declared = /allow approval:\s*([a-z0-9_-]+)/i.exec(adapterText ?? '')?.[1];
  return declared === String(gate?.type ?? '').toLowerCase() && gate?.safe === true;
}

export function assertApprovalAllowed({ adapterText, decision, gate }) {
  if (decision !== 'approve') return;
  if (!approvalInScope(adapterText, gate)) {
    throw new Error(`refusing out-of-scope approve for ${gate?.type ?? 'unknown'} gate`);
  }
}

export async function executeUiAction({
  runtime,
  capture,
  session,
  args,
  eventId,
  transcriptDirectory,
}) {
  const command = args._[0];
  let readiness = null;
  let target = { testId: args['test-id'], role: args.role, name: args.name };

  try {
    if (command === 'goto') {
      readiness = await navigateForAppEvidence(runtime, args.path ?? '/', readinessOptions(args));
    } else if (command === 'click') {
      await keyedLocator(runtime.page, {
        testId: args['test-id'],
        role: args.role,
        name: args.name,
      }).click({ timeout: Number(args.timeout ?? 10_000) });
    } else if (command === 'type-coordinator') {
      await keyedLocator(runtime.page, {
        testId: args['test-id'] ?? 'coordinator-composer',
        role: args.role,
        name: args.name,
      }).fill(args.text ?? '');
    } else if (command === 'drag') {
      const fromTestId = args['from-test-id'];
      const toTestId = args['to-test-id'];
      if (typeof fromTestId !== 'string' || typeof toTestId !== 'string') {
        throw new Error('drag requires --from-test-id and --to-test-id');
      }
      const dragOptions = dragOptionsFromArgs(args);
      target = {
        from: { testId: fromTestId, offset: dragOptions.sourceOffset },
        to: { testId: toTestId, offset: dragOptions.targetOffset },
        steps: dragOptions.steps,
      };
      await performPointerDrag({
        page: runtime.page,
        source: keyedLocator(runtime.page, { testId: fromTestId }),
        target: keyedLocator(runtime.page, { testId: toTestId }),
        ...dragOptions,
      });
    } else if (command === 'resolve-approval') {
      assertApprovalAllowed({
        adapterText: session.persona.text,
        decision: args.decision ?? 'defer',
        gate: { type: args['gate-type'], safe: true },
      });
      await keyedLocator(runtime.page, {
        testId: args['test-id'],
        role: args.role,
        name: args.name,
      }).click({ timeout: Number(args.timeout ?? 10_000) });
    } else if (command === 'capture') {
      if (args.path) await runtime.goto(args.path);
      readiness = await waitForAppReadiness(runtime.page, readinessOptions(args));
    } else {
      throw new Error(`unsupported command "${command}"`);
    }
  } catch (error) {
    const failure = error instanceof Error ? error : new Error(String(error));
    if (command === 'drag') {
      failure.evidenceStep = await captureTurn({
        page: runtime.page,
        capture,
        directory: transcriptDirectory,
        id: eventId,
        intent: args.thought ?? null,
        action: command,
        target,
        outcome: 'failed',
        error: { message: failure.message },
        frustrationSignals: ['action-failed'],
      });
    }
    throw failure;
  }

  return captureTurn({
    page: runtime.page,
    capture,
    directory: transcriptDirectory,
    id: eventId,
    intent: args.thought ?? null,
    action: command,
    target,
    outcome: 'succeeded',
    readiness,
  });
}
