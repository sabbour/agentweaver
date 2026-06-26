import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { TurnGroup } from '../components/TurnGroup';
import type { TurnGroupItem, TurnStep } from '../timeline/types';

afterEach(() => cleanup());

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function makeTurnGroup(overrides: Partial<TurnGroupItem> = {}): TurnGroupItem {
  return {
    kind: 'turn-group',
    turnId: 'T1',
    turnIndex: 1,
    steps: [],
    active: false,
    ...overrides,
  };
}

function makeToolCall(id: string, humanTitle: string, error = false): TurnStep {
  return {
    kind: 'tool-call',
    callId: id,
    toolName: 'read_file',
    humanTitle,
    args: { path: 'src/index.ts' },
    result: error ? null : { content: 'ok' },
    error: error ? { errorMessage: 'file not found', isSandboxViolation: false } : null,
    settled: true,
  };
}

function makeReportIntent(id: string, title: string): TurnStep {
  return {
    kind: 'tool-call',
    callId: id,
    toolName: 'report_intent',
    humanTitle: title,
    args: {},
    result: { content: 'ok' },
    error: null,
    settled: true,
  };
}

function makeAgentMessage(id: string, content: string): TurnStep {
  return {
    kind: 'agent-message',
    messageId: id,
    content,
    streaming: false,
  };
}

describe('TurnGroup empty-turn suppression', () => {
  // TG-01: closed turn with zero steps renders nothing
  it('renders nothing for a closed turn with no steps', () => {
    const { container } = render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps: [], active: false })}
          isLiveRun={false}
          streamStatus="done"
        />
      </Wrapper>,
    );
    // The TurnDivider would produce a "Turn 1" label — it must not appear
    expect(container.querySelector('[class]')?.textContent ?? '').not.toContain('Turn 1');
  });

  // TG-02: active turn with zero steps still renders (no suppression during live streaming)
  it('renders the steps container for an active turn with no steps', () => {
    const { container } = render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps: [], active: true })}
          isLiveRun={true}
          streamStatus="streaming"
        />
      </Wrapper>,
    );
    // Turn dividers are no longer shown; the container itself should render (not null)
    expect(container.firstChild).not.toBeNull();
  });
});

describe('TurnGroup — collapsible tool headers (header-as-toggle)', () => {
  // TG-03: completed turn — report_intent + tool cluster starts collapsed
  it('renders a completed report_intent + cluster as a collapsed toggle (tool not visible)', () => {
    const steps: TurnStep[] = [
      makeReportIntent('ri-1', 'Reading architecture docs'),
      makeToolCall('tc-1', 'read_file src/index.ts'),
    ];
    render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps, active: false })}
          isLiveRun={false}
          streamStatus="done"
        />
      </Wrapper>,
    );

    // Toggle button exists and is collapsed
    const toggle = screen.getByRole('button', { name: /Reading architecture docs/ });
    expect(toggle).toBeTruthy();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    // Tool card is NOT rendered while collapsed
    expect(screen.queryByText('read_file src/index.ts')).toBeNull();
  });

  // TG-04: clicking the intent toggle expands the tool cluster
  it('expands the tool cluster when the intent header is clicked', () => {
    const steps: TurnStep[] = [
      makeReportIntent('ri-1', 'Reading architecture docs'),
      makeToolCall('tc-1', 'read_file src/index.ts'),
    ];
    render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps, active: false })}
          isLiveRun={false}
          streamStatus="done"
        />
      </Wrapper>,
    );

    const toggle = screen.getByRole('button', { name: /Reading architecture docs/ });
    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('read_file src/index.ts')).toBeTruthy();
  });

  // TG-05: errored cluster auto-expands even for a completed turn
  it('auto-expands a completed cluster when a step has an error', () => {
    const steps: TurnStep[] = [
      makeReportIntent('ri-1', 'Running tests'),
      makeToolCall('tc-err', 'run_command npm test', true /* error */),
    ];
    render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps, active: false })}
          isLiveRun={false}
          streamStatus="done"
        />
      </Wrapper>,
    );

    // Toggle must be expanded by default due to the error
    const toggle = screen.getByRole('button', { name: /Running tests/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    // Tool card visible without a click
    expect(screen.getByText('run_command npm test')).toBeTruthy();
  });

  // TG-06: agent-message header + cluster — "Used N tools" toggle, collapsed by default
  it('renders an agent-message + cluster with a "Used N tools" toggle collapsed by default', () => {
    const steps: TurnStep[] = [
      makeAgentMessage('msg-1', 'Now let me create the core package:'),
      makeToolCall('tc-1', 'create_file src/core.ts'),
      makeToolCall('tc-2', 'create_file src/types.ts'),
    ];
    render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps, active: false })}
          isLiveRun={false}
          streamStatus="done"
        />
      </Wrapper>,
    );

    // Agent message bubble is visible
    expect(screen.getByLabelText('Agent message')).toBeTruthy();

    // "Used 2 tools" toggle button exists and is collapsed
    const toggle = screen.getByRole('button', { name: /Used 2 tools/ });
    expect(toggle).toBeTruthy();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    // Tool cards not rendered while collapsed
    expect(screen.queryByText('create_file src/core.ts')).toBeNull();

    // Clicking expands
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('create_file src/core.ts')).toBeTruthy();
    expect(screen.getByText('create_file src/types.ts')).toBeTruthy();
  });

  // TG-07: active turn — tool cards always visible, no collapse affordance
  it('renders an active turn with report_intent + cluster always expanded (no toggle)', () => {
    const steps: TurnStep[] = [
      makeReportIntent('ri-1', 'Reading architecture docs'),
      makeToolCall('tc-1', 'read_file src/index.ts'),
    ];
    render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps, active: true })}
          isLiveRun={true}
          streamStatus="streaming"
        />
      </Wrapper>,
    );

    // Tool card is visible inline without any toggle
    expect(screen.getByText('read_file src/index.ts')).toBeTruthy();

    // The intent toggle button should NOT exist (active turns render inline)
    expect(screen.queryByRole('button', { name: /Reading architecture docs/ })).toBeNull();
  });

  // TG-08: completed cluster with NO preceding inline header falls back to "Used N tools" row
  it('falls back to "Used N tools" header when cluster has no preceding inline step', () => {
    const steps: TurnStep[] = [
      makeToolCall('tc-1', 'read_file src/a.ts'),
      makeToolCall('tc-2', 'read_file src/b.ts'),
    ];
    render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps, active: false })}
          isLiveRun={false}
          streamStatus="done"
        />
      </Wrapper>,
    );

    expect(screen.getByRole('button', { name: /Used 2 tools/ })).toBeTruthy();
    expect(screen.queryByText('read_file src/a.ts')).toBeNull();
  });
});

