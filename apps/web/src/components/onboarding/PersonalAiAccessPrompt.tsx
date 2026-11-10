import { Button } from '@fluentui/react-components';
import { useCallback, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { SetupReadiness } from '../SetupReadiness';
import {
  hasDismissedPersonalAiAccessPrompt,
  markPersonalAiAccessPromptDismissed,
  personalAiAccessPromptStorageKey,
} from './personalAiAccessPromptStorage';

/** Shows AI access guidance without claiming to know the user's provider status. */
export function PersonalAiAccessPrompt({
  userKey,
  projectId,
  onDismiss,
}: {
  userKey: string;
  projectId?: string;
  onDismiss?: () => void;
}) {
  const navigate = useNavigate();
  const storageKey = useMemo(() => personalAiAccessPromptStorageKey(userKey), [userKey]);
  const [visible, setVisible] = useState(
    () => Boolean(userKey.trim()) && !hasDismissedPersonalAiAccessPrompt(storageKey),
  );

  const dismiss = useCallback(() => {
    markPersonalAiAccessPromptDismissed(storageKey);
    setVisible(false);
    onDismiss?.();
  }, [onDismiss, storageKey]);

  const openConnections = useCallback(() => {
    dismiss();
    navigate(projectId
      ? `/projects/${encodeURIComponent(projectId)}/settings?section=connections`
      : '/projects');
  }, [dismiss, navigate, projectId]);

  if (!visible || !userKey.trim()) return null;

  return (
    <SetupReadiness
      compact
      onDismiss={dismiss}
      model={{
        title: 'AI access',
        description: 'This guide does not check your current AI access.',
        items: [
          {
            id: 'github-copilot',
            title: 'GitHub Copilot',
            description: projectId
              ? 'Copilot connects to this project. Open Project settings > Connections to connect or check the connection.'
              : 'Copilot connects to a project. Choose a project, then open Project settings > Connections to connect or check the connection.',
            requirement: 'optional',
            status: 'optional',
          },
          {
            id: 'byok',
            title: 'Bring your own key (BYOK)',
            description: 'The v1 web app does not provide BYOK setup yet.',
            requirement: 'optional',
            status: 'unavailable',
          },
        ],
      }}
      primaryAction={(
        <Button appearance="primary" onClick={openConnections}>
          {projectId ? 'Open project connections' : 'Choose a project'}
        </Button>
      )}
    />
  );
}
