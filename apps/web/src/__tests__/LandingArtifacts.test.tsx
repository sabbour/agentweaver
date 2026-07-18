import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { FluentProvider } from '@fluentui/react-components';
import { agentweaverLightTheme } from '../theme';
import { CompetitorEvalArtifact } from '../components/artifacts/CompetitorEvalArtifact';
import { IncidentTriageArtifact } from '../components/artifacts/IncidentTriageArtifact';
import { SpaceInvadersArtifact } from '../components/artifacts/SpaceInvadersArtifact';
import { SCENARIOS } from '../components/landing/scenarios';

/**
 * Artifact-level guarantees: every generated artifact is a still preview with
 * NO focusable/interactive controls (all "buttons"/"links" are inert decorative
 * spans), and the scenario-specific trust copy is present.
 */

afterEach(cleanup);

function renderArtifact(node: React.ReactElement) {
  return render(<FluentProvider theme={agentweaverLightTheme}>{node}</FluentProvider>);
}

describe('artifact inert semantics', () => {
  it('renders no real buttons, links, inputs, or focusable elements', () => {
    const { container } = renderArtifact(<IncidentTriageArtifact />);
    expect(container.querySelectorAll('button, a, input, textarea, select')).toHaveLength(0);
    // Every decorative control is a non-focusable, aria-hidden span.
    const inert = container.querySelectorAll('[data-inert-preview]');
    expect(inert.length).toBeGreaterThan(0);
    inert.forEach((el) => {
      expect(el.tagName).toBe('SPAN');
      expect(el.getAttribute('tabindex')).toBeNull();
      expect(el.getAttribute('href')).toBeNull();
      expect(el.getAttribute('role')).toBeNull();
      expect(el.getAttribute('aria-hidden')).toBe('true');
    });
    // Nothing in the artifact carries a positive/zero tabindex.
    expect(container.querySelector('[tabindex="0"]')).toBeNull();
  });

  it('incident triage frames remediation as human-review only', () => {
    renderArtifact(<IncidentTriageArtifact />);
    expect(screen.getByText(/for human review/i)).toBeTruthy();
    expect(
      screen.getByText(/No changes have been applied\. Every remediation above requires explicit human approval/i),
    ).toBeTruthy();
  });

  it('competitor evaluation carries illustrative source notes and marks ratings illustrative', () => {
    const { container } = renderArtifact(<CompetitorEvalArtifact />);
    expect(screen.getByText('Illustrative source notes')).toBeTruthy();
    expect(screen.getByText(/Illustrative ratings/i)).toBeTruthy();
    expect(container.querySelectorAll('button, a')).toHaveLength(0);
  });

  it('space invaders demo is a non-interactive still preview', () => {
    const { container } = renderArtifact(<SpaceInvadersArtifact />);
    expect(container.querySelectorAll('button, a, input')).toHaveLength(0);
    expect(container.querySelector('[tabindex="0"]')).toBeNull();
  });
});

describe('scenario catalog', () => {
  it('defines the eight approved scenarios in order without the analytics scenario', () => {
    expect(SCENARIOS.map((s) => s.id)).toEqual([
      'product-feature',
      'marketing',
      'blog',
      'incident',
      'competitor',
      'rfp',
      'game',
      'decision',
    ]);
    expect(SCENARIOS.some((s) => /analytic|dashboard/i.test(s.id))).toBe(false);
  });

  it('never sets startedAt on any node so ElapsedTimer can not start an interval', () => {
    for (const scenario of SCENARIOS) {
      for (const node of scenario.nodes) {
        expect('startedAt' in node).toBe(false);
      }
    }
  });
});
