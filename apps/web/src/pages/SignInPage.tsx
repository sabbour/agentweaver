import {
  Text,
  Title1,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { GITHUB_AUTHORIZE_URL } from '../config';
import { GitHubIcon } from '../components/GitHubIcon';

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
    gap: tokens.spacingVerticalM,
  },
  branding: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalXS,
  },
  logo: {
    width: '160px',
    height: '160px',
    objectFit: 'contain',
  },
  tagline: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  githubButton: {
    backgroundColor: '#24292e',
    color: '#ffffff',
    border: 'none',
    borderRadius: '6px',
    padding: '12px 24px',
    fontSize: '16px',
    fontWeight: '600',
    cursor: 'pointer',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  errorText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
  },
});

export function SignInPage() {
  const styles = useStyles();

  const params = new URLSearchParams(window.location.search);
  const authError = params.get('auth') === 'error' ? (params.get('reason') ?? 'Authentication failed.') : null;

  return (
    <div className={styles.page}>
      <div className={styles.branding}>
        <img src="/agentweaver.png" alt="Agentweaver" className={styles.logo} />
        <Title1>Agentweaver</Title1>
        <Text className={styles.tagline}>Build workflows from specialized agents</Text>
      </div>

      <button
        className={styles.githubButton}
        onClick={() => { window.location.href = GITHUB_AUTHORIZE_URL; }}
      >
        <GitHubIcon size={20} />
        Sign in with GitHub
      </button>

      {authError && <Text className={styles.errorText}>{authError}</Text>}
    </div>
  );
}
