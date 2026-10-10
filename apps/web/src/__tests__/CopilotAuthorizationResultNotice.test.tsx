import { CopilotAuthorizationResultNotice } from '../components/CopilotAuthorizationResultNotice';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

afterEach(() => cleanup());

describe('CopilotAuthorizationResultNotice', () => {
  it('renders a safe success message without rendering the callback code', () => {
    render(
      <AzureFluentProvider>
        <CopilotAuthorizationResultNotice code="success" onDismiss={vi.fn()} />
      </AzureFluentProvider>,
    );

    expect(screen.getByText(/The Copilot App is connected to this project/)).toBeDefined();
    expect(screen.queryByText('success')).toBeNull();
  });

  it('renders an unknown callback code as a generic failure without echoing it', () => {
    const dismiss = vi.fn();
    render(
      <AzureFluentProvider>
        <CopilotAuthorizationResultNotice code="sensitive-unexpected-provider-value" onDismiss={dismiss} />
      </AzureFluentProvider>,
    );

    expect(screen.getByText(/could not be completed/)).toBeDefined();
    expect(screen.queryByText('sensitive-unexpected-provider-value')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(dismiss).toHaveBeenCalledOnce();
  });
});
