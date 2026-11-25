/**
 * In-memory operation tracking for project BYOK provider first-key writes.
 * Keyed by confirmed (issuer, actorId, tenantId, projectId, providerId) and
 * never stores secret values.
 */

export type ProjectCredentialOperationStatus =
  | 'pending'
  | 'publication_pending'
  | 'disable_pending'
  | 'quarantined'
  | 'uncertain';

export interface ProjectCredentialOperation {
  operationId: string;
  expectedProviderRevision: number;
  status: ProjectCredentialOperationStatus;
  stalePublicationConflict?: boolean;
}

export interface ProjectCredentialOperationKey {
  issuer: string;
  actorId: string;
  tenantId: string;
  projectId: string;
  providerId: string;
}

export interface ProjectCredentialOperationStore {
  get: (key: ProjectCredentialOperationKey) => ProjectCredentialOperation | null;
  set: (key: ProjectCredentialOperationKey, operation: ProjectCredentialOperation) => void;
  delete: (key: ProjectCredentialOperationKey) => void;
  clear: () => void;
  retainIdentity: (identity: { issuer: string; actorId: string; tenantId: string }) => void;
}

function serializeKey(key: ProjectCredentialOperationKey): string {
  return JSON.stringify([
    key.issuer,
    key.actorId,
    key.tenantId,
    key.projectId,
    key.providerId,
  ]);
}

function isNonEmpty(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

export function createProjectCredentialOperationStore(): ProjectCredentialOperationStore {
  const operations = new Map<string, {
    identity: { issuer: string; actorId: string; tenantId: string };
    operation: ProjectCredentialOperation;
  }>();

  return {
    get(key) {
      if (!isNonEmpty(key.issuer) || !isNonEmpty(key.actorId) || !isNonEmpty(key.tenantId) ||
          !isNonEmpty(key.projectId) || !isNonEmpty(key.providerId))
        return null;
      return operations.get(serializeKey(key))?.operation ?? null;
    },
    set(key, operation) {
      if (!isNonEmpty(key.issuer) || !isNonEmpty(key.actorId) || !isNonEmpty(key.tenantId) ||
          !isNonEmpty(key.projectId) || !isNonEmpty(key.providerId))
        return;
      operations.set(serializeKey(key), {
        identity: { issuer: key.issuer, actorId: key.actorId, tenantId: key.tenantId },
        operation,
      });
    },
    delete(key) {
      if (!isNonEmpty(key.issuer) || !isNonEmpty(key.actorId) || !isNonEmpty(key.tenantId) ||
          !isNonEmpty(key.projectId) || !isNonEmpty(key.providerId))
        return;
      operations.delete(serializeKey(key));
    },
    clear() {
      operations.clear();
    },
    retainIdentity(identity) {
      for (const [mapKey, entry] of operations.entries()) {
        if (entry.identity.issuer !== identity.issuer ||
            entry.identity.actorId !== identity.actorId ||
            entry.identity.tenantId !== identity.tenantId) {
          operations.delete(mapKey);
        }
      }
    },
  };
}
