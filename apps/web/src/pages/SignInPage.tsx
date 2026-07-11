import { Button, makeStyles, Text, tokens } from '@fluentui/react-components';
import { GITHUB_AUTHORIZE_URL } from '../config';
import { GitHubIcon } from '../components/GitHubIcon';

// Flat, centered sign-in card on a plain neutral background. No marketing copy,
// no gradients, no decorative badges. Surfaces an OAuth error from the URL and
// starts the GitHub authorize redirect.
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
    width: 'min(440px, 100%)',
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
});

export function SignInPage() {
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

        <div>
          <Text as="h1" className={styles.heading}>Sign in</Text>
          <Text as="p" className={styles.subheading}>Sign in to continue to Agentweaver.</Text>
        </div>

        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={<GitHubIcon size={20} />}
            onClick={() => { window.location.href = GITHUB_AUTHORIZE_URL; }}
          >
            Sign in with GitHub
          </Button>
          {authError && (
            <Text role="alert" className={styles.error}>{authError}</Text>
          )}
        </div>
      </div>
    </div>
  );
}
