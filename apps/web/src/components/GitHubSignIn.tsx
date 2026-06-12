import { useEffect, useRef, useState } from 'react';
import {
  Button,
  MessageBar,
  MessageBarBody,
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
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  deviceFlow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    alignItems: 'flex-end',
  },
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    letterSpacing: '0.1em',
  },
  link: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
    fontSize: tokens.fontSizeBase200,
    ':hover': { textDecoration: 'underline' },
  },
  errorText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
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
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Device flow state
  const [userCode, setUserCode] = useState<string | null>(null);
  const [verificationUri, setVerificationUri] = useState<string | null>(null);
  const [polling, setPolling] = useState(false);
  const [flowError, setFlowError] = useState<string | null>(null);
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const clearPollTimer = () => {
    if (pollTimerRef.current !== null) {
      clearInterval(pollTimerRef.current);
      pollTimerRef.current = null;
    }
  };

  const fetchStatus = async () => {
    try {
      const res = await apiClient.getGitHubAuthStatus();
      setStatus(res.status);
      setLogin(res.login);
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    let cancelled = false;
    apiClient.getGitHubAuthStatus()
      .then((res) => {
        if (!cancelled) {
          setStatus(res.status);
          setLogin(res.login);
        }
      })
      .catch((err) => {
        if (!cancelled) setError(apiErrorMessage(err));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => {
      cancelled = true;
      clearPollTimer();
    };
  }, []);

  const handleSignIn = async () => {
    setFlowError(null);
    setUserCode(null);
    setVerificationUri(null);
    try {
      const flow = await apiClient.startGitHubDeviceFlow();
      setUserCode(flow.user_code);
      setVerificationUri(flow.verification_uri);
      setPolling(true);

      pollTimerRef.current = setInterval(() => {
        void (async () => {
          try {
            const result = await apiClient.pollGitHubAuth();
            if (result.status === 'success') {
              clearPollTimer();
              setPolling(false);
              setUserCode(null);
              setVerificationUri(null);
              await fetchStatus();
            } else if (result.status === 'expired' || result.status === 'denied') {
              clearPollTimer();
              setPolling(false);
              setUserCode(null);
              setVerificationUri(null);
              setFlowError(result.status === 'expired' ? 'Authorization expired. Try again.' : 'Authorization denied.');
            }
          } catch (err) {
            clearPollTimer();
            setPolling(false);
            setFlowError(apiErrorMessage(err));
          }
        })();
      }, flow.interval * 1000);
    } catch (err) {
      setFlowError(apiErrorMessage(err));
    }
  };

  const handleSignOut = async () => {
    try {
      await apiClient.signOutGitHub();
      await fetchStatus();
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  };

  if (loading) {
    return <Spinner size="extra-tiny" aria-label="Loading GitHub auth status" />;
  }

  if (error) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error}</MessageBarBody>
      </MessageBar>
    );
  }

  if (status === 'signed_in') {
    return (
      <div className={styles.root}>
        <Text size={200}>Signed in as {login}</Text>
        <Button appearance="subtle" onClick={() => void handleSignOut()}>
          Sign out
        </Button>
      </div>
    );
  }

  if (userCode && verificationUri) {
    return (
      <div className={styles.deviceFlow}>
        <Text size={200}>
          Go to{' '}
          <a href={verificationUri} target="_blank" rel="noreferrer" className={styles.link}>
            {verificationUri}
          </a>
        </Text>
        <Text>
          Enter code: <span className={styles.code}>{userCode}</span>
        </Text>
        {polling && <Spinner size="extra-tiny" label="Waiting for authorization" />}
        {flowError && <Text className={styles.errorText}>{flowError}</Text>}
      </div>
    );
  }

  return (
    <div className={styles.root}>
      {flowError && <Text className={styles.errorText}>{flowError}</Text>}
      <Button appearance="secondary" disabled={polling} onClick={() => void handleSignIn()}>
        Sign in with GitHub
      </Button>
    </div>
  );
}
