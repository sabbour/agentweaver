import { Navigate } from 'react-router-dom';
import { MessageBar, MessageBarBody } from '@fluentui/react-components';
import { getLastActiveProjectId } from '../../components/shell/projectContext';

export function ObservabilityRedirectPage({ suffix = '' }: { suffix?: string }) {
  const projectId = getLastActiveProjectId();
  if (projectId) {
    return <Navigate to={`/projects/${projectId}/observability${suffix}`} replace />;
  }

  return (
    <MessageBar intent="warning">
      <MessageBarBody>Select a project to view observability data.</MessageBarBody>
    </MessageBar>
  );
}
