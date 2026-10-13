import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

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

export function runP1DispatchRecoveryProbes() {
  const startedAt = new Date().toISOString();
  const probes = [
    blockedProbe(
      'numeric-copilot-dispatch-boundaries',
      'AgentHost supports MaxModelTurns and positive HostedCopilot hard credit limits only for workflow-bound registrations using owner-prepared dispatch and begin admission; unbound MaxModelTurns, MaxToolCalls, MaxPromptTokens, and positive hard credit limits remain unsupported. Existing whole-run priced usage denies the next dispatch at or above its observed floor, but late usage cannot revoke a turn already admitted; no all-usage-final claim is made. This runner has no accepted source-owned observer for the actual floor, guard revision, denial/send state, soft-limit warning, and bounded native cursor evidence.',
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
      'The P1 Sandbox replacement and ordinary runtime registration/bootstrap path still lack a supported profile producer for the exact replacement resource and an ordinary observer for the Host recovery mode/cache receipt. The P2 manifest suspend/resume path is explicitly excluded. This source-only command has no actual gate/join restart receipts proving checkpoint continuity and no duplicate native send.',
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
          path: 'services/environment/Agentweaver.Environment/EnvironmentRuntimeBootstrapProfiles.cs',
          symbol: 'EnvironmentRuntimeBootstrapProfileRegistry.Resolve',
          code: 'runtime_delivery_unavailable',
          detail: 'Immutable owner/profile mapping requires an exact ProviderResourceRef; no accepted ordinary producer supplies the replacement mapping.',
        },
        {
          path: 'packages/Agentweaver.AgentRuntime/RuntimeAgentHost.cs',
          symbol: 'ReadinessAsyncCore',
          detail: 'Ordinary readiness omits RuntimeSessionRecovery mode and cache receipt; ResumeCache is populated only by the excluded P2 manifest-resume flow. Readiness/registration alone also cannot prove the exact endpoint matches the receiver expected audience and certificate DNS name.',
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
    probes,
  };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  if (process.argv.length !== 2) {
    process.stderr.write('Usage: node scripts/azure/p1-dispatch-recovery-probes/run.mjs\n');
    process.exitCode = 2;
  } else {
    process.stdout.write(`${JSON.stringify(runP1DispatchRecoveryProbes(), null, 2)}\n`);
  }
}
