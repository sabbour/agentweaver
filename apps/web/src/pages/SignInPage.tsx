import { Button, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ENTRA_AUTHORIZE_URL } from '../config';
import type { ReactNode } from 'react';

/**
 * Entra sign-in screen for unauthenticated users. It explains server-side OAuth
 * failures from the URL separately from client-side session-check failures.
 */
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

/** Optional session-check error supplied by the application auth gate. */
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

/**
 * Presents a fresh sign-in action and translates known OAuth callback reasons into
 * actionable copy; it does not retry authentication automatically.
 */
export function SignInPage({ sessionError = null }: SignInPageProps) {

  const params = new URLSearchParams(window.location.search);
  const authError = params.get('auth') === 'error' ? (() => {
    switch (params.get('reason')) {
      case 'access_denied':
        return 'Sign-in was canceled. Select Sign in when you are ready to continue.';
      case 'state_mismatch':
      case 'missing_params':
        return 'This sign-in request expired or is no longer valid. Start a new sign-in.';
      case 'sign_in_failed':
        return 'Microsoft Entra ID could not complete sign-in. Try again or contact your administrator.';
      default:
        return 'Authentication could not be completed. Start a new sign-in.';
    }
  })() : null;
  // The 0.x page sends the user to the server-side Entra sign-in route. The
  // shared view below only draws the card.
  return (
    <SignInView
      heading="Sign in with Microsoft Entra ID"
      sessionError={sessionError}
      action={<Button appearance="primary" onClick={() => { window.location.href = ENTRA_AUTHORIZE_URL; }}>Sign in with Microsoft Entra ID</Button>}
      error={authError}
    />
  );
}

/**
 * Retains the sign-in presentation while each product line owns authorization.
 * The caller supplies the heading, the sign-in button (`action`), and any error
 * text. The view starts no sign-in flow by itself.
 */
export function SignInView({ heading, sessionError, action, error, note }: {
  heading: string;
  sessionError?: string | null;
  action: ReactNode;
  error?: string | null;
  note?: ReactNode;
}) {
  const styles = useStyles();
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
          <Text as="h1" className={styles.heading}>{heading}</Text>
          <Text as="p" className={styles.subheading}>
            Run governed teams of AI agents on your organization&apos;s infrastructure.
          </Text>
        </div>

        <div className={styles.actions}>
          {action}
          {/* role="alert" makes screen readers announce a sign-in failure. */}
          {error && (
            <Text role="alert" className={styles.error}>{error}</Text>
          )}
        </div>
        {note && <Text as="p" className={styles.note}>{note}</Text>}
      </div>
    </div>
  );
}

/** Compact loading surface used while authentication options are being checked. */
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
