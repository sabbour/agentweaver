import { AssistantRunPage } from '../pages/AssistantRunPage';
import { getLastActiveProjectId } from '../components/shell/projectContext';
import { useSearchParams } from 'react-router-dom';

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const projectId = searchParams.get('project') ?? getLastActiveProjectId() ?? undefined;
  const runId = searchParams.get('runId') ?? '';
  return <AssistantRunPage key={`${projectId ?? ''}:${runId}`} projectId={projectId} />;
}
