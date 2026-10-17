/**
 * Runs read-only P1 dispatch recovery probes and emits evidence for the missing prerequisites.
 * Probe outcomes are represented as structured blockers; this entry point does not repair infrastructure.
 */
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { redact } from '../../harness-shared/redaction.mjs';

const sourceOnlyEvidence = (requiredReceipts, sourceGates = []) => ({
  kind: 'source-prerequisite-blocker',
  observed: false,
  observations: [],
  requiredReceipts,
  sourceGates,
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

/**
 * Collects the configured recovery-probe observations and writes the resulting evidence document.
 * Missing prerequisites are reported as blockers; failed command execution is returned as probe evidence.
 */
export async function runP1DispatchRecoveryProbes({
  observationClient,
  observationContext,
} = {}) {
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
  const startedAt = new Date().toISOString();
  const probes = [
    blockedProbe(
      'numeric-copilot-dispatch-boundaries',
      'AgentHost supports MaxModelTurns and MaxToolCalls only for workflow-bound registrations using owner-prepared dispatch and begin admission. MaxPromptTokens remains preserved through owner projection but is rejected with runtime_numeric_limits_unsupported before SDK setup, including for workflow-bound registrations; unbound MaxModelTurns and MaxToolCalls are also rejected. Positive HostedCopilot hard credit limits are supported only for workflow-bound registrations. Existing whole-run priced usage denies the next dispatch at or above its observed floor, but late usage cannot revoke a turn already admitted; no all-usage-final claim is made. This runner has no accepted source-owned observer for the actual floor, guard revision, denial/send state, soft-limit warning, and bounded native cursor evidence.',
      sourceOnlyEvidence(
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
            detail: 'Workflow-bound MaxToolCalls uses owner-prepared dispatch and begin admission. MaxPromptTokens is retained through owner projection and registration but is rejected before SDK setup; unbound MaxModelTurns and MaxToolCalls are rejected too.',
          },
          {
            path: 'packages/Agentweaver.AgentRuntime/AuthorizedRuntimeSession.cs',
            symbol: 'RequireSupportedCostTurnAffinity',
            code: 'runtime_usage_affinity_unavailable',
          },
          {
            path: 'services/orchestrator/Agentweaver.Orchestrator/RuntimeUsageSourceStore.cs',
            symbol: 'ReconcileNativeCreditLimitsAsync',
            detail: 'The workflow-bound owner path uses conservative whole-run priced totals and a current snapshot under the native dispatch lock to deny the next turn at or above the observed floor. At the soft limit it queues one durable warning; replays and concurrent admissions do not duplicate it, while later priced totals advance the floor and ledger-event count. Late authenticated usage affects the next snapshot; an already admitted turn is not revoked and this is not an all-usage-final claim.',
          },
        ],
      ),
    ),
    blockedProbe(
      'checkpoint-restart-duplicate-fence',
      'The ordinary replacement-profile producer and owner-fenced placement, bootstrap, readiness, and source-derived recovery receipt exist, but this run-p1 source-only report does not dispatch, invoke, or ingest the native runner or collect a replacement-run recovery receipt. GatewayRouteCatalog has no matching mapping, and no transport or live receipt proves a replacement run. The P2 manifest suspend/resume path is explicitly excluded; ResumeCache remains specific to that flow. This report has no actual gate/join restart receipts proving checkpoint continuity and no duplicate native send.',
      sourceOnlyEvidence([
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
            'current Environment/provider fences, lease revisions and preserved Core execution fence; changed provider/lease fences stated explicitly',
            'exact retained or restored workspace volume/resource/data generation and actual new Host native HTTPS endpoint',
            'verify the endpoint against the receiver expected audience and certificate DNS name; a missing or mismatched deployment prerequisite blocks acceptance even when registration/readiness succeeds',
            'current owner-approved bootstrap profile mapped to the exact replacement ProviderResourceRef',
          ],
        },
        {
          owner: 'Orchestrator, Environment, and Identity',
          receipt: 'ordinary-fresh-registration-and-bootstrap',
          requiredFacts: [
            'new RuntimeInstanceId and registration derived from exact current ordinary placement, same logical root/child and accepted selection',
            'actual fresh bootstrap delivery/nonce, consume and exchange receipts with current Observe/source grant',
            'stale old registration and grant denied; no root/gate fence reset or advance merely to create a replacement',
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
      ], [
        {
          path: 'services/environment/Agentweaver.Environment/EnvironmentRuntimePlacementReader.cs',
          symbol: 'ResolveCurrentProfileAsync, GetReadinessContextAsync',
          detail: 'The authenticated replacement producer and ordinary owner/readiness sources exist. This run-p1 report does not invoke or dispatch them; GatewayRouteCatalog has no matching mapping, and no transport or live receipt is produced.',
        },
        {
          path: 'packages/Agentweaver.AgentRuntime/RuntimeAgentHost.cs',
          symbol: 'ReadinessAsyncCore',
          detail: 'Ordinary RuntimeHostReadinessReceipt includes the source-derived RuntimeSessionRecovery mode and RuntimeHostRecoveryReceipt, including native-cache acknowledgment or journal evidence. This report does not invoke the runner or collect a run-specific recovery receipt; ResumeCache remains specific to the excluded P2 manifest-resume flow. Readiness/registration alone cannot prove actual native consumption or that the exact endpoint matches the receiver expected audience and certificate DNS name.',
        },
      ]),
    ),
  ];
  const completedAt = new Date().toISOString();

  return {
    kind: 'p1-dispatch-recovery-acceptance',
    schemaVersion: 1,
    overallStatus: 'blocked',
    startedAt,
    completedAt,
    ownerObservation,
    probes,
  };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  if (process.argv.length !== 2) {
    process.stderr.write('Usage: node scripts/azure/p1-dispatch-recovery-probes/run.mjs\n');
    process.exitCode = 2;
  } else {
    process.stdout.write(`${JSON.stringify(await runP1DispatchRecoveryProbes(), null, 2)}\n`);
  }
}
