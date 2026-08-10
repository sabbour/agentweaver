import { AzureFluentProvider } from '../copilot-fluent-system';
import { SignInPage } from '../pages/SignInPage';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

afterEach(() => cleanup());

describe('SignInPage', () => {
  it('renders Entra-first copy for Entra deployments', () => {
    render(
      <AzureFluentProvider density="compact">
        <SignInPage authMode="entra" />
      </AzureFluentProvider>,
    );

    expect(screen.getByRole('button', { name: 'Sign in with Microsoft Entra ID' })).toBeDefined();
    expect(screen.getByText(/link at least one GitHub account/i)).toBeDefined();
    expect(screen.getByText(/link a github account whenever you're ready/i)).toBeDefined();
  });

  it('renders GitHub sign-in copy for GitHub-mode deployments', () => {
    render(
      <AzureFluentProvider density="compact">
        <SignInPage authMode="github-legacy" />
      </AzureFluentProvider>,
    );

    expect(screen.getByRole('button', { name: 'Sign in with GitHub' })).toBeDefined();
    expect(screen.queryByText(/link at least one GitHub account/i)).toBeNull();
  });

  it('surfaces a session-check error instead of failing silently', () => {
    render(
      <AzureFluentProvider density="compact">
        <SignInPage authMode="entra" sessionError="Access denied. A recognized Agentweaver platform role is required. (no roles found on token)" />
      </AzureFluentProvider>,
    );

    expect(screen.getByText(/couldn't check your sign-in status/i)).toBeDefined();
    expect(screen.getByText(/no roles found on token/i)).toBeDefined();
  });
});
