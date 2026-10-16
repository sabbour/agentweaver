import {
  Button,
  makeStyles,
  mergeClasses,
  Title3,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useRef } from 'react';
import type { ReactNode } from 'react';
// A reusable right-side slide-in overlay panel. Clicking the backdrop or the close button dismisses
// it. Kept intentionally simple: a fixed overlay + a sliding panel with a CSS transition.
const useStyles = makeStyles({
  backdrop: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0, 0, 0, 0.40)',
    zIndex: 1000,
    opacity: 0,
    pointerEvents: 'none',
    transition: 'opacity 180ms cubic-bezier(0.22, 1, 0.36, 1)',
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
    visibility: 'hidden',
    pointerEvents: 'none',
    transition: 'transform 220ms cubic-bezier(0.22, 1, 0.36, 1)',
  },
  copilotPanel: {
    top: '40px',
    bottom: tokens.spacingVerticalM,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    borderTopLeftRadius: tokens.borderRadiusXLarge,
    borderBottomLeftRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    boxShadow: '0 28px 80px rgba(0, 0, 0, 0.22), 0 8px 24px rgba(0, 0, 0, 0.16)',
  },
  panelOpen: {
    transform: 'translateX(0)',
    visibility: 'visible',
    pointerEvents: 'auto',
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
  copilotHeader: {
    minHeight: '52px',
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: tokens.colorNeutralBackgroundInverted,
    borderBottom: '1px solid rgba(255, 255, 255, 0.12)',
    boxShadow: 'inset 0 -1px 0 rgba(255, 255, 255, 0.12)',
    '& .fui-Button': {
      color: tokens.colorNeutralForegroundOnBrand,
    },
    '& .fui-Title3': {
      color: tokens.colorNeutralForegroundOnBrand,
      letterSpacing: '-0.01em',
    },
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
  bodyFlush: {
    overflowY: 'hidden',
    padding: 0,
    gap: 0,
  },
  footer: {
    flexShrink: 0,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

const focusableSelector = [
  'a[href]',
  'area[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  'iframe',
  'object',
  'embed',
  '[contenteditable="true"]',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

function getFocusableElements(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(focusableSelector))
    .filter((element) => (
      element.tabIndex >= 0
      && !element.closest('[aria-hidden="true"]')
      && element.getAttribute('aria-disabled') !== 'true'
    ));
}

function restoreFocus(element: HTMLElement | null) {
  if (!element?.isConnected) return;
  element.focus();
}

export interface SlidePanelProps {
  open: boolean;
  id?: string;
  ariaLabel?: string;
  onClose: () => void;
  title: ReactNode;
  /** Optional widths override for wider content (e.g. file browsers). */
  width?: string;
  /** Keep expensive/persistent content mounted while the panel is closed. */
  keepMounted?: boolean;
  /** Remove default body padding for full-bleed panel content. */
  flushBody?: boolean;
  bodyClassName?: string;
  variant?: 'default' | 'copilotDock';
  /** Optional content pinned below the scrollable body (e.g. action buttons that must stay
   * visible without scrolling). Renders nothing when absent. */
  footer?: ReactNode;
  children: ReactNode;
}

export function SlidePanel({
  open,
  id,
  ariaLabel: ariaLabelProp,
  onClose,
  title,
  width,
  keepMounted = false,
  flushBody = false,
  bodyClassName,
  variant = 'default',
  footer,
  children,
}: SlidePanelProps) {
  const styles = useStyles();
  const ariaLabel = ariaLabelProp ?? (typeof title === 'string' ? title : undefined);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const closeButtonRef = useRef<HTMLButtonElement | null>(null);
  const openerRef = useRef<HTMLElement | null>(null);
  const wasOpenRef = useRef(false);
  const closeAndRestoreFocus = useCallback(() => {
    restoreFocus(openerRef.current);
    onClose();
  }, [onClose]);

  useEffect(() => {
    const panel = panelRef.current;
    if (!open) {
      if (wasOpenRef.current) {
        wasOpenRef.current = false;
        restoreFocus(openerRef.current);
        openerRef.current = null;
      }
      return;
    }

    wasOpenRef.current = true;
    const activeElement = document.activeElement;
    if (activeElement instanceof HTMLElement && (!panel || !panel.contains(activeElement))) {
      openerRef.current = activeElement;
    }

    (closeButtonRef.current ?? (panel ? getFocusableElements(panel)[0] : null) ?? panel)?.focus();
  }, [open]);

  useEffect(() => {
    if (!open) return;

    const onKeyDown = (e: KeyboardEvent) => {
      const panel = panelRef.current;
      if (!panel) return;

      if (e.key === 'Escape') {
        e.preventDefault();
        closeAndRestoreFocus();
        return;
      }

      if (e.key !== 'Tab') return;

      const focusable = getFocusableElements(panel);
      if (focusable.length === 0) {
        e.preventDefault();
        panel.focus();
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;

      if (e.shiftKey) {
        if (!activeElement || activeElement === first || !panel.contains(activeElement)) {
          e.preventDefault();
          last.focus();
        }
        return;
      }

      if (!activeElement || activeElement === last || !panel.contains(activeElement)) {
        e.preventDefault();
        first.focus();
      }
    };

    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, [open, closeAndRestoreFocus]);

  return (
    <>
      <div
        className={`${styles.backdrop}${open ? ` ${styles.backdropOpen}` : ''}`}
        aria-hidden="true"
        onClick={closeAndRestoreFocus}
      />
      <div
        ref={panelRef}
        id={id}
        className={mergeClasses(styles.panel, variant === 'copilotDock' && styles.copilotPanel, open && styles.panelOpen)}
        role="dialog"
        aria-modal={open ? 'true' : undefined}
        aria-label={ariaLabel}
        aria-hidden={open ? undefined : true}
        tabIndex={open ? -1 : undefined}
        style={width ? { width } : undefined}
      >
        <div className={mergeClasses(styles.header, variant === 'copilotDock' && styles.copilotHeader)}>
          <Title3>{title}</Title3>
          <Button
            ref={closeButtonRef}
            appearance="subtle"
            icon={<DismissRegular />}
            aria-label="Close panel"
            tabIndex={open ? 0 : -1}
            onClick={closeAndRestoreFocus}
          />
        </div>
        <div className={mergeClasses(styles.body, flushBody && styles.bodyFlush, bodyClassName)}>
          {open || keepMounted ? children : null}
        </div>
        {footer != null && (
          <div className={styles.footer}>
            {footer}
          </div>
        )}
      </div>
    </>
  );
}
