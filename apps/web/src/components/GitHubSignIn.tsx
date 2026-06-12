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
import { LockClosedRegular, CopyRegular, CheckmarkRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { GitHubAuthStatus } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  deviceCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: '0 0 2px rgba(0,0,0,0.12), 0 1px 2px rgba(0,0,0,0.14)',
    minWidth: '320px',
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  headerIcon: {
    color: tokens.colorBrandForeground1,
    fontSize: '20px',
    flexShrink: '0',
  },
  headerTitle: {
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  instruction: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  urlRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  link: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
    fontSize: tokens.fontSizeBase300,
    flexGrow: '1',
    ':hover': { textDecoration: 'underline' },
  },
  codeContainer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorBrandStroke2}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase600,
    letterSpacing: '0.15em',
    color: tokens.colorNeutralForeground1,
  },
  statusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  statusText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  actionsRow: {
    display: 'flex',
    justifyContent: 'flex-end',
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

  // Copy-to-clipboard state
  const [copiedCode, setCopiedCode] = useState(false);
  const [copiedUri, setCopiedUri] = useState(false);

  const handleCopyCode = async () => {
    if (userCode) {
      await navigator.clipboard.writeText(userCode);
      setCopiedCode(true);
      setTimeout(() => setCopiedCode(false), 2000);
    }
  };

  const handleCopyUri = async () => {
    if (verificationUri) {
      await navigator.clipboard.writeText(verificationUri);
      setCopiedUri(true);
      setTimeout(() => setCopiedUri(false), 2000);
    }
  };

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
      <div className={styles.deviceCard}>
        <div className={styles.cardHeader}>
          <LockClosedRegular className={styles.headerIcon} />
          <Text className={styles.headerTitle}>Sign in with GitHub</Text>
        </div>

        <Text className={styles.instruction}>
          Open the link below and enter the code to authorize.
        </Text>

        <div className={styles.urlRow}>
          <a href={verificationUri} target="_blank" rel="noreferrer" className={styles.link}>
            {verificationUri}
          </a>
          <Button
            appearance="subtle"
            size="small"
            icon={copiedUri ? <CheckmarkRegular /> : <CopyRegular />}
            aria-label="Copy URL"
            onClick={() => void handleCopyUri()}
          />
        </div>

        <div className={styles.codeContainer}>
          <span className={styles.code}>{userCode}</span>
          <Button
            appearance="subtle"
            size="small"
            icon={copiedCode ? <CheckmarkRegular /> : <CopyRegular />}
            aria-label="Copy code"
            onClick={() => void handleCopyCode()}
          />
        </div>

        {polling && (
          <div className={styles.statusRow}>
            <Spinner size="extra-tiny" />
            <Text className={styles.statusText}>Waiting for GitHub authorization...</Text>
          </div>
        )}

        {flowError && <Text className={styles.errorText}>{flowError}</Text>}

        <div className={styles.actionsRow}>
          <Button
            appearance="subtle"
            size="small"
            onClick={() => {
              clearPollTimer();
              setPolling(false);
              setUserCode(null);
              setVerificationUri(null);
            }}
          >
            Cancel
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.root}>
      {flowError && <Text className={styles.errorText}>{flowError}</Text>}
      <Button appearance="primary" disabled={polling} onClick={() => void handleSignIn()}>
        Sign in with GitHub
      </Button>
    </div>
  );
}
