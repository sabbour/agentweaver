import { useEffect, useState } from 'react';
import { BrowserRouter, Link, Route, Routes } from 'react-router-dom';
import {
  FluentProvider,
  Spinner,
  Title1,
  makeStyles,
  tokens,
  webLightTheme,
} from '@fluentui/react-components';
import { ProjectGalleryPage } from './pages/ProjectGalleryPage';
import { ProjectPage } from './pages/ProjectPage';
import { ProjectSettingsPage } from './pages/ProjectSettingsPage';
import { WatchPage } from './pages/WatchPage';
import { SettingsPage } from './pages/SettingsPage';
import { GitHubSignIn } from './components/GitHubSignIn';
import { SignInPage } from './pages/SignInPage';
import { apiClient } from './api/apiClient';

const useStyles = makeStyles({
  app: {
    minHeight: '100vh',
    backgroundColor: tokens.colorNeutralBackground2,
  },
  header: {
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXXL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  brand: {
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
  },
  headerNav: {
    display: 'flex',
    gap: tokens.spacingHorizontalL,
    alignItems: 'center',
  },
  navLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    fontSize: tokens.fontSizeBase300,
    ':hover': {
      color: tokens.colorNeutralForeground1,
    },
  },
  main: {
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
  },
});

function Shell() {
  const styles = useStyles();
  return (
    <div className={styles.app}>
      <header className={styles.header}>
        <Link to="/" className={styles.brand}>
          <Title1>Scaffolder</Title1>
        </Link>
        <nav className={styles.headerNav} aria-label="Main navigation">
          <Link to="/settings" className={styles.navLink}>Settings</Link>
          <GitHubSignIn />
        </nav>
      </header>
      <main className={styles.main}>
        <Routes>
          <Route path="/" element={<ProjectGalleryPage />} />
          <Route path="/projects/:projectId" element={<ProjectPage />} />
          <Route path="/projects/:projectId/settings" element={<ProjectSettingsPage />} />
          <Route path="/watch/:runId" element={<WatchPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </main>
    </div>
  );
}

const useAppStyles = makeStyles({
  loadingScreen: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
  },
});

function AuthGate() {
  const styles = useAppStyles();
  const [authChecked, setAuthChecked] = useState(false);
  const [signedIn, setSignedIn] = useState(false);

  useEffect(() => {
    let cancelled = false;
    apiClient.getGitHubAuthStatus()
      .then((res) => {
        if (!cancelled) {
          setSignedIn(res.status === 'signed_in');
          setAuthChecked(true);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setSignedIn(false);
          setAuthChecked(true);
        }
      });
    return () => { cancelled = true; };
  }, []);

  if (!authChecked) {
    return (
      <div className={styles.loadingScreen}>
        <Spinner size="large" />
      </div>
    );
  }

  if (!signedIn) {
    return <SignInPage />;
  }

  return <Shell />;
}

function App() {
  return (
    <FluentProvider theme={webLightTheme}>
      <BrowserRouter>
        <AuthGate />
      </BrowserRouter>
    </FluentProvider>
  );
}

export default App;
