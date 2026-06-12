import { useRef, useState } from 'react';
import {
  Button,
  Spinner,
  Text,
  Title1,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { LockClosedRegular, CopyRegular, CheckmarkRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';

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

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
    gap: tokens.spacingVerticalXXL,
  },
  branding: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalS,
  },
  tagline: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalXL,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: `0 4px 16px ${tokens.colorNeutralShadowAmbient}, 0 0 2px ${tokens.colorNeutralShadowKey}`,
    minWidth: '360px',
    maxWidth: '420px',
    width: '100%',
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

interface SignInPageProps {
  onSignedIn: () => void;
}

export function SignInPage({ onSignedIn }: SignInPageProps) {
  const styles = useStyles();

  const [userCode, setUserCode] = useState<string | null>(null);
  const [verificationUri, setVerificationUri] = useState<string | null>(null);
  const [polling, setPolling] = useState(false);
  const [flowError, setFlowError] = useState<string | null>(null);
  const [copiedCode, setCopiedCode] = useState(false);
  const [copiedUri, setCopiedUri] = useState(false);
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const clearPollTimer = () => {
    if (pollTimerRef.current !== null) {
      clearInterval(pollTimerRef.current);
      pollTimerRef.current = null;
    }
  };

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
              onSignedIn();
            } else if (result.status === 'expired' || result.status === 'denied') {
              clearPollTimer();
              setPolling(false);
              setUserCode(null);
              setVerificationUri(null);
              setFlowError(
                result.status === 'expired'
                  ? 'Authorization expired. Try again.'
                  : 'Authorization denied.',
              );
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

  return (
    <div className={styles.page}>
      <div className={styles.branding}>
        <Title1>Scaffolder</Title1>
        <Text className={styles.tagline}>Sign in with GitHub to get started</Text>
      </div>

      <div className={styles.card}>
        <div className={styles.cardHeader}>
          <LockClosedRegular className={styles.headerIcon} />
          <Text className={styles.headerTitle}>Sign in with GitHub</Text>
        </div>

        {!userCode && (
          <>
            {flowError && <Text className={styles.errorText}>{flowError}</Text>}
            <Button
              appearance="primary"
              disabled={polling}
              onClick={() => void handleSignIn()}
            >
              Sign in with GitHub
            </Button>
          </>
        )}

        {userCode && verificationUri && (
          <>
            <Text className={styles.instruction}>
              Open the link below and enter the code to authorize.
            </Text>

            <div className={styles.urlRow}>
              <a
                href={verificationUri}
                target="_blank"
                rel="noreferrer"
                className={styles.link}
              >
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
          </>
        )}
      </div>
    </div>
  );
}
