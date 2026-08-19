import { apiClient } from '../api/apiClient';
import { Button, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Spinner } from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';

/**
 * Polls GET /api/auth/github on mount and renders a prominent warning bar when the user's
 * GitHub token has expired or requires action (signed_out state).  Returns null when the
 * token is healthy or while the check is still pending.
 */
export function GitHubTokenWarningBanner() {
  const [tokenActionRequired, setTokenActionRequired] = useState<boolean | null>(null);
  const [relinking, setRelinking] = useState(false);

  const check = useCallback(async () => {
    try {
      const s = await apiClient.getGitHubAuthStatus();
      setTokenActionRequired(s.token_action_required ?? (s.status !== 'signed_in'));
    } catch {
      // Silently ignore — do not block the page if the status check fails.
    }
  }, []);

  useEffect(() => { void check(); }, [check]);

  const handleRelink = async () => {
    setRelinking(true);
    try {
      const { authorize_url: authorizeUrl } = await apiClient.beginLinkGitHubAccount();
      window.location.href = authorizeUrl;
    } catch {
      setRelinking(false);
    }
  };

  if (!tokenActionRequired) return null;

  return (
    <MessageBar intent="warning" style={{ marginBottom: '12px' }}>
      <MessageBarBody>
        <MessageBarTitle>GitHub access expired</MessageBarTitle>
        Your GitHub token has expired. Agent tasks cannot start until you re-link your account.
      </MessageBarBody>
      <MessageBarActions>
        <Button size="small" disabled={relinking} onClick={() => void handleRelink()}>
          {relinking ? <Spinner size="tiny" /> : 'Re-link GitHub account'}
        </Button>
      </MessageBarActions>
    </MessageBar>
  );
}
