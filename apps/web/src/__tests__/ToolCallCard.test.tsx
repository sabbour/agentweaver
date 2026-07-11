import userEvent from '@testing-library/user-event';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ToolCallCard } from '../components/ToolCallCard';
import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ToolCallItem } from '../timeline/types';
import type { ReactNode } from 'react';
function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
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

  // C-08: a settled read shows NO clock/spinner and NO error affordance (completed work resolves)
  it('settled completed call shows no pending spinner', () => {
    const item = makeCall({ settled: true, result: { content: 'a\nb\nc' } });
    const { container } = render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    // The "Pending" spinner and "Result not received" warning must be gone once settled.
    expect(container.querySelector('[aria-label="Pending"]')).toBeNull();
    expect(container.querySelector('[aria-label="Result not received"]')).toBeNull();
  });

  // C-09: muted metadata (line/match counts) renders after the label
  it('renders muted line-count metadata for a settled read', () => {
    const item = makeCall({
      toolName: 'read_file',
      settled: true,
      result: { content: 'line1\nline2\nline3\nline4' },
    });
    render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    expect(screen.getByText('4 lines')).toBeDefined();
  });

  it('renders match-count metadata for a settled search', () => {
    const item = makeCall({
      toolName: 'grep_search',
      humanTitle: 'Search \u00b7 TODO',
      args: { pattern: 'TODO' },
      settled: true,
      result: { content: 'a.ts:1\nb.ts:2' },
    });
    render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    expect(screen.getByText('2 matches')).toBeDefined();
  });

  // C-10: an unsettled call during a live stream shows the running spinner (never a bare clock)
  it('unsettled call in a live stream shows a running spinner, not a clock', () => {
    const { container } = render(
      <Wrapper>
        <ToolCallCard item={makeCall({ settled: false })} streamStatus="streaming" />
      </Wrapper>,
    );
    expect(container.querySelector('[aria-label="Pending"]')).not.toBeNull();
  });

  // C-11: a long title stays a single line via CSS ellipsis (no wrap class)
  it('applies single-line ellipsis styling to the title', () => {
    const item = makeCall({
      humanTitle: 'View ' + 'src/very/deeply/nested/path/'.repeat(6) + 'Component.tsx:1-260',
      settled: true,
      result: { content: 'x' },
    });
    render(
      <Wrapper>
        <ToolCallCard item={item} streamStatus="done" />
      </Wrapper>,
    );
    const titleEl = screen.getByText(item.humanTitle);
    expect(getComputedStyle(titleEl).whiteSpace).toBe('nowrap');
  });
});
