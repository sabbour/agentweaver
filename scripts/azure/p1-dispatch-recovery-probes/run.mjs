/**
 * Reads source-owned P1 dispatch and recovery receipts without creating runtime or platform resources.
 * A probe stays blocked until every required live receipt has a supported owner producer.
 */
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runP1NativeCreditDenialReaders } from '../p1-native-probes/run.mjs';
import { sealP1NativeProbeEnvelope } from '../../harness-shared/p1-acceptance-result.mjs';
import { redact } from '../../harness-shared/redaction.mjs';
import { readNumericDenials, validateNumericDenialInput } from './numeric-denials.mjs';

const sourceOnlyEvidence = (requiredReceipts, sourceGates = []) => ({
  kind: 'source-prerequisite-blocker',
  observed: false,
  acceptanceEvidence: false,
  observations: [],
  requiredReceipts,
  sourceGates,
});

const liveObservationEvidence = (observations, requiredReceipts, sourceGates = []) => ({
  ...(observations.length > 0
    ? {
      kind: 'p1-live-runtime-observations',
      observed: true,
      acceptanceEvidence: false,
      observations,
      requiredReceipts,
      sourceGates,
    }
    : sourceOnlyEvidence(requiredReceipts, sourceGates)),
});

const blockedProbe = (name, reason, evidence) => ({
  name,
  status: 'blocked',
  reason,
  evidence,
  cleanup: {
    status: 'not-started',
    createdResourceIds: [],
  },
});

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function positive(value) {
  return Number.isSafeInteger(value) && value > 0;
}

function containsCredentials(value) {
  if (Array.isArray(value)) return value.some(containsCredentials);
  return isRecord(value) && Object.entries(value).some(([key, item]) =>
    /^(?:(?:bearer|access|refresh|id)[_-]?token|token|authorization|password|client[_-]?secret|secret)$/i.test(key) ||
    containsCredentials(item));
}

/** Require a non-zero GUID for runtime and grant identities. */
function nonEmptyGuid(value) {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value) &&
    value.toLowerCase() !== '00000000-0000-0000-0000-000000000000';
}

function summarizeOwnerObservation(runtime, environment) {
  const execution = runtime.execution.value;
  const guard = runtime.creditGuard;
  return {
    kind: 'read-only-owner-observation',
    acceptanceEvidence: false,
    raceObserved: false,
    cleanupVerified: false,
    runtime: {
      registration: runtime.registration.value,
      execution: execution.observation,
      checkpointParent: execution.checkpointParent,
      creditGuard: guard.state === 'Available' ? guard.value : { state: guard.state },
      dispatches: {
        count: runtime.dispatches.value.items.length,
        truncated: runtime.dispatches.value.truncated,
      },
      toolReservations: {
        count: runtime.toolReservations.value.items.length,
        truncated: runtime.toolReservations.value.truncated,
      },
    },
    environment: {
      environmentId: environment.environmentId,
      fence: environment.fence,
      providerAttempts: {
        count: environment.providerAttempts.attempts.length,
        states: environment.providerAttempts.attempts.map(attempt => attempt.state),
      },
      lateResourceCleanups: {
        count: environment.lateResourceCleanups.items.length,
        hasMore: environment.lateResourceCleanups.hasMore,
      },
    },
  };
}

function summarizeCreditDenialObservation(result) {
  return {
    status: result.overallStatus,
    cases: Object.fromEntries(['fixedZero', 'positive'].map(name => {
      const item = result.cases?.[name] ?? { status: 'blocked', code: 'credit_receipt_missing' };
      return [name, {
        status: item.status,
        ...(item.code ? { code: item.code } : {}),
        ...(item.reason ? { reason: item.reason } : {}),
        ...(item.evidence ? { evidence: item.evidence } : {}),
      }];
    })),
  };
}

/** Return the stable SDK session ID bound to a registration's tenant and session owner. */
function expectedNativeSessionId(binding) {
  const scope = [binding.tenantId, binding.projectId, binding.runId, binding.sessionId].join('\0');
  return `agentweaver-session-${createHash('sha256').update(scope, 'utf8').digest('hex')}`;
}

