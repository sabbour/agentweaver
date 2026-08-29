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

export function GitHubCopilotConnectionRequiredAction({
  requirement,
  onDismiss,
}: {
  requirement: GitHubCopilotConnectionRequirement;
  onDismiss: () => void;
}) {
  return (
    <MessageBar intent="warning">
      <MessageBarBody>{requirement.message}</MessageBarBody>
      <MessageBarActions>
        <GitHubCopilotConnectionPicker
          projectId={requirement.action.project_id}
          triggerLabel="Connect GitHub Copilot"
        />
        <Button appearance="transparent" size="small" onClick={onDismiss}>Dismiss</Button>
      </MessageBarActions>
    </MessageBar>
  );
}
