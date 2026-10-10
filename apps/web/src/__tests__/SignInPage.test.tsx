import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SignInPage } from '../pages/SignInPage';

afterEach(() => {
  window.history.replaceState({}, '', '/');
  cleanup();
});

describe('SignInPage callback errors', () => {
  it('shows actionable guidance for a denied sign-in', () => {
    window.history.replaceState({}, '', '/?auth=error&reason=access_denied');
    render(<AzureFluentProvider><SignInPage /></AzureFluentProvider>);
    expect(screen.getByRole('alert').textContent).toContain('Sign-in was canceled');
  });

  it('does not echo unknown provider error or query data', () => {
    window.history.replaceState({}, '', '/?auth=error&reason=code%3Dsecret%26state%3Dsecret');
    render(<AzureFluentProvider><SignInPage /></AzureFluentProvider>);
    expect(screen.getByRole('alert').textContent).toContain('Authentication could not be completed');
    expect(screen.queryByText(/code=secret/)).toBeNull();
  });
});
