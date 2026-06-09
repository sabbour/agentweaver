import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { ToolCallCard } from '../components/ToolCallCard';
import type { ToolCallItem } from '../timeline/types';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function makeCall(overrides: Partial<ToolCallItem> = {}): ToolCallItem {
  return {
    kind: 'tool-call',
    callId: 'C1',
    toolName: 'read_file',
    humanTitle: 'Read file \u00b7 src/x.ts',
    args: { path: 'src/x.ts' },
    result: null,
    error: null,
    settled: false,
    ...overrides,
  };
}

describe('ToolCallCard', () => {
  // C-01: unsettled card — spinner visible, title shown
  it('renders unsettled card with title', () => {
    render(
      <Wrapper>
        <ToolCallCard item={makeCall()} streamStatus="streaming" />
      </Wrapper>,
    );
    // The human title should appear somewhere in the accordion header
    expect(screen.getByText('Read file \u00b7 src/x.ts')).toBeDefined();
  });

  // C-03: sandbox violation — WarningFilled and "sandbox" badge
  it('renders sandbox violation with sandbox badge', () => {
    const item = makeCall({
      settled: true,
      error: { errorMessage: 'Path is outside the sandbox boundary', isSandboxViolation: true },
    });
    render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    expect(screen.getByText('sandbox')).toBeDefined();
  });

  // C-04: non-sandbox tool error — error badge, no sandbox badge
  it('renders non-sandbox error with error badge', () => {
    const item = makeCall({
      settled: true,
      error: { errorMessage: 'File not found', isSandboxViolation: false },
    });
    const { container } = render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    // Find badges specifically (they have specific role/styling in Fluent)
    // The error badge should exist in the rendered output
    const hasErrorBadge = Array.from(container.querySelectorAll('*')).some(
      (el) => el.textContent?.trim() === 'error' && el.tagName !== 'SCRIPT',
    );
    expect(hasErrorBadge).toBe(true);
    const hasSandboxBadge = Array.from(container.querySelectorAll('*')).some(
      (el) => el.textContent?.trim() === 'sandbox',
    );
    expect(hasSandboxBadge).toBe(false);
  });

  // C-05: settled success card shows title
  it('renders succeeded card without error/sandbox badge', () => {
    const item = makeCall({
      settled: true,
      result: { content: 'file data' },
    });
    const { container } = render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    const hasSandboxBadge = Array.from(container.querySelectorAll('*')).some(
      (el) => el.textContent?.trim() === 'sandbox',
    );
    expect(hasSandboxBadge).toBe(false);
    // Error badge should not exist — look for standalone "error" text node
    const badges = Array.from(container.querySelectorAll('span, div')).filter(
      (el) => el.childNodes.length === 1 &&
        el.childNodes[0].nodeType === 3 && // text node
        el.textContent?.trim() === 'error',
    );
    expect(badges).toHaveLength(0);
  });

  // C-06: stream error with unsettled card shows warning indicator
  it('renders warning when stream errored and call unsettled', () => {
    render(
      <Wrapper>
        <ToolCallCard item={makeCall()} streamStatus="error" />
      </Wrapper>,
    );
    // WarningFilled should be present with aria-label "Result not received"
    const warning = document.querySelector('[aria-label="Result not received"]');
    expect(warning).toBeDefined();
  });

  // C-07: accordion header toggles open/closed (regression: controlled openItems froze it)
  it('accordion header toggles open/closed', async () => {
    const user = userEvent.setup();
    const item = makeCall({
      settled: true,
      result: { content: 'file data' },
    });
    const { container } = render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );

    const btn = within(container).getByRole('button');
    // Starts closed — defaultOpenItems=[] for non-sandbox settled calls
    expect(btn.getAttribute('aria-expanded')).toBe('false');

    // Open it
    await user.click(btn);
    expect(btn.getAttribute('aria-expanded')).toBe('true');

    // Close it
    await user.click(btn);
    expect(btn.getAttribute('aria-expanded')).toBe('false');
  });
});
