import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { TurnGroup } from '../components/TurnGroup';
import type { TurnGroupItem } from '../timeline/types';

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
  it('renders the turn divider for an active turn with no steps', () => {
    const { container } = render(
      <Wrapper>
        <TurnGroup
          item={makeTurnGroup({ steps: [], active: true })}
          isLiveRun={true}
          streamStatus="streaming"
        />
      </Wrapper>,
    );
    // The TurnDivider should be present with "Turn 1" text
    const text = container.textContent ?? '';
    expect(text).toContain('Turn 1');
  });
});