/** Check that cache evidence belongs to this registration and its exact SDK source. */
function validNativeCacheAcknowledgment(acknowledgment, binding, registration, source) {
  const material = acknowledgment?.reference?.material;
  const identity = acknowledgment?.identity;
  const kind = String(material?.kind).replaceAll(/[-_]/g, '').toLowerCase();
  const sameOptional = (left, right) => (left ?? null) === (right ?? null);
  return isRecord(acknowledgment) &&
    acknowledgment.contractVersion === 1 &&
    isRecord(identity) &&
    identity.projectId === binding.projectId &&
    identity.runId === binding.runId &&
    identity.sessionId === binding.sessionId &&
    nonEmptyGuid(acknowledgment.eventId) &&
    positive(acknowledgment.position) &&
    isRecord(acknowledgment.reference) &&
    acknowledgment.reference.purpose === 'runtime-sdk-cache' &&
    positive(acknowledgment.reference.byteLength) &&
    acknowledgment.reference.byteLength <= 1024 * 1024 &&
    isRecord(material) &&
    (kind === 'sdkcache' || material.kind === 1) &&
    material.contractVersion === 1 &&
    /^[a-f0-9]{64}$/i.test(material.sha256 ?? '') &&
    material.tenantId === binding.tenantId &&
    material.runtimeInstanceId === registration.runtimeInstanceId &&
    material.registrationRevision === registration.revision &&
    material.executionFence === binding.executionFence &&
    material.acceptedSelectionHash === binding.acceptedSelectionHash &&
    material.sdkVersion === source.sdkVersion &&
    material.runtimeVersion === source.runtimeVersion &&
    material.modelSelectionReference === source.modelSelectionReference &&
    material.modelId === source.modelId &&
    sameOptional(material.maxPromptTokens, source.maxPromptTokens) &&
    sameOptional(material.reasoningEffort, source.reasoningEffort);
}

