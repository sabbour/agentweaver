import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { RunTimeline } from '../components/RunTimeline';
import { buildRunTimeline } from '../timeline/runTimelineSteps';
import type { RunStreamEvent } from '../api/sse';

const evt = (sequence: number, type: string, payload: Record<string, unknown>): RunStreamEvent =>
  ({ sequence, type: type as RunStreamEvent['type'], payload });

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

afterEach(() => cleanup());

describe('RunTimeline default expansion', () => {
  it('keeps the step count label aligned with the rendered top-level steps after narration collapsing', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: "Now let's build the prototype with Vite + React" }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'write_file', arguments: { path: 'src/main.tsx' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'agent.intent', { intent: 'Now the storage module' }),
      evt(5, 'tool.call', { callId: 'c2', toolName: 'write_file', arguments: { path: 'src/storage.ts' } }),
      evt(6, 'tool.result', { callId: 'c2', content: 'ok' }),
      evt(7, 'agent.intent', { intent: 'Review the result' }),
      evt(8, 'agent.message', { messageId: 'm1', content: 'Ready for review.' }),
      evt(9, 'agent.turn.end', {}),
    ]);

    render(
      <Wrapper>
        <RunTimeline embedded steps={model.steps} running={false} />
      </Wrapper>,
    );

    expect(screen.getByText('2 steps')).toBeTruthy();
    expect(screen.getByText('Build the prototype with Vite + React')).toBeTruthy();
    expect(screen.getByText('Review the result')).toBeTruthy();
    expect(screen.queryByText('Now the storage module')).toBeNull();
  });

  it('numbers multiple distinct steps sequentially (Step 1, Step 2, Step 3) instead of labeling every step "Step 1"', () => {
    // Regression test for a bug where every step in a run rendered as "Step 1" instead of
    // incrementing — root-caused to `collapseContinuationNarrationSteps` folding an entire
    // run's worth of continuation-narrated steps into one mega-step (see
    // runTimelineSteps.test.ts's "does not merge continuation steps past the
    // collapsible-narration caps" test for the underlying data-model regression test).
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Read the code' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'agent.intent', { intent: 'Build the project' }),
      evt(5, 'tool.call', { callId: 'c2', toolName: 'run_command', arguments: { command: 'npm run build' } }),
      evt(6, 'tool.result', { callId: 'c2', content: 'ok' }),
      evt(7, 'agent.intent', { intent: 'Deploy the artifact' }),
      evt(8, 'tool.call', { callId: 'c3', toolName: 'run_command', arguments: { command: 'npm run deploy' } }),
      evt(9, 'tool.result', { callId: 'c3', content: 'ok' }),
      evt(10, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(3);

    render(
      <Wrapper>
        <RunTimeline steps={model.steps} running={false} />
      </Wrapper>,
    );

    expect(screen.getByText('Step 1 ·')).toBeTruthy();
    expect(screen.getByText('Step 2 ·')).toBeTruthy();
    expect(screen.getByText('Step 3 ·')).toBeTruthy();
    expect(screen.queryByText('Step 4 ·')).toBeNull();
  });

  describe('RunTimeline tool arguments', () => {
    function openSingleTool() {
      fireEvent.click(screen.getByTestId('timeline-tool-group'));
      fireEvent.click(screen.getByTestId('timeline-tool-row'));
    }

    it('renders object arguments as labeled values and truncates only long values', () => {
      const fileText = `${'const greeting = "hello";\n'.repeat(50)}END OF FILE`;
      const model = buildRunTimeline([
        evt(1, 'agent.intent', { intent: 'Create the file' }),
        evt(2, 'tool.call', {
          callId: 'c1',
          toolName: 'create_file',
          arguments: { path: 'src/greeting.ts', file_text: fileText },
        }),
        evt(3, 'agent.turn.end', {}),
      ]);

      render(
        <Wrapper>
          <RunTimeline embedded steps={model.steps} running={false} />
        </Wrapper>,
      );

      openSingleTool();

      const argumentsBlock = screen.getByTestId('timeline-tool-arguments');
      expect(within(argumentsBlock).getByText('path')).toBeTruthy();
      expect(within(argumentsBlock).getByText('src/greeting.ts')).toBeTruthy();
      expect(within(argumentsBlock).getByText('file_text')).toBeTruthy();
      expect(argumentsBlock.textContent).not.toContain('END OF FILE');

      fireEvent.click(screen.getByTestId('timeline-tool-arguments-toggle'));

      expect(argumentsBlock.textContent).toContain('END OF FILE');
    });

    it.each(['not valid JSON', '["not", "an", "object"]'])(
      'falls back to raw text when arguments are not an object (%s)',
      (argumentsJson) => {
        const tool = {
          callId: 'c1',
          toolName: 'tool',
          category: 'other' as const,
          title: 'Tool',
          status: 'complete' as const,
          argumentsJson,
          expandable: true,
        };
        const steps = [{
          id: 'intent-1',
          intent: 'Run tool',
          status: 'complete' as const,
          active: false,
          synthetic: false,
          tools: [tool],
          messages: [],
          children: [{ kind: 'tool' as const, tool }],
          sequence: 1,
        }];

        render(
          <Wrapper>
            <RunTimeline flat steps={steps} running={false} />
          </Wrapper>,
        );

        openSingleTool();

        const argumentsBlock = screen.getByTestId('timeline-tool-arguments');
        expect(argumentsBlock.tagName).toBe('PRE');
        expect(argumentsBlock.textContent).toBe(argumentsJson);
      },
    );
  });

  it('expands every activity step by default while keeping tool groups collapsed', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.turn.start', { turnId: 't1' }),
      evt(2, 'agent.intent', { intent: 'Read the code' }),
      evt(3, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } }),
      evt(4, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(5, 'agent.message', { messageId: 'm1', content: 'Finished reading.' }),
      evt(6, 'agent.intent', { intent: 'Build the project' }),
      evt(7, 'tool.call', { callId: 'c2', toolName: 'run_command', arguments: { command: 'npm run build' } }),
      evt(8, 'tool.error', { callId: 'c2', errorMessage: 'exit code 1' }),
      evt(9, 'agent.message', { messageId: 'm2', content: 'Build failed.' }),
      evt(10, 'agent.turn.end', {}),
    ]);

    render(
      <Wrapper>
        <RunTimeline embedded steps={model.steps} running={false} />
      </Wrapper>,
    );

    // Both steps are expanded by default: their panel content (messages + the "Used N tools"
    // headers) renders immediately without expanding each step.
    expect(screen.getByText('Finished reading.')).toBeTruthy();
    expect(screen.getByText('Build failed.')).toBeTruthy();

    const toolGroups = screen.getAllByTestId('timeline-tool-group');
    expect(toolGroups.length).toBe(2);
    // Tool groups stay folded: collapsed (aria-expanded=false) and no individual tool rows shown.
    for (const group of toolGroups) {
      expect(group.getAttribute('aria-expanded')).toBe('false');
    }
    expect(screen.queryByTestId('timeline-tool-diff')).toBeNull();
  });

  it('opens later steps by default even when they stream in after the first render', () => {
    // Regression test: the underlying Accordion's `defaultOpenItems` only applies at the
    // moment it first mounts. Steps commonly arrive asynchronously (SSE / history load), so a
    // step that streams in after mount must still default to open — it must not silently
    // render collapsed just because it wasn't part of the very first render.
    const firstBatch = buildRunTimeline([
      evt(1, 'agent.turn.start', { turnId: 't1' }),
      evt(2, 'agent.intent', { intent: 'Read the code' }),
      evt(3, 'agent.message', { messageId: 'm1', content: 'Finished reading.' }),
    ]);

    const { rerender } = render(
      <Wrapper>
        <RunTimeline embedded steps={firstBatch.steps} running />
      </Wrapper>,
    );
    expect(screen.getByText('Finished reading.')).toBeTruthy();

    const secondBatch = buildRunTimeline([
      evt(1, 'agent.turn.start', { turnId: 't1' }),
      evt(2, 'agent.intent', { intent: 'Read the code' }),
      evt(3, 'agent.message', { messageId: 'm1', content: 'Finished reading.' }),
      evt(4, 'agent.intent', { intent: 'Build the project' }),
      evt(5, 'agent.message', { messageId: 'm2', content: 'Build finished.' }),
    ]);

    act(() => {
      rerender(
        <Wrapper>
          <RunTimeline embedded steps={secondBatch.steps} running={false} />
        </Wrapper>,
      );
    });

    // The newly-streamed second step must already be expanded — no click required.
    expect(screen.getByText('Build finished.')).toBeTruthy();
  });

  it('renders relative timestamps in the production message stream with an absolute-time tooltip', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-14T03:58:00-07:00'));
    try {
      const knownTime = new Date('2026-07-14T03:55:00-07:00');
      const model = buildRunTimeline([
        evt(1, 'agent.intent', { intent: 'Explain' }),
        evt(2, 'agent.message', {
          messageId: 'm1',
          content: 'Finished reading.',
          timestamp_utc: knownTime.toISOString(),
        }),
        evt(3, 'agent.turn.end', {}),
      ]);

      render(
        <Wrapper>
          <RunTimeline embedded steps={model.steps} running={false} />
        </Wrapper>,
      );

      const ts = screen.getByText('3m ago');
      expect(ts).toBeTruthy();
      expect(ts.getAttribute('title')).toBe(knownTime.toLocaleString());
    } finally {
      vi.useRealTimers();
    }
  });

  it('refreshes relative timestamps while the timeline is mounted', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-14T03:58:00-07:00'));
    try {
      const knownTime = new Date(Date.now() - 2_000);
      const model = buildRunTimeline([
        evt(1, 'agent.intent', { intent: 'Explain' }),
        evt(2, 'agent.message', {
          messageId: 'm1',
          content: 'Fresh update',
          timestamp_utc: knownTime.toISOString(),
        }),
        evt(3, 'agent.turn.end', {}),
      ]);

      render(
        <Wrapper>
          <RunTimeline embedded steps={model.steps} running={false} />
        </Wrapper>,
      );

      expect(screen.getByText('just now')).toBeTruthy();

      act(() => {
        vi.advanceTimersByTime(5_000);
      });

      expect(screen.getByText('7s ago')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });
});
