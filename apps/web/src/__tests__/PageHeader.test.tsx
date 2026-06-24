import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { PageHeader } from '../components/PageHeader';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

afterEach(() => cleanup());

describe('PageHeader', () => {
  it('renders the title', () => {
    render(<Wrapper><PageHeader title="Flow" /></Wrapper>);
    expect(screen.getByText('Flow')).toBeDefined();
  });

  it('renders the subtitle when provided', () => {
    render(<Wrapper><PageHeader title="Flow" subtitle="What each agent is working on right now." /></Wrapper>);
    expect(screen.getByText('What each agent is working on right now.')).toBeDefined();
  });

  it('renders the actions slot', () => {
    render(
      <Wrapper>
        <PageHeader title="Flow" actions={<button type="button">Refresh</button>} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeDefined();
  });

  it('renders the breadcrumb slot above the title', () => {
    render(
      <Wrapper>
        <PageHeader title="Flow" breadcrumb={<nav aria-label="Breadcrumb">Projects / Flow</nav>} />
      </Wrapper>,
    );
    expect(screen.getByLabelText('Breadcrumb')).toBeDefined();
    expect(screen.getByText('Flow')).toBeDefined();
  });
});
