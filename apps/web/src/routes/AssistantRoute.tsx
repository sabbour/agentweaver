import { AssistantRunPage } from '../pages/AssistantRunPage';
import { readAssistantSessionKey } from './assistantNavigation';
import { useLocation, useSearchParams } from 'react-router-dom';
import { useState } from 'react';

interface AssistantRouteIdentity {
  projectId?: string;
  explicitSessionKey: string;
  establishedRunId: string;
  revision: number;
}

function nextAssistantRouteIdentity(
  current: AssistantRouteIdentity,
  projectId: string | undefined,
  explicitSessionKey: string,
  runId: string,
): AssistantRouteIdentity {
  if (current.projectId !== projectId) {
    return {
      projectId,
      explicitSessionKey,
      establishedRunId: runId,
      revision: current.revision + 1,
    };
  }
  if (explicitSessionKey && explicitSessionKey !== current.explicitSessionKey) {
    return {
      projectId,
      explicitSessionKey,
      establishedRunId: runId,
      revision: current.revision + 1,
    };
  }
  if (runId && current.establishedRunId && runId !== current.establishedRunId) {
    return {
      projectId,
      explicitSessionKey,
      establishedRunId: runId,
      revision: current.revision + 1,
    };
  }
  if (runId && !current.establishedRunId) {
    return { ...current, establishedRunId: runId };
  }
  if (!runId && current.establishedRunId) {
    // A gone or closed run resets in place so the next auto-resumed run is
    // treated as this session's first assignment rather than cross-run navigation.
    return { ...current, establishedRunId: '' };
  }
  return current;
}

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const projectId = searchParams.get('project') ?? undefined;
  const runId = searchParams.get('runId') ?? '';
  const explicitSessionKey = readAssistantSessionKey(location.state);
  const [routeIdentity, setRouteIdentity] = useState<AssistantRouteIdentity>({
    projectId,
    explicitSessionKey,
    establishedRunId: runId,
    revision: 0,
  });
  const nextIdentity = nextAssistantRouteIdentity(
    routeIdentity,
    projectId,
    explicitSessionKey,
    runId,
  );
  if (nextIdentity !== routeIdentity) {
    setRouteIdentity(nextIdentity);
  }

  return (
    <AssistantRunPage
      key={`${projectId ?? ''}:${nextIdentity.revision}`}
      projectId={projectId}
    />
  );
}
