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
import { ArrowMaximizeRegular, ZoomInRegular, ZoomOutRegular } from '@fluentui/react-icons';

export const MIN_ZOOM = 0.5;
export const MAX_ZOOM = 1;
export const ZOOM_STEP = 0.1;

export const clampZoom = (z: number, max = MAX_ZOOM): number =>
  Math.min(max, Math.max(MIN_ZOOM, Math.round(z * 100) / 100));

export interface CtrlScrollZoomOptions {
  /** Upper zoom bound for this instance. Defaults to MAX_ZOOM (1 = 100%). */
  maxZoom?: number;
}

export interface CtrlScrollZoom {
  /** Current zoom factor (1 = 100%). Apply via CSS `style={{ zoom }}`. */
  zoom: number;
  /** Zoom in by one step (clamped to effective max). */
  zoomIn: () => void;
  /** Zoom out by one step (clamped to MIN_ZOOM). */
  zoomOut: () => void;
  /** Reset zoom to 100% (the natural, fitted size). */
  resetZoom: () => void;
  /** Callback ref to attach to the scroll viewport that receives the wheel gesture. */
  viewportRef: (node: HTMLElement | null) => void;
  /** Effective upper zoom bound for this hook instance. */
  maxZoom: number;
}

export function useCtrlScrollZoom(options?: CtrlScrollZoomOptions): CtrlScrollZoom {
  const effectiveMax = options?.maxZoom ?? MAX_ZOOM;
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
        setZoom((z) => clampZoom(z - Math.sign(e.deltaY) * ZOOM_STEP, effectiveMax));
      };
      node.addEventListener('wheel', onWheel, { passive: false });
      cleanupRef.current = () => node.removeEventListener('wheel', onWheel);
    }
  }, [effectiveMax]);

  const zoomIn = useCallback(() => setZoom((z) => clampZoom(z + ZOOM_STEP, effectiveMax)), [effectiveMax]);
  const zoomOut = useCallback(() => setZoom((z) => clampZoom(z - ZOOM_STEP, effectiveMax)), [effectiveMax]);
  const resetZoom = useCallback(() => setZoom(1), []);

  return { zoom, zoomIn, zoomOut, resetZoom, viewportRef, maxZoom: effectiveMax };
}

const useControlStyles = makeStyles({
  zoomBar: {
    display: 'flex',
    alignItems: 'center',
    gap: '2px',
    padding: '2px',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow4,
  },
  zoomReadout: {
    color: tokens.colorNeutralForeground2,
    minWidth: '44px',
    textAlign: 'center',
    fontVariantNumeric: 'tabular-nums',
  },
  divider: {
    width: '1px',
    alignSelf: 'stretch',
    margin: '3px 1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
});

export interface ZoomControlsProps {
  zoom: number;
  onZoomIn: () => void;
  onZoomOut: () => void;
  /** Optional fit-to-view (reset to 100%) handler. When provided, a fit button is shown. */
  onFit?: () => void;
  /** Effective max zoom; defaults to MAX_ZOOM (1 = 100%). */
  maxZoom?: number;
}

/** A compact segmented zoom control: optional fit button, −/+ buttons, and a live % readout. */
export function ZoomControls({ zoom, onZoomIn, onZoomOut, onFit, maxZoom = MAX_ZOOM }: ZoomControlsProps) {
  const styles = useControlStyles();
  return (
    <div className={styles.zoomBar} title="Ctrl + Scroll to zoom">
      {onFit && (
        <>
          <Button
            size="small"
            appearance="subtle"
            icon={<ArrowMaximizeRegular />}
            aria-label="Fit to view"
            onClick={onFit}
          />
          <span className={styles.divider} aria-hidden />
        </>
      )}
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
        disabled={zoom >= maxZoom}
        onClick={onZoomIn}
      />
    </div>
  );
}
