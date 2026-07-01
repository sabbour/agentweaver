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
const setCenter = vi.fn();
const getNode = vi.fn().mockReturnValue({
  position: { x: 100, y: 50 },
  measured: { width: 220, height: 100 },
});

vi.mock('@xyflow/react', async (importActual) => {
  const actual = await importActual<typeof import('@xyflow/react')>();
  return {
    ...actual,
    useReactFlow: () => ({ zoomIn, zoomOut, zoomTo, fitView, setCenter, getNode }),
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
  setCenter.mockClear();
  getNode.mockClear();
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

  it('"Zoom to readable" button renders and snaps to 85% zoom', () => {
    render(<Wrapper><GraphControls /></Wrapper>);
    const btn = screen.getByRole('button', { name: 'Zoom to readable' });
    expect(btn).toBeTruthy();
    expect(btn.textContent).toContain('85%');
    fireEvent.click(btn);
    expect(zoomTo).toHaveBeenCalledTimes(1);
    expect(zoomTo.mock.calls[0][0]).toBe(0.85);
  });

  it('does not render nav buttons when orderedNodeIds is empty', () => {
    render(<Wrapper><GraphControls orderedNodeIds={[]} /></Wrapper>);
    expect(screen.queryByRole('button', { name: 'Previous card' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Next card' })).toBeNull();
  });

  it('does not render nav buttons when only one node is provided', () => {
    render(<Wrapper><GraphControls orderedNodeIds={['node-1']} /></Wrapper>);
    expect(screen.queryByRole('button', { name: 'Previous card' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Next card' })).toBeNull();
  });

  it('renders Prev/Next buttons when multiple orderedNodeIds are provided', () => {
    render(<Wrapper><GraphControls orderedNodeIds={['a', 'b', 'c']} /></Wrapper>);
    expect(screen.getByRole('button', { name: 'Previous card' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Next card' })).toBeTruthy();
  });

  it('Next card pans to the next node via setCenter', () => {
    render(<Wrapper><GraphControls orderedNodeIds={['a', 'b', 'c']} /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Next card' }));
    expect(getNode).toHaveBeenCalledWith('b');
    expect(setCenter).toHaveBeenCalledTimes(1);
    // Center should be midpoint of the mocked node (x=100+220/2=210, y=50+100/2=100).
    expect(setCenter.mock.calls[0][0]).toBe(210);
    expect(setCenter.mock.calls[0][1]).toBe(100);
    expect(setCenter.mock.calls[0][2]).toMatchObject({ zoom: 0.85 });
  });

  it('Previous card wraps around to last node', () => {
    render(<Wrapper><GraphControls orderedNodeIds={['a', 'b', 'c']} /></Wrapper>);
    fireEvent.click(screen.getByRole('button', { name: 'Previous card' }));
    // Starting at index 0, prev wraps to index 2 ('c').
    expect(getNode).toHaveBeenCalledWith('c');
    expect(setCenter).toHaveBeenCalledTimes(1);
  });

  it('Next card cycles back to first node after last', () => {
    render(<Wrapper><GraphControls orderedNodeIds={['a', 'b']} /></Wrapper>);
    // Navigate to 'b' (index 1)
    fireEvent.click(screen.getByRole('button', { name: 'Next card' }));
    getNode.mockClear();
    setCenter.mockClear();
    // Navigate again: should wrap back to 'a' (index 0)
    fireEvent.click(screen.getByRole('button', { name: 'Next card' }));
    expect(getNode).toHaveBeenCalledWith('a');
  });
});
