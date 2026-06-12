import { useEffect, useState } from 'react';
import {
  Button,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { GitHubAuthStatus } from '../api/types';

const useStyles = makeStyles({
  root: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
});

/** Extract a human-readable message from an ApiError, handling ProblemDetails JSON bodies. */
function apiErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 503) return 'GitHub sign-in is not configured on this server.';
    try {
      const problem = JSON.parse(err.body) as { detail?: string };
      if (problem.detail) return problem.detail;
    } catch { /* not JSON */ }
    return `Error ${err.status}: ${err.body}`;
  }
  return err instanceof Error ? err.message : String(err);
}

export function GitHubSignIn() {
  const styles = useStyles();
  const [status, setStatus] = useState<GitHubAuthStatus | null>(null);
  const [login, setLogin] = useState<string | null>(null);
  const [avatarUrl, setAvatarUrl] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiClient.getGitHubAuthStatus()
      .then((res) => {
        if (!cancelled) {
          setStatus(res.status);
          setLogin(res.login ?? null);
          setAvatarUrl(res.avatar_url ?? null);
        }
      })
      .catch((err) => {
        if (!cancelled) setError(apiErrorMessage(err));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const handleSignOut = async () => {
    try {
      await apiClient.signOutGitHub();
      window.location.href = '/';
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  };

  if (loading) {
    return <Spinner size="extra-tiny" aria-label="Loading GitHub auth status" />;
  }

  if (error) {
    return null;
  }

  if (status === 'signed_in') {
    return (
      <div className={styles.root}>
        {avatarUrl && (
          <img
            src={avatarUrl}
            alt={login ?? ''}
            style={{ width: 28, height: 28, borderRadius: '50%', objectFit: 'cover' }}
          />
        )}
        <Text>{login}</Text>
        <Button appearance="subtle" onClick={() => void handleSignOut()}>Sign out</Button>
      </div>
    );
  }

  return null;
}