/** Validate current owner, placement, source, and recovery receipts before summarizing them. */
function summarizeRecoveryReceipt(readback, context) {
  const registration = readback?.registration;
  const readiness = readback?.readinessReceipt;
  const recovery = readiness?.recovery;
  const binding = registration?.binding;
  const owner = context?.owner;
  const placement = context?.placement;
  const expectedPodUid = context?.expectedPodUid;
  const expectedRuntimeInstanceId = context?.expectedRuntimeInstanceId;
  const previousPodUid = context?.previousPodUid;
  const previousRuntimeInstanceId = context?.previousRuntimeInstanceId;
  const placementObservation = readback?.placementObservation;
  const observedResource = placementObservation?.resource;
  const observedPod = placementObservation?.pod;
  const registrationExpiry = Date.parse(registration?.expiresAt);
  const now = Date.now();
  const source = recovery?.source;
  const recoveryMode = recovery?.recoveryMode;
  const identityKeys = ['actorId', 'tenantId', 'projectId', 'runId', 'sessionId', 'environmentId'];
  const expectedSessionId = isRecord(binding) ? expectedNativeSessionId(binding) : '';
  const sourceGrant = readiness?.sourceGrant;
  const grantExpiry = Date.parse(sourceGrant?.expiresAt);
  if (!isRecord(owner) || !isRecord(registration) || !isRecord(binding) || !isRecord(placement?.resource) ||
      !isRecord(placementObservation) || !isRecord(observedResource) || !isRecord(observedPod) ||
      !isRecord(readiness) || !isRecord(recovery) || !isRecord(source) ||
      String(registration.state).toLowerCase() !== 'active' ||
      !positive(registration.revision) || !Number.isFinite(registrationExpiry) || registrationExpiry <= now ||
      identityKeys.some(key => typeof owner[key] !== 'string' || !owner[key] ||
        binding[key] !== owner[key]) ||
      !positive(owner.executionFence) || binding.executionFence !== owner.executionFence ||
      !/^[a-f0-9]{64}$/i.test(binding.acceptedSelectionHash ?? '') ||
      typeof expectedPodUid !== 'string' || !expectedPodUid ||
      typeof previousPodUid !== 'string' || !previousPodUid || expectedPodUid === previousPodUid ||
      observedResource.resourceId !== placement.resource.resourceId ||
      observedResource.generation !== placement.resource.generation ||
      observedPod.uid !== expectedPodUid ||
      binding.placementUid !== placement.resource.resourceId ||
      !positive(placement.resource.generation) ||
      binding.placementGeneration !== placement.resource.generation ||
      !nonEmptyGuid(expectedRuntimeInstanceId) ||
      !nonEmptyGuid(previousRuntimeInstanceId) ||
      registration.runtimeInstanceId !== expectedRuntimeInstanceId ||
      registration.runtimeInstanceId !== readiness.runtimeInstanceId ||
      expectedRuntimeInstanceId === previousRuntimeInstanceId ||
      readiness.contractVersion !== 1 ||
      readiness.runtimeInstanceId !== expectedRuntimeInstanceId ||
      readiness.registrationRevision !== registration.revision ||
      readiness.executionFence !== owner.executionFence ||
      !isRecord(sourceGrant) ||
      sourceGrant.runtimeInstanceId !== registration.runtimeInstanceId ||
      sourceGrant.registrationRevision !== registration.revision ||
      !nonEmptyGuid(sourceGrant.grantId) ||
      !positive(sourceGrant.revision) ||
      String(sourceGrant.purpose).toLowerCase() !== 'observe' ||
      String(sourceGrant.state).toLowerCase() !== 'active' ||
      sourceGrant.audience !== binding.observationEndpoint ||
      typeof sourceGrant.issuer !== 'string' || !sourceGrant.issuer ||
      !/^[a-f0-9]{64}$/i.test(sourceGrant.configurationHash ?? '') ||
      !Number.isFinite(grantExpiry) || grantExpiry <= now || grantExpiry > registrationExpiry ||
      !Number.isFinite(Date.parse(sourceGrant.recordedAt)) ||
      recovery.contractVersion !== 1 ||
      recovery.runtimeInstanceId !== expectedRuntimeInstanceId ||
      recovery.registrationRevision !== registration.revision ||
      recovery.executionFence !== owner.executionFence ||
      !['NativeCache', 'JournalRebuild'].includes(recoveryMode) ||
      source.runtimeInstanceId !== registration.runtimeInstanceId ||
      source.registrationRevision !== registration.revision ||
      source.sdkSessionId !== expectedSessionId ||
      source.acceptedSelectionHash !== binding.acceptedSelectionHash ||
      source.modelSelectionReference !== binding.modelSelectionReference ||
      typeof source.sdkVersion !== 'string' || !source.sdkVersion ||
      typeof source.runtimeVersion !== 'string' || !source.runtimeVersion ||
      typeof source.modelId !== 'string' || !source.modelId) {
    throw new Error('The current replacement Host recovery receipt does not match the run-bound owner and placement.');
  }

  const result = {
    owner: {
      projectId: owner.projectId,
      runId: owner.runId,
      sessionId: owner.sessionId,
      executionFence: owner.executionFence,
    },
    previousPodUid,
    replacementPodUid: expectedPodUid,
    placementResourceId: placement.resource.resourceId,
    placementGeneration: placement.resource.generation,
    previousRuntimeInstanceId,
    runtimeInstanceId: expectedRuntimeInstanceId,
    registrationRevision: registration.revision,
    recoveryMode,
    sourceSessionSha256: createHash('sha256').update(source.sdkSessionId, 'utf8').digest('hex'),
  };
  if (recoveryMode === 'NativeCache') {
    const acknowledgment = recovery.nativeCacheAcknowledgment;
    if (!validNativeCacheAcknowledgment(acknowledgment, binding, registration, source) ||
        recovery.journal != null) {
      throw new Error('The replacement Host cache recovery acknowledgment is missing or invalid.');
    }
    result.nativeCacheAcknowledgment = {
      eventId: acknowledgment.eventId,
      position: acknowledgment.position,
    };
  } else {
    const journal = recovery.journal;
    if (!isRecord(journal) || !positive(journal.throughPosition) ||
        !/^[a-f0-9]{64}$/i.test(journal.journalSha256 ?? '') ||
        recovery.nativeCacheAcknowledgment != null) {
      throw new Error('The replacement Host journal recovery receipt is missing or invalid.');
    }
    result.journal = {
      throughPosition: journal.throughPosition,
      journalSha256: journal.journalSha256,
    };
  }
  return result;
}

