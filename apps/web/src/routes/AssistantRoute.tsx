import { AssistantRunPage } from '../pages/AssistantRunPage';
import { useSearchParams } from 'react-router-dom';

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const projectId = searchParams.get('project') ?? undefined;
  const runId = searchParams.get('runId') ?? '';
  return <AssistantRunPage key={`${projectId ?? ''}:${runId}`} projectId={projectId} />;
}
