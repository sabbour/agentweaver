import test from 'node:test';
import assert from 'node:assert/strict';
import { recoverP1RunCleanup } from '../p1-recovery-cleanup.mjs';

const INVOCATION_ID = '5749fb6f-543b-427d-a04e-b7f836180ef4';
const PROJECT_ID = 'ab0500373d224974b13c7cebdfa0d132';
const OWNER_ASSIGNMENT_ID = '4ea2b975-f000-46b0-b546-196c693c04e4';
const ORCHESTRATOR_ASSIGNMENT_ID = 'fddc75b9-9635-4bc3-9292-ccfa32e8b638';
const ACTOR_ID = 'run-5-setup-actor';
const ENVIRONMENT = {
  issuer: 'https://identity.controlled.test/',
  audience: 'agentweaver-controlled',
  baseUrl: 'https://environment.controlled.test/',
  actorId: ACTOR_ID,
  tenantId: 'run-5-tenant',
};
const PROJECT_NAME = `p1-${INVOCATION_ID}`;
const ASSIGNMENTS = [
  { assignmentId: OWNER_ASSIGNMENT_ID, role: 'owner' },
  { assignmentId: ORCHESTRATOR_ASSIGNMENT_ID, role: 'orchestrator' },
];
const TOKEN = 'controlled-secret-token';

function reconciliation({
  projectId = PROJECT_ID,
  projectState = 'active',
  projectRevision = 2,
  assignments = ASSIGNMENTS,
  authorityPermissions = ['readProjects', 'writeProjects'],
  verdict = 'safe to clean up',
  verdictReasonCode = 'positive_active_setup_evidence',
} = {}) {
  const projectName = PROJECT_NAME;
  return {
    kind: 'p1-run-reconciliation',
    schemaVersion: 1,
    mode: 'read-only-assessment',
    invocationId: INVOCATION_ID,
    projectName,
    projectMatch: {
      status: 'found',
      matches: [{
        projectId,
        name: projectName,
        state: projectState,
        revision: projectRevision,
        configurationRevision: 1,
        effectiveRoles: [],
      }],
    },
    effectiveAuthority: {
      status: 'complete',
      targetVisibility: 'all-tenant-projects',
      context: {
        contractVersion: 1,
        issuer: ENVIRONMENT.issuer,
        actorId: ACTOR_ID,
        tenantId: ENVIRONMENT.tenantId,
        membershipRevision: 4,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [{
          resourceType: 'tenant',
          resourceId: ENVIRONMENT.tenantId,
          permissions: authorityPermissions.map(permission => ({ permission, roleRevision: 4 })),
        }],
      },
    },
    roleAssignments: {
      status: 'complete',
      complete: true,
      projectId,
      assignments: assignments.map(assignment => ({
        assignmentId: assignment.assignmentId,
        principalId: ACTOR_ID,
        role: assignment.role,
        revision: 1,
        createdBy: ACTOR_ID,
        createdAt: '2026-10-13T00:00:00.000Z',
      })),
    },
    auditRecords: {
      status: 'complete',
      complete: true,
      truncated: false,
      records: assignments.map((assignment, index) => ({
        eventId: `b0000000-0000-4000-8000-00000000000${index + 1}`,
        eventType: 'role_assigned',
        assignmentId: assignment.assignmentId,
        resourceType: 'project',
        resourceId: projectId,
        role: assignment.role,
        revision: 1,
        actor: ACTOR_ID,
        createdAt: '2026-10-13T00:00:00.000Z',
      })),
    },
    verdict,
    verdictReasonCode,
    verdictReason: 'The current project and setup assignment evidence match.',
    actions: {
      assessmentOnly: true,
      writeRequestsIssued: false,
      cleanupPerformed: false,
      fixtureWritesPerformed: false,
      grantsChanged: false,
      newJourneyStarted: false,
    },
    accessToken: TOKEN,
  };
}

function cleanupApi({ failAt = null } = {}) {
  const calls = [];
  const methods = new Map([
    ['revokeProjectRoleAssignment', {
      method: 'DELETE',
      path: '/api/v1/projects/{projectId}/role-assignments/{assignmentId}',
      parameters: [{ in: 'query', name: 'expectedRevision', required: true }],
    }],
    ['updateProject', {
      method: 'PATCH',
      path: '/api/v1/projects/{projectId}',
      parameters: [],
      requestBody: {
        required: true,
        content: {
          'application/json': {
            schema: { $ref: '#/components/schemas/UpdateProjectRequest' },
          },
        },
      },
    }],
  ]);
  return {
    calls,
    api: {
      operations: methods,
      schemas: new Map([['UpdateProjectRequest', {
        type: 'object',
        required: ['expectedRevision', 'name', 'state'],
        properties: {
          expectedRevision: { type: 'integer' },
          name: { type: 'string' },
          state: { type: 'string', enum: ['active', 'archived'] },
        },
      }]]),
      async invoke(operationId, request) {
        calls.push({ operationId, ...request });
        if (failAt === operationId) {
          return {
            operationId,
            status: 409,
            transientResponseBody: { code: 'revision_conflict' },
          };
        }
        if (operationId === 'updateProject') {
          return {
            operationId,
            status: 200,
            transientResponseBody: {
              projectId: PROJECT_ID,
              name: PROJECT_NAME,
              state: 'archived',
              revision: 3,
              configurationRevision: 1,
            },
          };
        }
        const role = request.pathParameters.assignmentId === OWNER_ASSIGNMENT_ID
          ? 'owner' : 'orchestrator';
        return {
          operationId,
          status: 200,
          transientResponseBody: {
            assignmentId: request.pathParameters.assignmentId,
            projectId: PROJECT_ID,
            principalId: ACTOR_ID,
            role,
            state: 'revoked',
            revision: 2,
            grantedBy: ACTOR_ID,
            revokedBy: ACTOR_ID,
          },
        };
      },
    },
  };
}

