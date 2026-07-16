import { AssistantRunPage } from '../pages/AssistantRunPage';
import { useSearchParams } from 'react-router-dom';

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  return <AssistantRunPage projectId={searchParams.get('project') ?? undefined} />;
}
