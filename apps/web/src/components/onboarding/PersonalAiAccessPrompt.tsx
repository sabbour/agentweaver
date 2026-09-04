import { Button } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { SetupReadiness } from '../SetupReadiness';
import {
  hasDismissedPersonalAiAccessPrompt,
  markPersonalAiAccessPromptDismissed,
  personalAiAccessPromptStorageKey,
} from './personalAiAccessPromptStorage';

export function PersonalAiAccessPrompt({ userKey }: { userKey: string }) {
  const navigate = useNavigate();
  const storageKey = useMemo(() => personalAiAccessPromptStorageKey(userKey), [userKey]);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    let cancelled = false;
    if (hasDismissedPersonalAiAccessPrompt(storageKey)) return undefined;

    void apiClient.getUserAiAccess()
      .then((status) => {
        if (!cancelled) setVisible(status.effective_source === 'none');
      })
      .catch(() => {
        // A failed readiness check must not claim that the user has no provider access.
      });

    return () => { cancelled = true; };
  }, [storageKey]);

  const dismiss = useCallback(() => {
    markPersonalAiAccessPromptDismissed(storageKey);
    setVisible(false);
  }, [storageKey]);

  const openSettings = useCallback(() => {
    dismiss();
    navigate('/settings');
  }, [dismiss, navigate]);

  if (!visible) return null;

  return (
    <SetupReadiness
      compact
      onDismiss={dismiss}
      model={{
        title: 'Set up personal AI access',
        description: 'Session chat needs either your own model provider or an authorized GitHub Copilot account.',
        items: [{
          id: 'personal-ai-access',
          title: 'Personal AI access',
          description: 'Open Account settings to add a provider or connect GitHub Copilot.',
          requirement: 'required',
          status: 'action-required',
        }],
      }}
      primaryAction={(
        <Button appearance="primary" onClick={openSettings}>
          Open AI Access settings
        </Button>
      )}
    />
  );
}