/**
 * Reads current owner, native credit-denial, and replacement readiness evidence.
 * It never dispatches a turn or provisions a replacement, and missing source receipts stay blocked.
 */
export async function runP1DispatchRecoveryProbes({
  observationClient,
  observationContext,
  creditDenialInput,
  creditDenialDependencies,
  numericDenialInput,
  numericDenialDependencies,
  runtimeReadinessProvider,
  recoveryContext,
} = {}) {
  const startedAt = new Date().toISOString();
  let ownerObservation = { status: 'not-requested' };
  if (observationClient !== undefined || observationContext !== undefined) {
    try {
      if (typeof observationClient?.readRuntimeObservationV1 !== 'function'
        || typeof observationClient?.readEnvironmentEvidence !== 'function'
        || !observationContext) {
        throw new Error('A runtime observation client and complete owner context are required.');
      }
      const runtime = await observationClient.readRuntimeObservationV1(observationContext.owner);
      const environment = await observationClient.readEnvironmentEvidence({
        environmentCreation: observationContext.environmentCreation,
        owner: observationContext.owner,
        operationId: observationContext.operationId,
      });
      ownerObservation = {
        status: 'observed',
        value: summarizeOwnerObservation(runtime, environment),
      };
    } catch (error) {
      ownerObservation = {
        status: 'unavailable',
        reason: redact(error instanceof Error ? error.message : String(error)),
      };
    }
  }

  let creditDenialObservation = { status: 'not-requested' };
  if (creditDenialInput !== undefined || creditDenialDependencies !== undefined) {
    try {
      creditDenialObservation = summarizeCreditDenialObservation(
        await runP1NativeCreditDenialReaders(creditDenialInput, creditDenialDependencies));
    } catch (error) {
      creditDenialObservation = {
        status: 'unavailable',
        reason: redact(error instanceof Error ? error.message : String(error)),
      };
    }
  }

  let recoveryObservation = { status: 'not-requested' };
  let numericDenialObservation = { status: 'not-requested' };
  if (numericDenialInput !== undefined || numericDenialDependencies !== undefined) {
    try {
      numericDenialObservation = await readNumericDenials(numericDenialInput, numericDenialDependencies);
    } catch {
      numericDenialObservation = {
        status: 'blocked',
        code: 'numeric_denial_input_invalid',
        reason: 'Complete numeric denial lookup pins and an authorized Orchestrator reader are required.',
      };
    }
  }
  if (runtimeReadinessProvider !== undefined || recoveryContext !== undefined) {
    try {
      if (typeof runtimeReadinessProvider?.readCurrent !== 'function' || !recoveryContext) {
        throw new Error('A current replacement placement and RuntimeHost readiness reader are required.');
      }
      const readback = await runtimeReadinessProvider.readCurrent({
        owner: recoveryContext.owner,
        placement: recoveryContext.placement,
        expectedPodUid: recoveryContext.expectedPodUid,
        expectedRuntimeInstanceId: recoveryContext.expectedRuntimeInstanceId,
      });
      recoveryObservation = {
        status: 'observed',
        value: summarizeRecoveryReceipt(readback, recoveryContext),
      };
    } catch (error) {
      recoveryObservation = {
        status: 'unavailable',
        reason: redact(error instanceof Error ? error.message : String(error)),
      };
    }
  }

  const numericObservations = [];
  if (numericDenialObservation.status === 'partial') {
    numericObservations.push({
      kind: 'partial-orchestrator-numeric-denial-read-results',
      cases: numericDenialObservation.cases,
    });
  }
  if (ownerObservation.status === 'observed') {
    numericObservations.push({ kind: 'read-only-owner-observation', value: ownerObservation.value });
  }
  if (Object.values(creditDenialObservation.cases ?? {}).some(item => item.status === 'passed')) {
    // Preserve each validated receipt when its sibling case is blocked.
    numericObservations.push({
      kind: creditDenialObservation.status === 'passed'
        ? 'validated-host-credit-denial-receipts'
        : 'partial-host-credit-denial-read-results',
      status: creditDenialObservation.status,
      cases: creditDenialObservation.cases,
    });
  }

  const probes = [
    blockedProbe(
      'numeric-copilot-dispatch-boundaries',
      'The existing Host reader can verify persisted zero-limit and hard-credit no-send receipts. The Orchestrator reader can verify persisted MaxModelTurns and MaxToolCalls denial and unchanged owner send-state receipts, but these do not prove native cursor/no-send, admitted-turn completion, or whole-run soft-warning/floor advance. MaxPromptTokens is rejected before SDK setup. Missing native-boundary, warning, and cursor-linked dispatch receipts keep this check blocked.',
      liveObservationEvidence(
        numericObservations,
        [
          {
            owner: 'Projects & Config',
            receipt: 'accepted-run-selection',
            requiredFacts: [
              'exact project, run, root and child identities',
              'accepted selection hash, configured limits and current run/session fence',
            ],
          },
          {
            owner: 'Orchestrator',
            receipt: 'prepared-dispatch-and-denial-observation',
            requiredFacts: [
              'current registration, source grant, checkpoint ID/parent/revision and dispatch intent',
              'exact attempted message ID and request hash bound to the current owner denial',
              'stored dispatch state and owner revision before and after denial, proving no send transition',
              'current native event/cursor observation bounded to this attempt; a missing journal entry alone is insufficient',
            ],
          },
          {
            owner: 'Events',
            receipt: 'priced-usage-and-credit-guard',
            requiredFacts: [
              'actual priced usage receipt and cost identity for prior admitted native turns',
              'whole-run priced Copilot total across children, observed ledger count/digest/rate identity, and current floor',
              'current snapshot/guard revision and exhaustion state; reject partial or unpriced observations',
              'one durable soft-limit warning at the first supported observed-cost boundary, without duplicates on replay or concurrent attempts',
              'later priced totals advance the observed floor and ledger-event count',
            ],
          },
          {
            owner: 'AgentHost and Events',
            receipt: 'native-turn-observations',
            requiredFacts: [
              'actual native message ID, turn ID, event range, completion/output receipt and usage references for admitted sends',
              'bounded native observation before and after the denied attempt, correlated to its stable platform ID',
            ],
          },
        ],
        [
          {
            path: 'packages/Agentweaver.Identity/RuntimeContractValidation.cs',
            symbol: 'RequireSupportedNumericRunLimits',
            code: 'runtime_numeric_limits_unsupported',
            detail: 'Workflow-bound MaxToolCalls uses owner-prepared dispatch and begin admission. MaxPromptTokens remains unsupported before SDK setup; unbound MaxModelTurns and MaxToolCalls are rejected.',
          },
          {
            path: 'packages/Agentweaver.AgentRuntime/AuthorizedRuntimeSession.cs',
            symbol: 'RequireSupportedCostTurnAffinity',
            code: 'runtime_usage_affinity_unavailable',
          },
          {
            path: 'services/orchestrator/Agentweaver.Orchestrator/RuntimeUsageSourceStore.cs',
            symbol: 'ReconcileNativeCreditLimitsAsync',
            detail: 'The owner path denies the next native turn at or above the observed whole-run priced floor under the dispatch lock. It queues one durable soft-limit warning; later usage updates the next snapshot and does not revoke an admitted turn, so this is not an all-usage-final claim.',
          },
        ],
      ),
    ),
    blockedProbe(
      'checkpoint-restart-duplicate-fence',
      recoveryObservation.status === 'observed'
        ? 'A current replacement Host recovery receipt was read and validated, but no source-owned producer returns the before/after gate-join checkpoint, dispatch result, native cursor, and external-effect receipts needed to prove no duplicate execution. This P1 check remains blocked; P2 suspend/resume is excluded.'
        : 'The ordinary replacement placement and Host recovery sources exist, but no live replacement recovery receipt or before/after gate-join and no-duplicate receipts were read. The P2 manifest suspend/resume path is excluded and cannot substitute for ordinary replacement recovery.',
      liveObservationEvidence(
        recoveryObservation.status === 'observed'
          ? [{ kind: 'replacement-host-recovery-receipt', value: recoveryObservation.value }]
          : [],
        [
          {
            owner: 'Projects & Config, Orchestrator, and MAF owner',
            receipt: 'invocation-owned-gate-join-and-checkpoint',
            requiredFacts: [
              'exact project/run/root/child, actor and accepted selection identities with the target gate/join state',
              'current logical owner and Core execution fence plus actual MAF checkpoint ID, parent, revision and work-plan state',
              'stable dispatch identity and actual pre-replacement dispatch state/result/output',
            ],
          },
          {
            owner: 'Environment and Sandbox provider',
            receipt: 'ordinary-replacement-placement',
            requiredFacts: [
              'actual old lease/resource/Pod and new Sandbox resource/Pod identities from ordinary abandon, reconciliation and reprovision',
              'current Environment/provider fences and lease revisions, preserved Core execution fence, and exact retained workspace generation',
              'actual replacement Host endpoint checked against the receiver expected audience and certificate DNS name',
              'owner-approved bootstrap profile mapped to the exact replacement ProviderResourceRef',
            ],
          },
          {
            owner: 'Orchestrator, Environment, and Identity',
            receipt: 'ordinary-fresh-registration-and-bootstrap',
            requiredFacts: [
              'new RuntimeInstanceId and registration derived from exact current ordinary placement, same logical root/child and accepted selection',
              'actual fresh bootstrap delivery, consume and exchange receipts with the current Observe/source grant',
              'stale old registration and grant denied; no root/gate fence reset merely to create a replacement',
            ],
          },
          {
            owner: 'AgentHost and Events',
            receipt: 'ordinary-runtime-recovery-observation',
            requiredFacts: [
              'actual new Host process and RuntimeSessionRecovery mode (native cache or authenticated journal rebuild)',
              'actual native cache acknowledgment/consumption or journal checkpoint/cursor; not source compatibility alone',
              'current readiness/source-grant evidence tied to the replacement Host and placement',
            ],
          },
          {
            owner: 'Orchestrator, AgentHost, and Events',
            receipt: 'gate-join-result-and-no-duplicate',
            requiredFacts: [
              'same logical checkpoint, work plan, gate/join dispatch ID and expected result across replacement',
              'before/after native send, event and external-effect identities proving no duplicate execution',
              'actual authorized join/result receipt and exact output replay, with Core execution fence preserved',
            ],
          },
        ],
        [
          {
            path: 'services/environment/Agentweaver.Environment/EnvironmentRuntimePlacementReader.cs',
            symbol: 'ResolveCurrentProfileAsync, GetReadinessContextAsync',
            detail: 'The ordinary replacement profile and owner-fenced placement source exist, but this runner does not abandon or reprovision a Host.',
          },
          {
            path: 'packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs',
            symbol: 'MapRuntimeAgentHost /runtime/v1/readiness',
            detail: 'The live Host readiness endpoint returns RuntimeHostRecoveryReceipt. No source-owned endpoint or reader produces the invocation-owned before/after gate-join and no-duplicate receipts.',
          },
          {
            path: 'packages/Agentweaver.Identity/RuntimeHostContracts.cs',
            symbol: 'RuntimeHostRecoveryReceipt.ValidateFor',
            detail: 'The recovery receipt validates its current registration and native cache or journal evidence; it does not prove the prior dispatch result or absence of duplicate effects.',
          },
        ],
      ),
    ),
  ];
  const completedAt = new Date().toISOString();
  const overallStatus = probes.some(probe => probe.status === 'failed')
    ? 'failed'
    : probes.some(probe => probe.status === 'blocked') ? 'blocked' : 'passed';

  return sealP1NativeProbeEnvelope({
    kind: 'p1-dispatch-recovery-acceptance',
    schemaVersion: 1,
    overallStatus,
    startedAt,
    completedAt,
    ownerObservation,
    liveObservations: {
      creditDenials: creditDenialObservation,
      numericDenials: numericDenialObservation,
      replacementRecovery: recoveryObservation,
    },
    probes,
  });
}

