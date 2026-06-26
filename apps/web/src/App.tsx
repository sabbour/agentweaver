import { useEffect, useState } from 'react';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import {
  FluentProvider,
  Spinner,
  makeStyles,
  tokens,
  webLightTheme,
} from '@fluentui/react-components';
import { ProjectGalleryPage } from './pages/ProjectGalleryPage';
import { ProjectPage } from './pages/ProjectPage';
import { ProjectSettingsPage } from './pages/ProjectSettingsPage';
import { WatchPage } from './pages/WatchPage';
import { WorkflowRunPage } from './pages/WorkflowRunPage';
import { CoordinatorRunPage } from './pages/CoordinatorRunPage';
import { TeamPage } from './pages/TeamPage';
import { CastingWizardPage } from './pages/CastingWizardPage';
import { MemoriesPage } from './pages/MemoriesPage';
import { WorkflowsPage } from './pages/WorkflowsPage';
import { SignInPage } from './pages/SignInPage';
import { DiagnosticsPage } from './pages/DiagnosticsPage';
import { HeartbeatPage } from './pages/HeartbeatPage';
import { DashboardPage } from './pages/DashboardPage';
import { FlowPage } from './pages/FlowPage';
import { OrchestrationsPage } from './pages/OrchestrationsPage';
import { WorkspacePage } from './pages/WorkspacePage';
import { OverviewPage } from './pages/OverviewPage';
import { AppShell } from './components/shell/AppShell';
import { apiClient } from './api/apiClient';
import { bindSessionLogin, captureSessionAuthFromUrl, clearSessionAuth, getSessionLogin, getSessionToken } from './config';

function Shell() {
  return (
    <AppShell>
      <Routes>
        {/* Global (non-project) destinations */}
        <Route path="/" element={<OverviewPage />} />
        <Route path="/overview" element={<OverviewPage />} />
        <Route path="/projects" element={<ProjectGalleryPage />} />

        {/* Project-scoped */}
        <Route path="/projects/:projectId" element={<DashboardPage />} />
        <Route path="/projects/:projectId/board" element={<ProjectPage />} />
        <Route path="/projects/:projectId/flow" element={<FlowPage />} />
        <Route path="/projects/:projectId/orchestrations" element={<OrchestrationsPage />} />
        <Route path="/projects/:projectId/workspace" element={<WorkspacePage />} />
        <Route path="/projects/:projectId/settings" element={<ProjectSettingsPage />} />
        <Route path="/projects/:projectId/team" element={<TeamPage />} />
        <Route path="/projects/:projectId/team/cast" element={<CastingWizardPage />} />
        <Route path="/projects/:projectId/memories" element={<MemoriesPage />} />
        <Route path="/projects/:projectId/workflows" element={<WorkflowsPage />} />
        <Route path="/projects/:projectId/diagnostics" element={<DiagnosticsPage />} />
        <Route path="/projects/:projectId/heartbeat" element={<HeartbeatPage />} />
        <Route path="/projects/:projectId/runs/:runId/execution/:executionId" element={<WatchPage />} />
        <Route path="/projects/:projectId/runs/:runId/workflow" element={<WorkflowRunPage />} />
        <Route path="/projects/:projectId/orchestrations/:runId" element={<CoordinatorRunPage />} />
      </Routes>
    </AppShell>
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
    captureSessionAuthFromUrl();
    apiClient.getGitHubAuthStatus()
      .then((res) => {
        if (cancelled) return;
        if (res.status === 'signed_in') {
          const storedLogin = getSessionLogin();
          if (getSessionToken() && storedLogin && res.login && storedLogin !== res.login) {
            clearSessionAuth();
            setSignedIn(false);
          } else {
            bindSessionLogin(res.login);
            setSignedIn(true);
          }
        } else {
          clearSessionAuth();
          setSignedIn(false);
        }
        setAuthChecked(true);
      })
      .catch(() => {
        if (!cancelled) {
          clearSessionAuth();
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
