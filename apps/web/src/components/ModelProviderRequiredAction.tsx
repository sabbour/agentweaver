import {
  type ModelProviderConnectionRequirement,
  isProjectScopedModelProviderRequirement,
} from '../api/modelProviderConnectionRequirement';
import { ProjectModelProviderSettings } from './ProjectModelProviderSettings';
import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';
import { useNavigate } from 'react-router-dom';

export function ModelProviderRequiredAction({
  requirement,
  onDismiss,
}: {
  requirement: ModelProviderConnectionRequirement;
  onDismiss: () => void;
}) {
  const navigate = useNavigate();
  const isProjectScoped = isProjectScopedModelProviderRequirement(requirement);
  const projectId = requirement.action.project_id;
  return (
    <MessageBar intent="warning">
      <MessageBarBody>{requirement.message}</MessageBarBody>
      <MessageBarActions>
        {isProjectScoped ? (
          <ProjectModelProviderSettings projectId={projectId} triggerLabel="Connect GitHub" />
        ) : (
          <Button appearance="primary" onClick={() => navigate('/platform-settings')}>Connect GitHub</Button>
        )}
        <Button appearance="transparent" size="small" onClick={onDismiss}>Dismiss</Button>
      </MessageBarActions>
    </MessageBar>
  );
}
