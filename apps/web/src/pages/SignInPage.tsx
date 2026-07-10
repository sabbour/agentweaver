import {
  Button,
  Text,
  Title1,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { GITHUB_AUTHORIZE_URL } from '../config';
import { GitHubIcon } from '../components/GitHubIcon';
import { AzureSurface } from '../components/azure/AzureLayout';

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalXXL,
  },
  card: {
    width: 'min(420px, 100%)',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalL,
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
      <AzureSurface className={styles.card} density="spacious" tone="raised">
        <div className={styles.branding}>
          <img src="/agentweaver.png" alt="Agentweaver" className={styles.logo} />
          <Title1>Agentweaver</Title1>
          <Text className={styles.tagline}>Build workflows from specialized agents</Text>
        </div>

        <Button
          appearance="primary"
          size="large"
          icon={<GitHubIcon size={20} />}
          onClick={() => { window.location.href = GITHUB_AUTHORIZE_URL; }}
        >
          Sign in with GitHub
        </Button>

        {authError && <Text className={styles.errorText}>{authError}</Text>}
      </AzureSurface>
    </div>
  );
}
