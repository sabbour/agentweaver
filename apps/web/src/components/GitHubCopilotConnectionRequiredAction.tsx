import { apiClient } from '../api/apiClient';
import {
  type GitHubCopilotConnectionRequirement,
} from '../api/githubConnectionRequirement';
import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';
import { useState } from 'react';

export function GitHubCopilotConnectionRequiredAction({
  requirement,
  onDismiss,
}: {
  requirement: GitHubCopilotConnectionRequirement;
  onDismiss: () => void;
}) {
  const [connecting, setConnecting] = useState(false);
  const [connectionError, setConnectionError] = useState<string | null>(null);

  const connect = async () => {
    setConnecting(true);
    setConnectionError(null);
    try {
      const handoff = await apiClient.beginProjectCopilotAuthorization(requirement.action.project_id);
      window.location.assign(handoff.authorization_url);
    } catch {
      setConnectionError('The GitHub Copilot App connection could not be started. Try again.');
      setConnecting(false);
    }
  };

  return (
    <MessageBar intent="warning">
      <MessageBarBody>
        {connectionError ?? requirement.message}
      </MessageBarBody>
      <MessageBarActions>
        <Button appearance="primary" size="small" disabled={connecting} onClick={() => void connect()}>
          {connecting ? 'Opening GitHub…' : 'Connect GitHub Copilot'}
        </Button>
        <Button appearance="transparent" size="small" onClick={onDismiss}>Dismiss</Button>
      </MessageBarActions>
    </MessageBar>
  );
}
