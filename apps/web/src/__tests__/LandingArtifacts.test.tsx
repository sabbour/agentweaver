import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { FluentProvider } from '@fluentui/react-components';
import { agentweaverLightTheme } from '../theme';
import { CompetitorEvalArtifact } from '../components/artifacts/CompetitorEvalArtifact';
import { IncidentTriageArtifact } from '../components/artifacts/IncidentTriageArtifact';
import { SpaceInvadersArtifact } from '../components/artifacts/SpaceInvadersArtifact';
import { LumenpathArtifact } from '../components/artifacts/LumenpathArtifact';
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

  it('lumenpath preview is inert and names its featured tier consistently', () => {
    const { container } = renderArtifact(<LumenpathArtifact />);
    // Still preview: no real controls.
    expect(container.querySelectorAll('button, a, input, textarea, select')).toHaveLength(0);
    expect(container.querySelector('[tabindex="0"]')).toBeNull();
    // Recommended plan shows a real tier name AND the badge (not the badge as its name),
    // and the CTA matches the tier name.
    expect(screen.getByText('Trail')).toBeTruthy();
    expect(screen.getByText('Most popular')).toBeTruthy();
    expect(screen.getByText('Choose Trail')).toBeTruthy();
    // Secondary plans are named and present (editorial lead + quieter rows, not a 3-up grid).
    expect(screen.getByText('Spark')).toBeTruthy();
    expect(screen.getByText('Beacon')).toBeTruthy();
    // Product-specific brand voice, not interchangeable SaaS copy.
    expect(screen.getByText(/Journey-path intelligence/i)).toBeTruthy();
    // AI landing-page tells that were removed must stay gone.
    const text = container.textContent ?? '';
    expect(text).not.toContain('3.2M');
    expect(text).not.toContain('not the next thing on the list');
    expect(text).not.toContain('Watch the 2-min tour');
    expect(text).not.toContain('Answers, not dashboards');
    expect(text).not.toContain('◆');
    // The rejected template patterns (logo wall, uniform feature-card grid) must stay gone.
    expect(text).not.toContain('Reading trails for');
  });

  it('lumenpath journey map is accessible as a named image with no hidden ancestor', () => {
    const { container } = renderArtifact(<LumenpathArtifact />);
    // The authored route-map SVG must be exposed as a single named image.
    const svg = container.querySelector('svg[role="img"][aria-label="Journey route map"]');
    expect(svg).not.toBeNull();
    // No ancestor between the SVG and the container root may carry aria-hidden="true";
    // doing so would suppress the image from the accessibility tree.
    let node: Element | null = svg?.parentElement ?? null;
    while (node && node !== container) {
      expect(node.getAttribute('aria-hidden')).not.toBe('true');
      node = node.parentElement;
    }
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
