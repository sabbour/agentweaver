import { apiClient } from './api/apiClient';
import { FluentProvider, Spinner, makeStyles, tokens } from '@fluentui/react-components';
import { agentweaverLightTheme } from './theme';
import { AppShell } from './components/shell/AppShell';
import { ConsoleRouteRedirect } from './components/shell/ConsoleRouteRedirect';
import {
  bindSessionLogin,
  captureSessionAuthFromUrl,
  clearSessionAuth,
  getSessionLogin,
  getSessionToken,
} from './config';
import { CastingWizardPage } from './pages/CastingWizardPage';
import { ClusterPage } from './pages/ClusterPage';
import { DashboardPage } from './pages/DashboardPage';
import { DiagnosticsPage } from './pages/DiagnosticsPage';
import { FlowPage } from './pages/FlowPage';
import { AgentMemoryPage } from './pages/AgentMemoryPage';
import { HeartbeatPage } from './pages/HeartbeatPage';
import { MemoriesPage } from './pages/MemoriesPage';
import { ObservabilityAgentsPage } from './pages/observability/ObservabilityAgentsPage';
import { ObservabilityOverviewPage } from './pages/observability/ObservabilityOverviewPage';
import { ObservabilityRedirectPage } from './pages/observability/ObservabilityRedirectPage';
import { ObservabilityTracesPage } from './pages/observability/ObservabilityTracesPage';
import { OrchestrationsPage } from './pages/OrchestrationsPage';
import { OverviewPage } from './pages/OverviewPage';
import { ProjectGalleryPage } from './pages/ProjectGalleryPage';
import { ProjectPage } from './pages/ProjectPage';
import { ProjectSettingsPage } from './pages/ProjectSettingsPage';
import { SignInPage } from './pages/SignInPage';
import { SkillsPage } from './pages/SkillsPage';
import { TeamPage } from './pages/TeamPage';
import { WorkflowsPage } from './pages/WorkflowsPage';
import { WorkspacePage } from './pages/WorkspacePage';
import { CoordinatorRunRoute } from './routes/CoordinatorRunRoute';
import { AssistantRoute } from './routes/AssistantRoute';
import { useEffect, useState } from 'react';
import { BrowserRouter, Route, Routes } from 'react-router-dom';

const useAppLoadingStyles = makeStyles({
  screen: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
  },
});

function Shell() {
  return (
    <AppShell>
      <Routes>
        {/* Global (non-project) destinations */}
        <Route path="/" element={<OverviewPage />} />
        <Route path="/overview" element={<OverviewPage />} />
        <Route path="/projects" element={<ProjectGalleryPage />} />
        <Route path="/console" element={<ConsoleRouteRedirect />} />
        {/* #346 MCP-driven operator assistant — feature-flagged (?assistant=1), additive rollout. */}
        <Route path="/assistant" element={<AssistantRoute />} />
        <Route path="/observability" element={<ObservabilityRedirectPage />} />
        <Route path="/observability/traces" element={<ObservabilityRedirectPage suffix="/traces" />} />
        <Route path="/observability/agents" element={<ObservabilityRedirectPage suffix="/agents" />} />

        {/* Project-scoped */}
        <Route path="/projects/:projectId" element={<DashboardPage />} />
        <Route path="/projects/:projectId/board" element={<ProjectPage />} />
        <Route path="/projects/:projectId/flow" element={<FlowPage />} />
        <Route path="/projects/:projectId/orchestrations" element={<OrchestrationsPage />} />
        <Route path="/projects/:projectId/workspace" element={<WorkspacePage />} />
        <Route path="/projects/:projectId/settings" element={<ProjectSettingsPage />} />
        <Route path="/projects/:projectId/team" element={<TeamPage />} />
        <Route path="/projects/:projectId/team/:agentName/memory" element={<AgentMemoryPage />} />
        <Route path="/projects/:projectId/team/cast" element={<CastingWizardPage />} />
        <Route path="/projects/:projectId/memories" element={<MemoriesPage />} />
        <Route path="/projects/:projectId/skills" element={<SkillsPage />} />
        <Route path="/projects/:projectId/observability" element={<ObservabilityOverviewPage />} />
        <Route path="/projects/:projectId/observability/traces" element={<ObservabilityTracesPage />} />
        <Route path="/projects/:projectId/observability/agents" element={<ObservabilityAgentsPage />} />
        <Route path="/projects/:projectId/workflows" element={<WorkflowsPage />} />
        <Route path="/projects/:projectId/diagnostics" element={<DiagnosticsPage />} />
        <Route path="/projects/:projectId/heartbeat" element={<HeartbeatPage />} />
        <Route path="/projects/:projectId/cluster" element={<ClusterPage />} />
        <Route path="/projects/:projectId/orchestrations/:runId" element={<CoordinatorRunRoute />} />
      </Routes>
    </AppShell>
  );
}

function AuthGate() {
  const [authChecked, setAuthChecked] = useState(false);
  const [signedIn, setSignedIn] = useState(false);

  useEffect(() => {
    let cancelled = false;
    captureSessionAuthFromUrl()
      .then(() => apiClient.getGitHubAuthStatus())
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

  const loadingStyles = useAppLoadingStyles();
  if (!authChecked) {
    return (
      <div className={loadingStyles.screen}>
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
    <FluentProvider theme={agentweaverLightTheme}>
      <BrowserRouter>
        <AuthGate />
      </BrowserRouter>
    </FluentProvider>
  );
}

export default App;
