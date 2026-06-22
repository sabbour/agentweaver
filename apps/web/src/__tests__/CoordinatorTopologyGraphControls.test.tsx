import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';

// ResizeObserver is required by @xyflow/react and absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

// Spies for the React Flow imperative zoom API. GraphControls renders inside a
// <ReactFlow> Panel in production; here we drive it in isolation by mocking the
// flow store hooks so we can assert the controls invoke the expected handlers.
const zoomIn = vi.fn();
const zoomOut = vi.fn();
const zoomTo = vi.fn();
const fitView = vi.fn();

vi.mock('@xyflow/react', async (importActual) => {
  const actual = await importActual<typeof import('@xyflow/react')>();
  return {
    ...actual,
    useReactFlow: () => ({ zoomIn, zoomOut, zoomTo, fitView }),
    // transform = [x, y, zoom]; report 100% zoom.
    useStore: (selector: (s: { transform: [number, number, number] }) => unknown) =>
      selector({ transform: [0, 0, 1] }),
  };
});

import { GraphControls } from '../components/CoordinatorTopologyGraph';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

afterEach(() => {
  cleanup();
  zoomIn.mockClear();
  zoomOut.mockClear();
  zoomTo.mockClear();
  fitView.mockClear();
});

describe('GraphControls', () => {
  it('renders all zoom controls with the current zoom level', () => {
    render(<Wrapper><GraphControls /></Wrapper>);
    expect(screen.getByRole('button', { name: 'Zoom in' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Zoom out' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Fit to view' })).toBeTruthy();
    // Reset button shows the live zoom percentage.
    expect(screen.getByRole('button', { name: /Reset zoom to 100%/ }).textContent).toContain('100%');
  });

  it('zoom-in button calls zoomIn', () => {
    render(<Wrapper><GraphControls /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));
    expect(zoomIn).toHaveBeenCalledTimes(1);
  });

  it('zoom-out button calls zoomOut', () => {
    render(<Wrapper><GraphControls /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Zoom out' }));
    expect(zoomOut).toHaveBeenCalledTimes(1);
  });

  it('reset button resets zoom to 100% (zoomTo 1)', () => {
    render(<Wrapper><GraphControls /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: /Reset zoom to 100%/ }));
    expect(zoomTo).toHaveBeenCalledTimes(1);
    expect(zoomTo.mock.calls[0][0]).toBe(1);
  });

  it('fit-to-view button calls fitView', () => {
    render(<Wrapper><GraphControls /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Fit to view' }));
    expect(fitView).toHaveBeenCalledTimes(1);
  });
});