function parseArguments(argv) {
  const options = {};
  const keys = new Map([['--input', 'input'], ['--provider-module', 'providerModule']]);
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === '--help') return { help: true };
    if (!keys.has(name) || typeof argv[index + 1] !== 'string' || argv[index + 1].startsWith('--')) {
      throw new Error('Use --input <run-bound-manifest.json> --provider-module <authorized-runtime-reader.mjs>.');
    }
    const key = keys.get(name);
    if (options[key] !== undefined) throw new Error(`The ${name} option can only be used once.`);
    options[key] = argv[++index];
  }
  if (Boolean(options.input) !== Boolean(options.providerModule)) {
    throw new Error('The run-bound input manifest and authorized provider module must be supplied together.');
  }
  return options;
}

/**
 * Runs the read-only CLI with a caller-supplied source client; no credentials belong in its JSON manifest.
 * The provider module must use the live owner routes and return readers, not prebuilt probe results.
 */
export async function runP1DispatchRecoveryCli(argv = process.argv.slice(2), io = console) {
  let options;
  try {
    options = parseArguments(argv);
  } catch (error) {
    io.error(`Usage: ${error.message}`);
    return 2;
  }
  if (options.help) {
    io.log('Usage: node scripts/azure/p1-dispatch-recovery-probes/run.mjs [--input <run-bound-manifest.json> --provider-module <authorized-runtime-reader.mjs>]');
    io.log('The provider exports createP1DispatchRecoveryDependencies({ input }) and returns read-only owner clients.');
    return 0;
  }
  if (!options.input) {
    io.log(JSON.stringify(await runP1DispatchRecoveryProbes(), null, 2));
    return 0;
  }

  let input;
  try {
    input = JSON.parse(await readFile(resolve(options.input), 'utf8'));
  } catch {
    io.error('The dispatch-recovery input manifest is missing or malformed JSON.');
    return 2;
  }
  if (!isRecord(input)) {
    io.error('The dispatch-recovery input manifest must be a JSON object.');
    return 2;
  }
  try {
    if (Object.keys(input).some(key => ![
      'observationContext', 'creditDenialInput', 'numericDenialInput', 'recoveryContext',
    ].includes(key))) throw new Error('The dispatch-recovery manifest contains unknown fields.');
    if (containsCredentials(input)) throw new Error('Credentials must not be stored in the dispatch-recovery manifest.');
    if (input.numericDenialInput !== undefined) validateNumericDenialInput(input.numericDenialInput);
  } catch (error) {
    io.error(error.message);
    return 2;
  }
  let dependencies;
  try {
    const provider = await import(pathToFileURL(resolve(options.providerModule)).href);
    if (typeof provider.createP1DispatchRecoveryDependencies !== 'function') {
      throw new Error('The provider module must export createP1DispatchRecoveryDependencies(input).');
    }
    dependencies = await provider.createP1DispatchRecoveryDependencies({ input });
  } catch (error) {
    io.error(redact(error instanceof Error ? error.message : String(error)));
    return 2;
  }
  if (!isRecord(dependencies)) {
    io.error('The authorized provider module did not return a client object.');
    return 2;
  }
  const result = await runP1DispatchRecoveryProbes({
    observationClient: dependencies.observationClient,
    observationContext: input.observationContext,
    creditDenialInput: input.creditDenialInput,
    creditDenialDependencies: dependencies.creditDenialDependencies,
    numericDenialInput: input.numericDenialInput,
    numericDenialDependencies: dependencies.numericDenialDependencies,
    runtimeReadinessProvider: dependencies.runtimeReadinessProvider,
    recoveryContext: input.recoveryContext,
  });
  io.log(JSON.stringify(redact(result), null, 2));
  return result.overallStatus === 'failed' ? 1 : result.overallStatus === 'blocked' ? 2 : 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  process.exitCode = await runP1DispatchRecoveryCli();
}
