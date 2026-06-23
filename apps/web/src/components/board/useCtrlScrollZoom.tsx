/**
 * useCtrlScrollZoom — shared Ctrl+Scroll zoom behaviour and affordance.
 *
 * Extracted from the Kanban board so the same gesture, bounds, and controls are
 * reused verbatim by both the board (KanbanBoard) and the workflow diagram
 * (WorkflowRunPage). Users zoom OUT to fit a wide canvas on screen at once; 100%
 * is the natural, unscaled size.
 *
 * The wheel listener is attached as a native, non-passive listener (so
 * preventDefault suppresses the browser's page-zoom only while Ctrl is held;
 * plain scroll-to-pan is left untouched). It is wired through a callback ref so
 * it attaches/detaches correctly regardless of conditional rendering.
 */
import { useCallback, useRef, useState } from 'react';
import { Button, Caption1, makeStyles, tokens } from '@fluentui/react-components';
import { ZoomInRegular, ZoomOutRegular } from '@fluentui/react-icons';

export const MIN_ZOOM = 0.5;
export const MAX_ZOOM = 1;
export const ZOOM_STEP = 0.1;

export const clampZoom = (z: number): number =>
  Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round(z * 100) / 100));

export interface CtrlScrollZoom {
  /** Current zoom factor (1 = 100%). Apply via CSS `style={{ zoom }}`. */
  zoom: number;
  /** Zoom in by one step (clamped to MAX_ZOOM). */
  zoomIn: () => void;
  /** Zoom out by one step (clamped to MIN_ZOOM). */
  zoomOut: () => void;
  /** Callback ref to attach to the scroll viewport that receives the wheel gesture. */
  viewportRef: (node: HTMLElement | null) => void;
}

export function useCtrlScrollZoom(): CtrlScrollZoom {
  const [zoom, setZoom] = useState(1);
  const cleanupRef = useRef<(() => void) | null>(null);

  const viewportRef = useCallback((node: HTMLElement | null) => {
    if (cleanupRef.current) {
      cleanupRef.current();
      cleanupRef.current = null;
    }
    if (node) {
      const onWheel = (e: WheelEvent) => {
        if (!e.ctrlKey) return;
        e.preventDefault();
        setZoom((z) => clampZoom(z - Math.sign(e.deltaY) * ZOOM_STEP));
      };
      node.addEventListener('wheel', onWheel, { passive: false });
      cleanupRef.current = () => node.removeEventListener('wheel', onWheel);
    }
  }, []);

  const zoomIn = useCallback(() => setZoom((z) => clampZoom(z + ZOOM_STEP)), []);
  const zoomOut = useCallback(() => setZoom((z) => clampZoom(z - ZOOM_STEP)), []);

  return { zoom, zoomIn, zoomOut, viewportRef };
}

const useControlStyles = makeStyles({
  zoomBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalXS,
  },
  zoomHint: {
    color: tokens.colorNeutralForeground3,
    marginRight: tokens.spacingHorizontalS,
  },
  zoomReadout: {
    color: tokens.colorNeutralForeground2,
    minWidth: '40px',
    textAlign: 'center',
  },
});

export interface ZoomControlsProps {
  zoom: number;
  onZoomIn: () => void;
  onZoomOut: () => void;
}

/** The shared "Ctrl + Scroll to zoom" hint, +/- buttons, and live % readout. */
export function ZoomControls({ zoom, onZoomIn, onZoomOut }: ZoomControlsProps) {
  const styles = useControlStyles();
  return (
    <div className={styles.zoomBar}>
      <Caption1 className={styles.zoomHint}>Ctrl + Scroll to zoom</Caption1>
      <Button
        size="small"
        appearance="subtle"
        icon={<ZoomOutRegular />}
        aria-label="Zoom out"
        disabled={zoom <= MIN_ZOOM}
        onClick={onZoomOut}
      />
      <Caption1 className={styles.zoomReadout} aria-live="polite">
        {Math.round(zoom * 100)}%
      </Caption1>
      <Button
        size="small"
        appearance="subtle"
        icon={<ZoomInRegular />}
        aria-label="Zoom in"
        disabled={zoom >= MAX_ZOOM}
        onClick={onZoomIn}
      />
    </div>
  );
}
