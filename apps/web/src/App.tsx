import { apiClient } from './api/apiClient';
import { ApiError } from './api/client';
import { FluentProvider } from '@fluentui/react-components';
import { agentweaverLightTheme } from './theme';
import { AppShell } from './components/shell/AppShell';
import {
  captureSessionAuthFromUrl,
  clearSessionAuth,
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
import { PlatformSettingsPage } from './pages/PlatformSettingsPage';
import { SessionsPage } from './pages/SessionsPage';
import { SettingsPage } from './pages/SettingsPage';
import { SignInPage, SignInPageLoading } from './pages/SignInPage';
import { SkillsPage } from './pages/SkillsPage';
import { TeamPage } from './pages/TeamPage';
import { WorkflowsPage } from './pages/WorkflowsPage';
import { WorkspacePage } from './pages/WorkspacePage';
import { CoordinatorRunRoute } from './routes/CoordinatorRunRoute';
import { AssistantRoute } from './routes/AssistantRoute';
import { useCallback, useEffect, useState } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useParams } from 'react-router-dom';
import { PageContainer, PageHeader, SetupReadiness } from './components/ui';
import {
  clearRequiredSetupPending,
  hasRequiredSetupPending,
} from './components/onboarding/firstRunTourStorage';

function Shell({
  isPlatformAdmin,
  onAiConfigurationChanged,
  startFirstRunTour,
  tourUserKey,
  onFirstRunTourStarted,
}: {
  isPlatformAdmin: boolean;
  onAiConfigurationChanged?: () => void;
  startFirstRunTour?: boolean;
  tourUserKey?: string | null;
  onFirstRunTourStarted?: () => void;
}) {
  return (
    <AppShell
      isPlatformAdmin={isPlatformAdmin}
      startFirstRunTour={startFirstRunTour}
      tourUserKey={tourUserKey}
      onFirstRunTourStarted={onFirstRunTourStarted}
    >
      <Routes>
        {/* Global (non-project) destinations */}
        <Route path="/" element={<OverviewPage />} />
        <Route path="/overview" element={<OverviewPage />} />
        <Route path="/projects" element={<ProjectGalleryPage />} />
        <Route path="/sessions" element={<SessionsPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route
          path="/platform-settings"
          element={isPlatformAdmin
            ? <PlatformSettingsPage onRetryAccess={onAiConfigurationChanged} />
            : <Navigate to="/overview" replace />}
        />
        {/* Legacy operator-dock bookmark (#346) — the dock is retired; route old links
            straight through the assistant page. */}
        <Route path="/console" element={<Navigate to="/assistant" replace />} />
        {/* #346 MCP-driven operator assistant. Optional ?project=<id> seeds project-aware tools. */}
        <Route path="/assistant" element={<AssistantRoute />} />
        <Route path="/observability" element={<ObservabilityRedirectPage />} />
        <Route path="/observability/traces" element={<ObservabilityRedirectPage suffix="/traces" />} />
        <Route path="/observability/agents" element={<ObservabilityRedirectPage suffix="/agents" />} />

        {/* Project-scoped */}
        <Route path="/projects/:projectId" element={<DashboardPage />} />
        <Route path="/projects/:projectId/board" element={<ProjectPage />} />
        <Route path="/projects/:projectId/flow" element={<FlowPage />} />
        <Route path="/projects/:projectId/orchestrations" element={<OrchestrationsPage />} />
        <Route path="/projects/:projectId/sessions" element={<LegacyProjectSessionsRedirect />} />
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

function LegacyProjectSessionsRedirect() {
  const { projectId } = useParams<{ projectId: string }>();
  const search = projectId ? `?project=${encodeURIComponent(projectId)}` : '';
  return <Navigate to={`/sessions${search}`} replace />;
}

/**
 * Turns a session-check failure into a message the user can actually act on, instead of a silent
 * "you're signed out" with no explanation. A missing/invalid bearer token (401) is the normal,
 * expected "not signed in yet" case and stays quiet; everything else (a platform-role denial, a
 * 404 from a misconfigured deployment, a 5xx, a network failure) is surfaced verbatim so the
 * failure is diagnosable from the UI alone.
 */
function describeSessionCheckError(err: unknown): string | null {
  if (err instanceof ApiError) {
    if (err.status === 401) return null;
    const payload = err.payload as
      | {
          error?: string;
          entra_object_id?: string;
          entra_tenant_id?: string;
          entra_client_id?: string;
          roles_found_on_token?: string[];
        }
      | null;
    if (payload?.error) {
      const details: string[] = [];
      if (payload.entra_client_id) details.push(`app ${payload.entra_client_id}`);
      if (payload.entra_tenant_id) details.push(`tenant ${payload.entra_tenant_id}`);
      if (payload.entra_object_id) details.push(`user ${payload.entra_object_id}`);
      if (payload.roles_found_on_token) {
        details.push(
          payload.roles_found_on_token.length > 0
            ? `roles found: ${payload.roles_found_on_token.join(', ')}`
            : 'no roles found on token',
        );
      }
      return details.length > 0 ? `${payload.error} (${details.join(', ')})` : payload.error;
    }
    return `Sign-in check failed (HTTP ${err.status}). ${err.body || 'No further details from the server.'}`;
  }
  if (err instanceof Error) return `Sign-in check failed: ${err.message}`;
  return 'Sign-in check failed for an unknown reason.';
}

function AuthGate() {
  const [authChecked, setAuthChecked] = useState(false);
  const [signedIn, setSignedIn] = useState(false);
  const [hasPlatformAccess, setHasPlatformAccess] = useState(false);
  const [isPlatformAdmin, setIsPlatformAdmin] = useState(false);
  const [aiConfigured, setAiConfigured] = useState(true);
  const [tourUserKey, setTourUserKey] = useState<string | null>(null);
  const [startFirstRunTour, setStartFirstRunTour] = useState(false);
  const [requiredSetupPending, setRequiredSetupPending] = useState(hasRequiredSetupPending);
  const [sessionError, setSessionError] = useState<string | null>(null);

  const runSessionCheck = useCallback(async (cancelledRef?: { cancelled: boolean }) => {
    setAuthChecked(false);
    setSessionError(null);
    setAiConfigured(true);
    setSignedIn(false);
    setHasPlatformAccess(false);
    setIsPlatformAdmin(false);
    setTourUserKey(null);

    try {
      await captureSessionAuthFromUrl();
      await apiClient.getServerInfo();
      if (cancelledRef?.cancelled) return;
      const session = await apiClient.getAuthSession();
      if (cancelledRef?.cancelled) return;
      setSessionError(null);
      if (!session.authenticated) {
        clearRequiredSetupPending();
        setRequiredSetupPending(false);
        clearSessionAuth();
        setSignedIn(false);
        setHasPlatformAccess(false);
        setIsPlatformAdmin(false);
        setAiConfigured(true);
        setAuthChecked(true);
        return;
      }
      const roles = session.platform_roles;
      setHasPlatformAccess(roles.length > 0);
      setIsPlatformAdmin(roles.includes('PlatformAdmin'));
      setAiConfigured(session.ai_configured);
      setTourUserKey(session.entra_object_id ?? session.login);
      setSignedIn(true);
      setAuthChecked(true);
    } catch (err: unknown) {
      if (cancelledRef?.cancelled) return;
      clearSessionAuth();
      if (err instanceof ApiError && err.status === 401) {
        clearRequiredSetupPending();
        setRequiredSetupPending(false);
      }
      setSignedIn(false);
      setHasPlatformAccess(false);
      setIsPlatformAdmin(false);
      setTourUserKey(null);
      setAiConfigured(true);
      setSessionError(describeSessionCheckError(err));
      setAuthChecked(true);
    }
  }, []);

  const completeRequiredSetup = useCallback(() => {
    clearRequiredSetupPending();
    setRequiredSetupPending(false);
    setStartFirstRunTour(true);
    void runSessionCheck();
  }, [runSessionCheck]);

  const handleFirstRunTourStarted = useCallback(() => {
    setStartFirstRunTour(false);
  }, []);

  useEffect(() => {
    const cancelledRef = { cancelled: false };
    const timer = window.setTimeout(() => {
      void runSessionCheck(cancelledRef);
    }, 0);
    return () => {
      cancelledRef.cancelled = true;
      window.clearTimeout(timer);
    };
  }, [runSessionCheck]);

  if (!authChecked) {
    return <SignInPageLoading />;
  }

  if (!signedIn) {
    return <SignInPage sessionError={sessionError} />;
  }

  if (!hasPlatformAccess) {
    return (
      <PageContainer width="readable">
        <PageHeader
          title="Access denied"
          description="Your account is signed in, but no Agentweaver platform role is assigned."
        />
        <SetupReadiness
          model={{
            title: 'Access setup',
            description: 'A Platform Admin controls access through Microsoft Entra ID.',
            items: [{
              id: 'platform-role',
              title: 'Agentweaver platform role',
              description: 'Ask a Platform Admin to assign a role. Then reload this page.',
              requirement: 'required',
              status: 'unavailable',
            }],
          }}
        />
      </PageContainer>
    );
  }

  if (!aiConfigured || (isPlatformAdmin && requiredSetupPending)) {
    if (isPlatformAdmin) {
      return (
        <Routes>
          <Route
            path="/platform-settings"
            element={<PlatformSettingsPage setupRequired onRetryAccess={completeRequiredSetup} />}
          />
          <Route path="*" element={<Navigate to="/platform-settings" replace />} />
        </Routes>
      );
    }

    return (
      <PageContainer width="readable">
        <PageHeader
          title="Model provider setup required"
          description="Agentweaver requires a model provider before AI work can start."
        />
        <SetupReadiness
          model={{
            title: 'Setup readiness',
            description: 'A Platform Admin manages the model provider for this deployment.',
            items: [{
              id: 'model-provider',
              title: 'Model provider',
              description: 'Ask a Platform Admin to complete this setup.',
              requirement: 'required',
              status: 'unavailable',
            }],
          }}
        />
      </PageContainer>
    );
  }

  return (
    <Shell
      isPlatformAdmin={isPlatformAdmin}
      onAiConfigurationChanged={() => { void runSessionCheck(); }}
      startFirstRunTour={startFirstRunTour}
      tourUserKey={tourUserKey}
      onFirstRunTourStarted={handleFirstRunTourStarted}
    />
  );
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
