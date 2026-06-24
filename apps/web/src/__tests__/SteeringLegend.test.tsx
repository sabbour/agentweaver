import type { ReactNode } from 'react';
import { afterEach, describe, it, expect } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { SteeringLegend } from '../components/SteeringLegend';
import { STEERING_HELP } from '../components/steeringHelp';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

afterEach(cleanup);

// (d) Send / Redirect / Amend clarification: the three steering verbs must each show
// a distinct, visible helper text so the user can tell them apart at a glance.

describe('SteeringLegend', () => {
  it('renders a distinct helper text for each of Send, Redirect, and Amend', () => {
    render(<Wrapper><SteeringLegend /></Wrapper>);

    const send = screen.getByTestId('steering-help-send');
    const redirect = screen.getByTestId('steering-help-redirect');
    const amend = screen.getByTestId('steering-help-amend');

    expect(send.textContent).toContain('Send');
    expect(send.textContent).toContain(STEERING_HELP.send);
    expect(redirect.textContent).toContain('Redirect');
    expect(redirect.textContent).toContain(STEERING_HELP.redirect);
    expect(amend.textContent).toContain('Amend');
    expect(amend.textContent).toContain(STEERING_HELP.amend);

    // The three explanations are actually different from one another.
    expect(STEERING_HELP.send).not.toBe(STEERING_HELP.redirect);
    expect(STEERING_HELP.redirect).not.toBe(STEERING_HELP.amend);
    expect(STEERING_HELP.send).not.toBe(STEERING_HELP.amend);
  });

  it('matches the agreed contract wording', () => {
    expect(STEERING_HELP.send).toBe('Message the coordinator without changing the current plan.');
    expect(STEERING_HELP.redirect).toContain('Override the current plan');
    expect(STEERING_HELP.amend).toContain('Add to the outcome/plan without discarding in-flight work');
  });
});
