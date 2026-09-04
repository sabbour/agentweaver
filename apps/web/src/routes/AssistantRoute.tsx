import { AssistantRunPage } from '../pages/AssistantRunPage';
import { readAssistantSessionKey } from './assistantNavigation';
import { useLocation, useSearchParams } from 'react-router-dom';

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const projectId = searchParams.get('project') ?? undefined;
  const explicitSessionKey = readAssistantSessionKey(location.state);
  return (
    <AssistantRunPage
      key={`${projectId ?? ''}:${explicitSessionKey}`}
      projectId={projectId}
    />
  );
}
