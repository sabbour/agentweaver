import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useCtrlScrollZoom, ZoomControls } from '../components/board/useCtrlScrollZoom';

afterEach(() => cleanup());

// Default harness (no options) — mirrors KanbanBoard behaviour: max = 100%.
function ZoomHarness() {
  const { zoom, zoomIn, zoomOut, viewportRef, maxZoom } = useCtrlScrollZoom();
  return (
    <FluentProvider theme={webLightTheme}>
      <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} maxZoom={maxZoom} />
      <div ref={viewportRef}>
        <div style={{ zoom }} data-testid="zoom-target" />
      </div>
    </FluentProvider>
  );
}

// Harness with elevated max — mirrors WorkflowRunPage / CoordinatorRunPage.
function ZoomHarnessWithMax({ maxZoom: max }: { maxZoom: number }) {
  const { zoom, zoomIn, zoomOut, viewportRef, maxZoom } = useCtrlScrollZoom({ maxZoom: max });
  return (
    <FluentProvider theme={webLightTheme}>
      <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} maxZoom={maxZoom} />
      <div ref={viewportRef}>
        <div style={{ zoom }} data-testid="zoom-target" />
      </div>
    </FluentProvider>
  );
}

describe('useCtrlScrollZoom — shared Ctrl+Scroll zoom (workflow canvas + board)', () => {
  it('renders the Ctrl+Scroll hint, +/- buttons, and a live % readout', () => {
    render(<ZoomHarness />);

    expect(screen.getByText('Ctrl + Scroll to zoom')).toBeTruthy();
    expect(screen.getByText('100%')).toBeTruthy();
    expect(screen.getByLabelText('Zoom out')).toBeTruthy();
    expect(screen.getByLabelText('Zoom in')).toBeTruthy();
  });

  it('default (no options) — disables zoom-in at 100% and lowers the readout when zooming out', () => {
    render(<ZoomHarness />);

    const zoomIn = screen.getByLabelText('Zoom in') as HTMLButtonElement;
    const zoomOut = screen.getByLabelText('Zoom out') as HTMLButtonElement;

    // At the default max (100%), zoom-in must be disabled.
    expect(zoomIn.disabled).toBe(true);

    fireEvent.click(zoomOut);
    expect(screen.getByText('90%')).toBeTruthy();
  });

  it('clamps at the 50% minimum', () => {
    render(<ZoomHarness />);
    const zoomOut = screen.getByLabelText('Zoom out') as HTMLButtonElement;

    for (let i = 0; i < 10; i++) fireEvent.click(zoomOut);

    expect(screen.getByText('50%')).toBeTruthy();
    expect(zoomOut.disabled).toBe(true);
  });

  it('with maxZoom:2 — zoom-in is ENABLED at 100% and readout can exceed 100%', () => {
    render(<ZoomHarnessWithMax maxZoom={2} />);

    const zoomIn = screen.getByLabelText('Zoom in') as HTMLButtonElement;

    // At 100% the zoom-in button must be enabled when max is 200%.
    expect(zoomIn.disabled).toBe(false);

    fireEvent.click(zoomIn);
    expect(screen.getByText('110%')).toBeTruthy();
  });

  it('with maxZoom:2 — zoom-in disables at 200% and readout shows 200%', () => {
    render(<ZoomHarnessWithMax maxZoom={2} />);

    const zoomIn = screen.getByLabelText('Zoom in') as HTMLButtonElement;

    // Click enough times to reach the 200% ceiling.
    for (let i = 0; i < 20; i++) fireEvent.click(zoomIn);

    expect(screen.getByText('200%')).toBeTruthy();
    expect(zoomIn.disabled).toBe(true);
  });
});
