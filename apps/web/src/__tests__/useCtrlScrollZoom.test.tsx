import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useCtrlScrollZoom, ZoomControls } from '../components/board/useCtrlScrollZoom';

afterEach(() => cleanup());

// Harness mirroring how the workflow canvas (WorkflowRunPage) and the Kanban board
// both consume the shared zoom hook + controls.
function ZoomHarness() {
  const { zoom, zoomIn, zoomOut, viewportRef } = useCtrlScrollZoom();
  return (
    <FluentProvider theme={webLightTheme}>
      <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
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

  it('disables zoom-in at 100% (max) and lowers the readout when zooming out', () => {
    render(<ZoomHarness />);

    const zoomIn = screen.getByLabelText('Zoom in') as HTMLButtonElement;
    const zoomOut = screen.getByLabelText('Zoom out') as HTMLButtonElement;

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
});
