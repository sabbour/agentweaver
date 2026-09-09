import { Button } from '@fluentui/react-components';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { AiExecutionProviderHint } from '../components/AiExecutionProviderHint';
import { aiExecutionProviderLabel } from '../components/aiExecutionContext';
import { AzureFluentProvider } from '../copilot-fluent-system';
import type { AiExecutionContext } from '../api/types';

const context = (
  phase: AiExecutionContext['phase'],
  providerKind: 'platform_github_copilot' | 'byok' = 'platform_github_copilot',
): AiExecutionContext => ({
  ai_required: true,
  operation: 'orchestration',
  phase,
  execution_key: 'signed-provider-key',
  expires_at: '2099-01-01T00:00:00Z',
  effective_model_provider: {
    state: 'resolved',
    provider_kind: providerKind,
    resolution_scope: 'project',
    provider_scope: 'platform',
    provider_type: providerKind === 'byok' ? 'azure' : null,
    model_id: 'gpt-5',
    provider_key: 'provider-fingerprint',
    unavailable_reason: null,
  },
});

afterEach(cleanup);

describe('AI execution provider hints', () => {
  it('shows phase-correct, user-friendly labels without exposing the provider key', () => {
    expect(aiExecutionProviderLabel(context('prepared')))
      .toBe('Expected provider: GitHub Copilot. Model: gpt-5.');
    expect(aiExecutionProviderLabel(context('active')))
      .toBe('Using GitHub Copilot. Model: gpt-5.');
    expect(aiExecutionProviderLabel(context('completed', 'byok')))
      .toBe('Used Azure BYOK. Model: gpt-5.');
    expect(aiExecutionProviderLabel(context('prepared'))).not.toContain('secret-provider-key');
  });

  it('associates the provider description with a keyboard-focusable action', async () => {
    const user = userEvent.setup();
    render(
      <AzureFluentProvider density="compact">
        <AiExecutionProviderHint context={context('prepared')}>
          <Button>Start AI work</Button>
        </AiExecutionProviderHint>
      </AzureFluentProvider>,
    );

    const button = screen.getByRole('button', { name: 'Start AI work' });
    await user.tab();

    expect(document.activeElement).toBe(button);
    expect(screen.getByText('Expected provider: GitHub Copilot. Model: gpt-5.')).toBeTruthy();
    await waitFor(() => expect(button.getAttribute('aria-describedby')).toBeTruthy());
    expect(document.body.textContent).not.toContain('secret-provider-key');
  });
});
