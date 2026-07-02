import { useEffect, type ReactNode } from 'react';
import { Button, Title3, makeStyles, tokens } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';

// A reusable right-side slide-in overlay panel. Clicking the backdrop or the close button dismisses
// it. Kept intentionally simple (alpha): a fixed overlay + a sliding panel with a CSS transition.
const useStyles = makeStyles({
  backdrop: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0, 0, 0, 0.32)',
    zIndex: 1000,
    opacity: 0,
    pointerEvents: 'none',
    transition: 'opacity 180ms ease',
  },
  backdropOpen: {
    opacity: 1,
    pointerEvents: 'auto',
  },
  panel: {
    position: 'fixed',
    top: 0,
    right: 0,
    bottom: 0,
    width: 'min(520px, 92vw)',
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow28,
    zIndex: 1001,
    transform: 'translateX(100%)',
    transition: 'transform 220ms ease',
  },
  panelOpen: {
    transform: 'translateX(0)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    flexShrink: 0,
  },
  body: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    padding: tokens.spacingHorizontalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

export interface SlidePanelProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  /** Optional widths override for wider content (e.g. file browsers). */
  width?: string;
  children: ReactNode;
}

export function SlidePanel({ open, onClose, title, width, children }: SlidePanelProps) {
  const styles = useStyles();

  // Close on Escape while open.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  return (
    <>
      <div
        className={`${styles.backdrop}${open ? ` ${styles.backdropOpen}` : ''}`}
        aria-hidden="true"
        onClick={onClose}
      />
      <div
        className={`${styles.panel}${open ? ` ${styles.panelOpen}` : ''}`}
        role="dialog"
        aria-modal="true"
        aria-hidden={!open}
        style={width ? { width } : undefined}
      >
        <div className={styles.header}>
          <Title3>{title}</Title3>
          <Button
            appearance="subtle"
            icon={<DismissRegular />}
            aria-label="Close panel"
            onClick={onClose}
          />
        </div>
        <div className={styles.body}>{open ? children : null}</div>
      </div>
    </>
  );
}
