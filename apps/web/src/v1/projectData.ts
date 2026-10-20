/**
 * Project-level reads shared by the shell header, breadcrumbs, and project pages.
 * Each hook loads through the signed-in Broker session and drops results that
 * arrive after the token or project changed, so a page never shows another
 * project's data.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type {
  ProjectAuthorizationContextResponse,
  ProjectAuthorizationPermission,
  ProjectSummary,
  VersionedProjectConfiguration,
} from './contracts';

/** Load state for one Gateway read. `loadedAt` is the client time the data arrived. */
export interface Loaded<T> {
  data: T | null;
  loading: boolean;
  error: unknown;
  loadedAt: Date | null;
  reload: () => void;
  /** Replaces the cached value after a successful write. */
  replace: (next: T) => void;
}

/** Loads a value scoped to the current token and key, ignoring stale responses. */
function useScopedLoad<T>(
  key: string,
  read: ((token: string, tenantSelector: string | null) => Promise<T>) | null,
  accept: (value: T) => boolean = () => true,
): Loaded<T> {
  const { session, apiCall } = useAuth();
  const scope = JSON.stringify([session?.accessToken ?? null, key]);
  const [state, setState] = useState<{ scope: string; data: T | null; error: unknown; loadedAt: Date | null }>(
    { scope: '', data: null, error: null, loadedAt: null },
  );
  const [generation, setGeneration] = useState(0);
  const readRef = useRef(read);
  const acceptRef = useRef(accept);
  useEffect(() => {
    readRef.current = read;
    acceptRef.current = accept;
  });
  const hasReader = read !== null;

  useEffect(() => {
    if (!hasReader) return;
    let active = true;
    const timer = window.setTimeout(() => {
      const reader = readRef.current;
      if (!reader) return;
      void apiCall(reader, null)
        .then((value) => {
          if (!active) return;
          if (!acceptRef.current(value)) throw new Error('The service returned data for a different project.');
          setState({ scope, data: value, error: null, loadedAt: new Date() });
        })
        .catch((reason: unknown) => {
          if (active) setState((previous) => ({
            scope,
            data: previous.scope === scope ? previous.data : null,
            error: reason,
            loadedAt: previous.scope === scope ? previous.loadedAt : null,
          }));
        });
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, scope, generation, hasReader]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const replace = useCallback((next: T) => setState({ scope, data: next, error: null, loadedAt: new Date() }), [scope]);
  const current = state.scope === scope;
  return {
    data: current ? state.data : null,
    loading: hasReader && (!current || (state.data === null && state.error === null)),
    error: current ? state.error : null,
    loadedAt: current ? state.loadedAt : null,
    reload,
    replace,
  };
}

/** Reads one project summary for the route's project. */
export function useProjectSummary(projectId: string): Loaded<ProjectSummary> {
  return useScopedLoad(
    `project:${projectId}`,
    projectId ? (token, tenant) => gatewayClient.getProject(token, projectId, tenant) : null,
    (value) => value.projectId === projectId,
  );
}

/** Reads the current configuration revision for the route's project. */
export function useProjectConfiguration(projectId: string): Loaded<VersionedProjectConfiguration> {
  return useScopedLoad(
    `configuration:${projectId}`,
    projectId ? (token, tenant) => gatewayClient.getProjectConfiguration(token, projectId, tenant) : null,
    (value) => value.projectId === projectId,
  );
}

/** Reads the signed-in actor's tenant and effective permissions. */
export function useAuthorizationContext(): Loaded<ProjectAuthorizationContextResponse> {
  return useScopedLoad('authorization-context', (token, tenant) => gatewayClient.getAuthorizationContext(token, tenant));
}

/** Plain-English labels for each permission the Projects owner can grant. */
export const PERMISSION_LABELS: Record<ProjectAuthorizationPermission, string> = {
  readProjects: 'View the project',
  writeProjects: 'Change project settings',
  createProjects: 'Create projects',
  readRunSelection: 'View run model and team choices',
  acceptRunSelection: 'Approve run model and team choices',
  accessPrivateKnowledge: 'Read private memories',
  readPlatformRuntimeDefaults: 'View platform defaults',
  writePlatformRuntimeDefaults: 'Change platform defaults',
};

/** One permission and the scope that grants it. */
export interface PermissionSource {
  permission: ProjectAuthorizationPermission;
  scope: 'platform' | 'tenant' | 'project';
}

/**
 * Collects permissions that apply to one project: grants on the project itself,
 * on the caller's tenant, and on the platform. Without a project, it returns
 * tenant and platform grants only.
 */
export function permissionsForProject(
  context: ProjectAuthorizationContextResponse,
  projectId?: string,
): PermissionSource[] {
  const sources: PermissionSource[] = [];
  for (const grant of context.effectiveAuthority) {
    const applies = grant.resourceType === 'platform'
      || (grant.resourceType === 'tenant' && grant.resourceId === context.tenantId)
      || (grant.resourceType === 'project' && projectId !== undefined && grant.resourceId === projectId);
    if (!applies) continue;
    for (const permission of grant.permissions) {
      if (!sources.some((item) => item.permission === permission.permission && item.scope === grant.resourceType))
        sources.push({ permission: permission.permission, scope: grant.resourceType });
    }
  }
  return sources;
}

/** Short access label for headers and tiles: "Can edit", "Can view", or "No access". */
export function accessSummary(permissions: PermissionSource[]): 'Can edit' | 'Can view' | 'No access' {
  if (permissions.some((item) => item.permission === 'writeProjects')) return 'Can edit';
  if (permissions.some((item) => item.permission === 'readProjects')) return 'Can view';
  return 'No access';
}
