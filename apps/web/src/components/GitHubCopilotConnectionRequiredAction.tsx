import {
  type GitHubCopilotConnectionRequirement,
} from '../api/githubConnectionRequirement';
import { GitHubCopilotConnectionPicker } from './GitHubCopilotConnectionPicker';
import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';
import { useNavigate } from 'react-router-dom';

export function GitHubCopilotConnectionRequiredAction({
  requirement,
  onDismiss,
}: {
  requirement: GitHubCopilotConnectionRequirement;
  onDismiss: () => void;
}) {
  const navigate = useNavigate();
  const projectId = requirement.action.project_id;
  return (
    <MessageBar intent="warning">
      <MessageBarBody>{requirement.message}</MessageBarBody>
      <MessageBarActions>
        {projectId ? (
          <GitHubCopilotConnectionPicker projectId={projectId} triggerLabel="Connect GitHub" />
        ) : (
          <Button appearance="primary" onClick={() => navigate('/settings')}>Connect GitHub</Button>
        )}
        <Button appearance="transparent" size="small" onClick={onDismiss}>Dismiss</Button>
      </MessageBarActions>
    </MessageBar>
  );
}
