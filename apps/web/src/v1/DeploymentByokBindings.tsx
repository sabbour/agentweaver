import { Button, Field, Input, Text } from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ByokDeploymentBindingSnapshot } from './contracts';
import { ErrorNotice } from './pageParts';

function validSnapshot(value: ByokDeploymentBindingSnapshot): boolean {
  return typeof value?.revision === 'string' && value.revision.length > 0 &&
    Array.isArray(value.bindings) && value.bindings.every((item) =>
      typeof item?.reference === 'string' && typeof item.enabled === 'boolean' &&
      Array.isArray(item.reasoningEfforts) && item.reasoningEfforts.every((effort) => typeof effort === 'string'));
}

export function DeploymentByokBindings() {
  const { apiCall, session, authSessionGeneration, authSessionEpoch, isActiveSession, ensureSession, authorize } = useAuth();
  const [tenant, setTenant] = useState('');
  const [state, setState] = useState<{
    key: string;
    status: 'loading' | 'error' | 'ready';
    snapshot?: ByokDeploymentBindingSnapshot;
  } | null>(null);
  const request = useRef(0);
  const pending = useRef<{ epoch: number; session: NonNullable<typeof session>; tenant: string; restarts: number } | null>(null);
  const key = `${authSessionEpoch}:${authSessionGeneration}:${tenant}`;
  const activeUnbound = session != null && !session.binding;

  const load = useCallback(async (restarts = 0) => {
    const selectedTenant = tenant.trim();
    if (!session || session.binding || !selectedTenant || !isActiveSession(session)) return;
    const current = ++request.current;
    const expectedKey = key;
    pending.current = { epoch: authSessionEpoch, session, tenant, restarts };
    setState({ key: expectedKey, status: 'loading' });
    try {
      const result = await apiCall((token) => {
        if (!isActiveSession(session) || token !== session.accessToken) throw new Error('The active session changed.');
        return gatewayClient.getByokDeploymentBindings(token, selectedTenant);
      }, null);
      if (current !== request.current || !isActiveSession(session)) return;
      if (!validSnapshot(result)) throw new Error('Invalid snapshot.');
      pending.current = null;
      setState({ key: expectedKey, status: 'ready', snapshot: result });
    } catch {
      if (current === request.current && isActiveSession(session)) {
        pending.current = null;
        setState({ key: expectedKey, status: 'error' });
      }
    }
  }, [apiCall, authSessionEpoch, isActiveSession, key, session, tenant]);

  useEffect(() => {
    const previous = pending.current;
    if (!previous) return;
    if (!session || session.binding || previous.epoch !== authSessionEpoch || previous.tenant !== tenant) {
      pending.current = null;
      return;
    }
    if (previous.session !== session) {
      if (previous.restarts === 0) void load(1);
      else {
        request.current += 1;
        pending.current = null;
        setState({ key, status: 'error' });
      }
    }
  }, [authSessionEpoch, key, load, session, tenant]);

  const currentState = activeUnbound && state?.key === key ? state : null;
  const visible = currentState?.snapshot;
  return (
    <PageSection title="Deployment-managed BYOK bindings"
      description="This is the responding Projects service's startup copy. AgentHost checks bindings when a run starts.">
      <Field label="Tenant ID">
        <Input value={tenant} autoComplete="off" onChange={(_, data) => {
          request.current += 1;
          pending.current = null;
          setTenant(data.value);
          setState(null);
        }} />
      </Field>
      {!activeUnbound && <div>
        <Text>Use a platform sign-in to read deployment bindings.</Text>
        <Button onClick={() => void ensureSession(null, true)}>Switch to platform sign-in</Button>
        <Button onClick={() => void authorize(null)}>Sign in</Button>
      </div>}
      <Button disabled={!activeUnbound || !tenant.trim() || currentState?.status === 'loading'}
        onClick={() => void load()}>Load bindings</Button>
      {currentState?.status === 'error' && <ErrorNotice>
        The deployment binding snapshot could not load. Check your access and try again.
      </ErrorNotice>}
      {visible && <>
        <Text>Projects startup revision: {visible.revision}</Text>
        {visible.bindings.length === 0 ? <Text>No BYOK bindings are in this startup copy.</Text> :
          <ul aria-label="Deployment-managed BYOK bindings">
            {visible.bindings.map((binding) => <li key={binding.reference}>
              {binding.reference} · Managed by deployment · {binding.enabled ? 'Enabled' : 'Disabled'}
              {binding.reasoningEfforts.length > 0 && ` · ${binding.reasoningEfforts.join(', ')}`}
            </li>)}
          </ul>}
      </>}
    </PageSection>
  );
}
