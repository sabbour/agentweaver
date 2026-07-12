import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
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
});
