import App from '../App';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const retrySpy = vi.fn();

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    getAuthSession: vi.fn(),
  },
}));

vi.mock('../config', () => ({
  captureSessionAuthFromUrl: vi.fn().mockResolvedValue(undefined),
  clearSessionAuth: vi.fn(),
}));

vi.mock('../components/shell/AppShell', () => ({
  AppShell: ({
    children,
    startFirstRunTour,
    tourUserKey,
  }: {
    children: ReactNode;
    startFirstRunTour?: boolean;
    tourUserKey?: string | null;
  }) => (
    <div data-testid="app-shell">
      {startFirstRunTour && <div>Product tour requested for {tourUserKey}</div>}
      {children}
    </div>
  ),
}));

vi.mock('../pages/PlatformSettingsPage', () => ({
  PlatformSettingsPage: ({
    setupRequired,
    onRetryAccess,
  }: {
    setupRequired?: boolean;
    onRetryAccess?: () => void;
  }) => (
    <div>
      <div>Platform settings</div>
      <div>{setupRequired ? 'Setup required' : 'Setup optional'}</div>
      <button type="button" onClick={() => { retrySpy(); onRetryAccess?.(); }}>Retry access</button>
    </div>
  ),
}));

vi.mock('../pages/OverviewPage', () => ({ OverviewPage: () => <div>Overview page</div> }));
vi.mock('../pages/SignInPage', () => ({
  SignInPage: ({ sessionError }: { sessionError?: string | null }) => <div>{sessionError ?? 'Sign in'}</div>,
  SignInPageLoading: () => <div>Loading</div>,
}));
vi.mock('../pages/CastingWizardPage', () => ({ CastingWizardPage: () => null }));
vi.mock('../pages/ClusterPage', () => ({ ClusterPage: () => null }));
vi.mock('../pages/DashboardPage', () => ({ DashboardPage: () => null }));
vi.mock('../pages/DiagnosticsPage', () => ({ DiagnosticsPage: () => null }));
vi.mock('../pages/FlowPage', () => ({ FlowPage: () => null }));
vi.mock('../pages/AgentMemoryPage', () => ({ AgentMemoryPage: () => null }));
vi.mock('../pages/HeartbeatPage', () => ({ HeartbeatPage: () => null }));
vi.mock('../pages/MemoriesPage', () => ({ MemoriesPage: () => null }));
vi.mock('../pages/observability/ObservabilityAgentsPage', () => ({ ObservabilityAgentsPage: () => null }));
vi.mock('../pages/observability/ObservabilityOverviewPage', () => ({ ObservabilityOverviewPage: () => null }));
vi.mock('../pages/observability/ObservabilityRedirectPage', () => ({ ObservabilityRedirectPage: () => null }));
vi.mock('../pages/observability/ObservabilityTracesPage', () => ({ ObservabilityTracesPage: () => null }));
vi.mock('../pages/OrchestrationsPage', () => ({ OrchestrationsPage: () => null }));
vi.mock('../pages/ProjectGalleryPage', () => ({ ProjectGalleryPage: () => null }));
vi.mock('../pages/ProjectPage', () => ({ ProjectPage: () => null }));
vi.mock('../pages/ProjectSettingsPage', () => ({ ProjectSettingsPage: () => null }));
vi.mock('../pages/SessionsPage', () => ({ SessionsPage: () => null }));
vi.mock('../pages/SettingsPage', () => ({ SettingsPage: () => null }));
vi.mock('../pages/SkillsPage', () => ({ SkillsPage: () => null }));
vi.mock('../pages/TeamPage', () => ({ TeamPage: () => null }));
vi.mock('../pages/WorkflowsPage', () => ({ WorkflowsPage: () => null }));
vi.mock('../pages/WorkspacePage', () => ({ WorkspacePage: () => null }));
vi.mock('../routes/CoordinatorRunRoute', () => ({ CoordinatorRunRoute: () => null }));
vi.mock('../routes/AssistantRoute', () => ({ AssistantRoute: () => null }));