test('recovery cleanup rechecks the exact evidence and sends three guarded writes once', async () => {
  const { api, calls } = cleanupApi();
  const savedEvidence = reconciliation();
  const reads = [];
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence,
    async reconcile() {
      reads.push('read');
      return reads.length === 1
        ? reconciliation()
        : reconciliation({
          projectState: 'archived',
          projectRevision: 3,
          assignments: [],
          verdict: 'already clean',
          verdictReasonCode: 'positive_archived_setup_revocation_evidence',
        });
    },
  });

  assert.equal(receipt.outcome, 'completed');
  assert.equal(receipt.preflight.verdict, 'safe to clean up');
  assert.equal(receipt.readback.verdict, 'already clean');
  assert.equal(receipt.actions.cleanupVerified, true);
  assert.deepEqual(calls.map(call => call.operationId), [
    'revokeProjectRoleAssignment', 'updateProject', 'revokeProjectRoleAssignment',
  ]);
  assert.deepEqual(calls.map(call => call.query?.expectedRevision ?? call.body.expectedRevision), [1, 2, 1]);
  assert.deepEqual(receipt.steps.map(step => step.outcome), ['confirmed', 'confirmed', 'confirmed']);
  assert.equal(JSON.stringify(receipt).includes(TOKEN), false);
  assert.equal(JSON.stringify(receipt).includes('[REDACTED]'), true);
});

test('recovery cleanup refuses a fresh project mismatch before sending writes', async () => {
  const { api, calls } = cleanupApi();
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      return reconciliation({ projectId: 'different-project-id' });
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'fresh_reconciliation_mismatch');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});

test('recovery cleanup refuses when a setup assignment revision changed', async () => {
  const { api, calls } = cleanupApi();
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      const fresh = reconciliation();
      fresh.roleAssignments.assignments[0].revision = 2;
      return fresh;
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'fresh_reconciliation_mismatch');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});

test('recovery cleanup refuses when the project configuration revision changed', async () => {
  const { api, calls } = cleanupApi();
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      const fresh = reconciliation();
      fresh.projectMatch.matches[0].configurationRevision = 2;
      return fresh;
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'fresh_reconciliation_mismatch');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});

test('recovery cleanup refuses when fresh audit already contains a revocation', async () => {
  const { api, calls } = cleanupApi();
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      const fresh = reconciliation();
      fresh.auditRecords.records[0].eventType = 'role_revoked';
      return fresh;
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'fresh_reconciliation_mismatch');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});

test('recovery cleanup stops on a failed write, records indeterminate, and reads back without retry', async () => {
  const { api, calls } = cleanupApi({ failAt: 'updateProject' });
  const reads = [];
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      reads.push('read');
      return reads.length === 1
        ? reconciliation()
        : reconciliation({
          projectState: 'archived',
          assignments: [ASSIGNMENTS[0]],
          verdict: 'needs review',
          verdictReasonCode: 'archived_setup_evidence_incomplete',
        });
    },
  });

  assert.equal(receipt.outcome, 'indeterminate');
  assert.equal(receipt.reasonCode, 'cleanup_write_failed_after_send');
  assert.equal(receipt.actions.writeRequestsIssued, true);
  assert.equal(receipt.readback.verdict, 'needs review');
  assert.deepEqual(calls.map(call => call.operationId), [
    'revokeProjectRoleAssignment', 'updateProject',
  ]);
  assert.deepEqual(receipt.steps.map(step => step.outcome), ['confirmed', 'indeterminate']);
  assert.equal(receipt.steps[1].status, 409);
  assert.equal(reads.length, 2);
});

test('recovery cleanup refuses before writes when tenant-admin write authority is missing', async () => {
  const { api, calls } = cleanupApi();
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      return reconciliation({ authorityPermissions: ['readProjects'] });
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'tenant_admin_authority_required');
  assert.deepEqual(calls, []);
});

test('recovery cleanup refuses before writes when the Gateway update contract loses its revision schema', async () => {
  const { api, calls } = cleanupApi();
  api.schemas.set('UpdateProjectRequest', {
    type: 'object',
    required: ['name', 'state'],
    properties: {
      name: { type: 'string' },
      state: { type: 'string', enum: ['active', 'archived'] },
    },
  });
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      return reconciliation();
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'cleanup_contract_unavailable');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});

test('recovery cleanup refuses before writes when the update operation adds a required query', async () => {
  const { api, calls } = cleanupApi();
  api.operations.get('updateProject').parameters.push({
    in: 'query',
    name: 'newRequiredFence',
    required: true,
  });
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      return reconciliation();
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'cleanup_contract_unavailable');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});

test('recovery cleanup refuses before writes when operation parameters use unresolved references', async () => {
  const { api, calls } = cleanupApi();
  api.operations.get('updateProject').parameters.push({
    $ref: '#/components/parameters/NewRequiredFence',
  });
  const receipt = await recoverP1RunCleanup({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
    savedEvidence: reconciliation(),
    async reconcile() {
      return reconciliation();
    },
  });

  assert.equal(receipt.outcome, 'refused');
  assert.equal(receipt.reasonCode, 'cleanup_contract_unavailable');
  assert.equal(receipt.actions.writeRequestsIssued, false);
  assert.deepEqual(calls, []);
});
