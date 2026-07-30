import { Button, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ENTRA_AUTHORIZE_URL, GITHUB_AUTHORIZE_URL } from '../config';
import { GitHubIcon } from '../components/GitHubIcon';
import type { AuthMode } from '../api/types';

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
  authMode?: AuthMode;
}

function authHeading(mode: AuthMode | undefined) {
  return mode === 'entra' ? 'Sign in with Microsoft Entra ID' : 'Sign in with GitHub';
}

function authCopy(mode: AuthMode | undefined) {
  return mode === 'entra'
    ? 'Start with your Microsoft Entra ID account. After that, link one or more GitHub accounts for repository access and Copilot-backed work.'
    : 'Use your GitHub account to continue to Agentweaver.';
}

export function SignInPage({ authMode = 'github-legacy' }: SignInPageProps) {
  const styles = useStyles();

  const params = new URLSearchParams(window.location.search);
  const authError = params.get('auth') === 'error' ? (params.get('reason') ?? 'Authentication failed.') : null;
  const primaryUrl = authMode === 'entra' ? ENTRA_AUTHORIZE_URL : GITHUB_AUTHORIZE_URL;

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <div className={styles.brand}>
          <img src="/agentweaver.png" alt="" className={styles.logo} />
          <Text className={styles.wordmark}>Agentweaver</Text>
        </div>

        <div>
          <Text as="h1" className={styles.heading}>{authHeading(authMode)}</Text>
          <Text as="p" className={styles.subheading}>{authCopy(authMode)}</Text>
        </div>

        {authMode === 'entra' && (
          <div className={styles.checklist}>
            <Text className={styles.checklistItem}>1. Sign in to Agentweaver with your Entra ID account.</Text>
            <Text className={styles.checklistItem}>2. Link at least one GitHub account before importing repositories or running GitHub/Copilot actions.</Text>
            <Text className={styles.note}>
              You can still browse Agentweaver before linking GitHub, but GitHub operations no longer use any shared fallback token.
            </Text>
          </div>
        )}

        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={authMode === 'entra' ? undefined : <GitHubIcon size={20} />}
            onClick={() => { window.location.href = primaryUrl; }}
          >
            {authMode === 'entra' ? 'Sign in with Microsoft Entra ID' : 'Sign in with GitHub'}
          </Button>
          {authMode === 'github-legacy' && (
            <Text className={styles.note}>
              This deployment signs in directly with GitHub.
            </Text>
          )}
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
