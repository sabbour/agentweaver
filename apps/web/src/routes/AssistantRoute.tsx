import { AssistantRunPage } from '../pages/AssistantRunPage';
import { useSearchParams } from 'react-router-dom';

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const projectId = searchParams.get('project') ?? undefined;
  return <AssistantRunPage key={projectId ?? ''} projectId={projectId} />;
}
