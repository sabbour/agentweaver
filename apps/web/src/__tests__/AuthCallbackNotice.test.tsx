import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AuthCallbackNotice } from '../components/AuthCallbackNotice';
import {
  PlatformCopilotAuthorizationResultNotice,
  RepoAppAuthorizationResultNotice,
  UserCopilotAuthorizationResultNotice,
} from '../components/GitHubAuthorizationResultNotices';
import { CopilotAuthorizationResultNotice } from '../components/CopilotAuthorizationResultNotice';
import { RepoAppInstallationResultNotice } from '../components/RepoAppInstallationResultNotice';

afterEach(() => cleanup());

const renderWithProvider = (ui: ReactNode) => render(
  <AzureFluentProvider>{ui}</AzureFluentProvider>,
);

describe('AuthCallbackNotice', () => {
  it.each([
    [CopilotAuthorizationResultNotice, 'success', 'Copilot App connected'],
    [CopilotAuthorizationResultNotice, 'human_entra_subject_required', 'Work account required'],
    [CopilotAuthorizationResultNotice, 'project_owner_required', 'Project owner required'],
    [CopilotAuthorizationResultNotice, 'authorization_transaction_invalid', 'Connection expired'],
    [CopilotAuthorizationResultNotice, 'authorization_transaction_consumed', 'Connection already completed'],
    [CopilotAuthorizationResultNotice, 'github_binding_unavailable', 'Copilot App unavailable'],
    [CopilotAuthorizationResultNotice, 'project_model_provider_reconnect_required', 'Reconnect required'],
    [RepoAppAuthorizationResultNotice, 'success', 'Repo App connected'],
    [RepoAppAuthorizationResultNotice, 'human_entra_subject_required', 'Work account required'],
    [RepoAppAuthorizationResultNotice, 'authorization_transaction_invalid', 'Connection expired'],
    [RepoAppAuthorizationResultNotice, 'authorization_transaction_consumed', 'Connection already completed'],
    [RepoAppAuthorizationResultNotice, 'github_binding_unavailable', 'Repo App unavailable'],
    [RepoAppAuthorizationResultNotice, 'rate_limited', 'Too many attempts'],
    [UserCopilotAuthorizationResultNotice, 'success', 'GitHub Copilot connected'],
    [UserCopilotAuthorizationResultNotice, 'human_entra_subject_required', 'Work account required'],
    [UserCopilotAuthorizationResultNotice, 'authorization_transaction_invalid', 'Connection expired'],
    [UserCopilotAuthorizationResultNotice, 'authorization_transaction_consumed', 'Connection already completed'],
    [UserCopilotAuthorizationResultNotice, 'github_binding_unavailable', 'GitHub Copilot unavailable'],
    [PlatformCopilotAuthorizationResultNotice, 'success', 'Platform Copilot connected'],
    [PlatformCopilotAuthorizationResultNotice, 'human_entra_subject_required', 'Work account required'],
    [PlatformCopilotAuthorizationResultNotice, 'platform_admin_required', 'Platform Admin required'],
    [PlatformCopilotAuthorizationResultNotice, 'authorization_transaction_invalid', 'Connection expired'],
    [PlatformCopilotAuthorizationResultNotice, 'authorization_transaction_consumed', 'Connection already completed'],
    [PlatformCopilotAuthorizationResultNotice, 'github_binding_unavailable', 'GitHub Copilot unavailable'],
    [RepoAppInstallationResultNotice, 'success', 'Repo App installed'],
    [RepoAppInstallationResultNotice, 'human_entra_subject_required', 'Work account required'],
    [RepoAppInstallationResultNotice, 'project_owner_required', 'Project owner required'],
    [RepoAppInstallationResultNotice, 'repository_not_connected', 'Connect a repository first'],
    [RepoAppInstallationResultNotice, 'installation_request_pending', 'Organization approval pending'],
    [RepoAppInstallationResultNotice, 'authorization_transaction_invalid', 'Installation expired'],
    [RepoAppInstallationResultNotice, 'authorization_transaction_consumed', 'Installation already completed'],
    [RepoAppInstallationResultNotice, 'github_binding_unavailable', 'Repo App unavailable'],
    [RepoAppInstallationResultNotice, 'installation_conflict', 'Installation already in use'],
    [RepoAppInstallationResultNotice, 'permission_changed', 'Permissions changed'],
    [RepoAppInstallationResultNotice, 'repository_not_found_in_installation', 'Repository access missing'],
  ] as const)('renders a clear title for an audited callback variant', (Component, code, title) => {
    renderWithProvider(<Component code={code} onDismiss={vi.fn()} />);
    const heading = screen.getByText(title);
    expect(heading).toBeDefined();
    expect(heading.closest('[role="alert"], [role="status"]')).not.toBeNull();
  });

  it('uses an alert for errors, supports dismissal, and never echoes unknown callback data', () => {
    const dismiss = vi.fn();
    renderWithProvider(
      <AuthCallbackNotice
        code="code=secret&state=secret"
        results={{}}
        fallback={{ intent: 'error', title: 'Not completed', message: 'Start again.' }}
        onDismiss={dismiss}
      />,
    );

    expect(screen.getByRole('alert')).toBeDefined();
    expect(screen.queryByText(/code=secret/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(dismiss).toHaveBeenCalledOnce();
  });
});
