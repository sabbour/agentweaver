import { Button } from '@fluentui/react-components';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SetupReadiness } from '../components/SetupReadiness';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

afterEach(cleanup);

describe('SetupReadiness', () => {
  it('shows required and optional rows with accessible status text', () => {
    render(
      <AzureFluentProvider>
        <SetupReadiness
          model={{
            title: 'Setup readiness',
            items: [
              {
                id: 'provider',
                title: 'Model provider',
                description: 'GitHub Copilot supplies AI access. Scope: Platform.',
                requirement: 'required',
                status: 'ready',
              },
              {
                id: 'repository',
                title: 'Repository access',
                description: 'Pull-request publishing requires repository access.',
                requirement: 'optional',
                status: 'optional',
              },
            ],
          }}
          primaryAction={<Button appearance="primary">Set up repository access</Button>}
        />
      </AzureFluentProvider>,
    );

    expect(screen.getByRole('heading', { name: 'Setup readiness' })).toBeDefined();
    expect(screen.getByRole('list', { name: 'Setup readiness checklist' })).toBeDefined();
    expect(screen.getByText('Required')).toBeDefined();
    expect(screen.getAllByText('Optional')).toHaveLength(2);
    expect(screen.getByText('Ready')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Set up repository access' })).toBeDefined();
  });

  it('collapses optional guidance and expands it from the keyboard button', () => {
    render(
      <AzureFluentProvider>
        <SetupReadiness
          model={{
            title: 'Project setup',
            collapseOptional: true,
            items: [
              {
                id: 'provider',
                title: 'Model provider',
                description: 'The model provider is ready.',
                requirement: 'required',
                status: 'ready',
              },
              {
                id: 'repository',
                title: 'Repository access',
                description: 'Repository access is optional.',
                requirement: 'optional',
                status: 'optional',
              },
            ],
          }}
        />
      </AzureFluentProvider>,
    );

    const toggle = screen.getByRole('button', { name: 'Show optional setup' });
    const list = screen.getByRole('list', { name: 'Project setup checklist' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.getAttribute('aria-controls')).toBe(list.id);
    expect(screen.queryByText('Repository access')).toBeNull();

    fireEvent.keyDown(toggle, { key: 'Enter' });
    fireEvent.click(toggle);

    expect(screen.getByText('Repository access')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Hide optional setup' }).getAttribute('aria-expanded')).toBe('true');
  });

  it('names loading and recovery actions', () => {
    const retry = vi.fn();
    const { rerender } = render(
      <AzureFluentProvider>
        <SetupReadiness
          model={{
            title: 'Setup readiness',
            loading: true,
            loadingLabel: 'Loading model provider status',
            items: [],
          }}
        />
      </AzureFluentProvider>,
    );

    expect(screen.getByText('Loading model provider status')).toBeDefined();

    rerender(
      <AzureFluentProvider>
        <SetupReadiness
          model={{
            title: 'Setup readiness',
            error: 'The setup status did not load.',
            items: [],
          }}
          onRetry={retry}
        />
      </AzureFluentProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Reload setup status' }));
    expect(retry).toHaveBeenCalledOnce();
  });
});
