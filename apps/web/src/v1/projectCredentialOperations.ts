/**
 * In-memory operation tracking for platform and project BYOK provider key writes.
 * Keyed by confirmed (issuer, actorId, tenantId, scope, projectId, providerId) and
 * never stores secret values.
 */

export type CredentialOperationScope = 'platform' | 'project';

export type ProjectCredentialOperationStatus =
  | 'pending'
  | 'publication_pending'
  | 'disable_pending'
  | 'quarantined'
  | 'uncertain';

export interface ProjectCredentialOperation {
  operationId: string;
  expectedProviderRevision: number;
  expectedCredentialRevision?: number;
  status: ProjectCredentialOperationStatus;
  stalePublicationConflict?: boolean;
}

export interface ProjectCredentialOperationKey {
  issuer: string;
  actorId: string;
  tenantId: string;
  scope?: CredentialOperationScope;
  projectId: string | null;
  providerId: string;
}

export interface ProjectCredentialOperationStore {
  get: (key: ProjectCredentialOperationKey) => ProjectCredentialOperation | null;
  set: (key: ProjectCredentialOperationKey, operation: ProjectCredentialOperation) => void;
  delete: (key: ProjectCredentialOperationKey) => void;
  clear: () => void;
  retainIdentity: (identity: { issuer: string; actorId: string; tenantId: string }) => void;
}

function isNonEmpty(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function normalizeKey(key: ProjectCredentialOperationKey): string | null {
  if (!isNonEmpty(key.issuer) || !isNonEmpty(key.actorId) || !isNonEmpty(key.tenantId) ||
      !isNonEmpty(key.providerId))
    return null;
  const scope: CredentialOperationScope = key.scope ?? (key.projectId === null ? 'platform' : 'project');
  if (scope === 'platform') {
    if (key.projectId !== null) return null;
  } else if (!isNonEmpty(key.projectId)) {
    return null;
  }
  return JSON.stringify([
    key.issuer,
    key.actorId,
    key.tenantId,
    scope,
    scope === 'platform' ? null : key.projectId,
    key.providerId,
  ]);
}

export function createProjectCredentialOperationStore(): ProjectCredentialOperationStore {
  const operations = new Map<string, {
    identity: { issuer: string; actorId: string; tenantId: string };
    operation: ProjectCredentialOperation;
  }>();

  return {
    get(key) {
      const mapKey = normalizeKey(key);
      if (!mapKey) return null;
      return operations.get(mapKey)?.operation ?? null;
    },
    set(key, operation) {
      const mapKey = normalizeKey(key);
      if (!mapKey) return;
      operations.set(mapKey, {
        identity: { issuer: key.issuer, actorId: key.actorId, tenantId: key.tenantId },
        operation,
      });
    },
    delete(key) {
      const mapKey = normalizeKey(key);
      if (!mapKey) return;
      operations.delete(mapKey);
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
