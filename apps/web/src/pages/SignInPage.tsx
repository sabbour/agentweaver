import { Button, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ENTRA_AUTHORIZE_URL } from '../config';

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground3,
    padding: tokens.spacingVerticalXXL,
  },
  card: {
    width: 'min(520px, 100%)',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow16,
  },
  brand: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  logo: {
    width: '24px',
    height: '24px',
    objectFit: 'contain',
  },
  wordmark: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  heading: {
    display: 'block',
    marginTop: tokens.spacingVerticalM,
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  subheading: {
    display: 'block',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  actions: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    marginTop: tokens.spacingVerticalXS,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  note: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  checklist: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  checklistItem: {
    color: tokens.colorNeutralForeground2,
  },
});

export interface SignInPageProps {
  /**
   * A session-check failure surfaced by AuthGate (apps/web/src/App.tsx) — e.g. `/api/auth/session`
   * returning an unexpected status, a platform-role denial, or a network error. Distinct from
   * `authError` below (which reads `?auth=error&reason=` set by the *server-side* OAuth redirect
   * on failure): this covers failures that happen client-side, after redirect, so the user always
   * sees why they landed back on the sign-in screen instead of silently retrying forever.
   */
  sessionError?: string | null;
}

export function SignInPage({ sessionError = null }: SignInPageProps) {
  const styles = useStyles();

  const params = new URLSearchParams(window.location.search);
  const authError = params.get('auth') === 'error' ? (params.get('reason') ?? 'Authentication failed.') : null;
  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <div className={styles.brand}>
          <img src="/agentweaver.png" alt="" className={styles.logo} />
          <Text className={styles.wordmark}>Agentweaver</Text>
        </div>

        {sessionError && (
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Sign-in status did not load</MessageBarTitle>
              {sessionError}
            </MessageBarBody>
          </MessageBar>
        )}

        <div>
          <Text as="h1" className={styles.heading}>Sign in with Microsoft Entra ID</Text>
          <Text as="p" className={styles.subheading}>Use your organization account to continue to Agentweaver.</Text>
        </div>

        <div className={styles.checklist}>
          <Text className={styles.checklistItem}>Sign in to Agentweaver with your Entra ID account.</Text>
          <Text className={styles.note}>
            Authorize the Repo App or Copilot App when a project needs its respective GitHub capability.
          </Text>
        </div>

        <div className={styles.actions}>
          <Button
            appearance="primary"
            onClick={() => { window.location.href = ENTRA_AUTHORIZE_URL; }}
          >
            Sign in with Microsoft Entra ID
          </Button>
          {authError && (
            <Text role="alert" className={styles.error}>{authError}</Text>
          )}
        </div>
      </div>
    </div>
  );
}

export function SignInPageLoading() {
  const styles = useStyles();
  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <Spinner label="Loading sign-in options" />
      </div>
    </div>
  );
}