describe('App auth gate', () => {
  beforeEach(() => {
    cleanup();
    retrySpy.mockReset();
    sessionStorage.clear();
    localStorage.clear();
    window.history.pushState({}, '', '/projects/proj-1');
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: 'C:\\data',
    });
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('shows sign-in instead of the AI lockout when the session check returns 401', async () => {
    vi.mocked(apiClient.getAuthSession).mockRejectedValue(new ApiError(401, '{"error":"unauthorized"}'));

    render(<App />);

    expect(await screen.findByText('Sign in')).toBeDefined();
    expect(screen.queryByText(/Model provider setup required/)).toBeNull();
  });

  it('redirects platform admins to platform settings when AI is not configured', async () => {
    vi.mocked(apiClient.getAuthSession).mockResolvedValue({
      authenticated: true,
      auth_mode: 'entra',
      display_name: 'Admin',
      email: 'admin@example.com',
      login: 'admin',
      avatar_url: null,
      entra_object_id: 'entra-admin',
      platform_roles: ['PlatformAdmin'],
      ai_configured: false,
    });

    render(<App />);

    expect(await screen.findByText('Platform settings')).toBeDefined();
    expect(screen.getByText('Setup required')).toBeDefined();
    await waitFor(() => expect(window.location.pathname).toBe('/platform-settings'));
  });

  it('lets a platform admin retry the AI configuration check after fixing setup', async () => {
    vi.mocked(apiClient.getAuthSession)
      .mockResolvedValueOnce({
        authenticated: true,
        auth_mode: 'entra',
        display_name: 'Admin',
        email: 'admin@example.com',
        login: 'admin',
        avatar_url: null,
        entra_object_id: 'entra-admin',
        platform_roles: ['PlatformAdmin'],
        ai_configured: false,
      })
      .mockResolvedValueOnce({
        authenticated: true,
        auth_mode: 'entra',
        display_name: 'Admin',
        email: 'admin@example.com',
        login: 'admin',
        avatar_url: null,
        entra_object_id: 'entra-admin',
        platform_roles: ['PlatformAdmin'],
        ai_configured: true,
      });

    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'Retry access' }));

    await waitFor(() => expect(retrySpy).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByTestId('app-shell')).toBeDefined());
    expect(screen.getByText('Product tour requested for entra-admin')).toBeDefined();
  });

  it('does not start the tour during a normal configured sign-in', async () => {
    vi.mocked(apiClient.getAuthSession).mockResolvedValue({
      authenticated: true,
      auth_mode: 'entra',
      display_name: 'Admin',
      email: 'admin@example.com',
      login: 'admin',
      avatar_url: null,
      entra_object_id: 'entra-admin',
      platform_roles: ['PlatformAdmin'],
      ai_configured: true,
    });

    render(<App />);

    expect(await screen.findByTestId('app-shell')).toBeDefined();
    expect(screen.queryByText(/Product tour requested/)).toBeNull();
  });

  it('keeps the OAuth return in setup until the admin continues', async () => {
    sessionStorage.setItem('agentweaver.requiredSetup.pending', '1');
    window.history.pushState({}, '', '/platform-settings?copilot_app_auth=success');
    vi.mocked(apiClient.getAuthSession).mockResolvedValue({
      authenticated: true,
      auth_mode: 'entra',
      display_name: 'Admin',
      email: 'admin@example.com',
      login: null,
      avatar_url: null,
      entra_object_id: 'entra-admin',
      platform_roles: ['PlatformAdmin'],
      ai_configured: true,
    });

    render(<App />);

    expect(await screen.findByText('Setup required')).toBeDefined();
    expect(screen.queryByTestId('app-shell')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Retry access' }));

    await waitFor(() => expect(screen.getByTestId('app-shell')).toBeDefined());
    expect(screen.getByText('Product tour requested for entra-admin')).toBeDefined();
    expect(sessionStorage.getItem('agentweaver.requiredSetup.pending')).toBeNull();
  });

  it('lets a non-admin enter the app when platform AI is not configured', async () => {
    vi.mocked(apiClient.getAuthSession).mockResolvedValue({
      authenticated: true,
      auth_mode: 'entra',
      display_name: 'Member',
      email: 'member@example.com',
      login: 'member',
      avatar_url: null,
      entra_object_id: 'entra-member',
      platform_roles: ['Contributor'],
      ai_configured: false,
    });

    render(<App />);

    expect(await screen.findByTestId('app-shell')).toBeDefined();
    expect(screen.queryByText('Model provider setup required')).toBeNull();
    expect(screen.queryByText('Platform settings')).toBeNull();
  });

  it('shows access denied instead of the AI lockout when no platform role is assigned', async () => {
    vi.mocked(apiClient.getAuthSession).mockResolvedValue({
      authenticated: true,
      auth_mode: 'entra',
      display_name: 'No Role',
      email: 'norole@example.com',
      login: 'norole',
      avatar_url: null,
      entra_object_id: 'entra-norole',
      platform_roles: [],
      ai_configured: false,
    });

    render(<App />);

    expect(await screen.findByText('Access denied')).toBeDefined();
    expect(screen.getByText(/no Agentweaver platform role is assigned/i)).toBeDefined();
    expect(screen.queryByText(/Model provider setup required/)).toBeNull();
  });
});
