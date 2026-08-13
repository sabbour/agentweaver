/**
 * AppDialog — native FluentUI v9 dialog wrapper styled to match copilot.com's
 * Day-theme "Create project" modal (measured 2026-07-10):
 *   - Surface: warm canvas bg (#f8f4f1), 20px radius, layered soft shadow, 1px stroke2 ring
 *   - Backdrop: rgba(0,0,0,0.10) + backdrop-blur 2px
 *   - Footer pattern (when primaryAction is provided): full-width primary + text Cancel below
 *
 * No imports from copilot-fluent-system — native @fluentui/react-components only.
 */

import {
  Button,
  Dialog,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  makeStyles,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import type { ReactElement, ReactNode } from 'react';

export interface AppDialogPrimaryAction {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  loading?: boolean;
}

export interface AppDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Optional trigger element; rendered inside DialogTrigger. */
  trigger?: ReactElement;
  /** Centered dialog title. If omitted, include <DialogTitle> in children for a11y. */
  title?: string;
  /** Muted description beneath the title. */
  description?: string;
  children?: ReactNode;
  /** When provided, renders a full-width primary button + text Cancel footer. */
  primaryAction?: AppDialogPrimaryAction;
  /** Override cancel label (default: "Cancel"). */
  cancel?: string;
  /** Custom footer slot — overrides primaryAction / cancel when provided. */
  footer?: ReactNode;
  /** Max width of the dialog surface (default: "480px"). */
  maxWidth?: string;
  /** Show the absolute-positioned close button (default: true). */
  showClose?: boolean;
  /**
   * Fluent dialog modality. Default `modal` closes when focus leaves the surface
   * (e.g. mid-re-render after an in-dialog control click). Use `alert` for dense
   * multi-control dialogs that must stay open across internal state updates.
   */
  modalType?: 'modal' | 'non-modal' | 'alert';
}

const useStyles = makeStyles({
  surface: {
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: '20px',
    boxShadow:
      '0 16px 24px rgba(0,0,0,0.08), 0 8px 16px rgba(0,0,0,0.03), 0 0 1px rgba(0,0,0,0.08)',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    // Do NOT set position here — Fluent's DialogSurface uses position:fixed +
    // inset:0 + margin:auto to center the dialog. Overriding to 'relative'
    // drops it into normal document flow, pinning it to the top of the window.
    // The close button (position:absolute) is still correctly contained because
    // position:fixed creates a containing block for absolute descendants.
    maxHeight: 'calc(100vh - 48px)',
    overflowY: 'auto',
  },
  backdrop: {
    backgroundColor: 'rgba(0, 0, 0, 0.10)',
    backdropFilter: 'blur(2px)',
    WebkitBackdropFilter: 'blur(2px)',
  },
  closeButton: {
    position: 'absolute',
    right: tokens.spacingHorizontalM,
    top: tokens.spacingVerticalM,
    minWidth: '32px',
    border: '0',
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalXS,
    textAlign: 'center',
  },
  titleText: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase500,
  },
  description: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
  },
  footer: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'stretch',
    gap: tokens.spacingVerticalXS,
    marginTop: tokens.spacingVerticalS,
  },
  primaryButton: {
    width: '100%',
    justifyContent: 'center',
  },
  cancelButton: {
    width: '100%',
    justifyContent: 'center',
  },
});

export function AppDialog({
  open,
  onOpenChange,
  trigger,
  title,
  description,
  children,
  primaryAction,
  cancel = 'Cancel',
  footer,
  maxWidth = '480px',
  showClose = true,
  modalType = 'modal',
}: AppDialogProps) {
  const styles = useStyles();

  const surfaceNode = (
    <DialogSurface
      className={styles.surface}
      style={{ maxWidth, width: `min(${maxWidth}, calc(100vw - 48px))` }}
      backdrop={{ className: styles.backdrop }}
    >
      {showClose && (
        <DialogTrigger disableButtonEnhancement>
          <Button
            className={styles.closeButton}
            appearance="subtle"
            icon={<DismissRegular />}
            aria-label="Close"
          />
        </DialogTrigger>
      )}
      <div className={styles.body}>
        {title && (
          <div className={styles.header}>
            <DialogTitle className={styles.titleText}>{title}</DialogTitle>
            {description && (
              <Text className={styles.description}>{description}</Text>
            )}
          </div>
        )}
        {children}
        {footer}
        {!footer && primaryAction && (
          <div className={styles.footer}>
            <Button
              appearance="primary"
              className={styles.primaryButton}
              disabled={primaryAction.disabled || primaryAction.loading}
              onClick={primaryAction.onClick}
            >
              {primaryAction.loading && (
                <Spinner size="tiny" aria-hidden="true" style={{ marginRight: '6px' }} />
              )}
              {primaryAction.label}
            </Button>
            <Button
              appearance="transparent"
              className={styles.cancelButton}
              onClick={() => onOpenChange(false)}
            >
              {cancel}
            </Button>
          </div>
        )}
      </div>
    </DialogSurface>
  );

  // Dialog requires children to be exactly `JSXElement` (surface only) or
  // `[JSXElement, JSXElement]` (trigger + surface). Conditional rendering
  // inside a single return would produce `undefined | JSXElement` for the
  // trigger slot, which fails that union — so we use two explicit render paths.
  if (trigger) {
    return (
      <Dialog open={open} modalType={modalType} onOpenChange={(_, state) => onOpenChange(state.open)}>
        <DialogTrigger disableButtonEnhancement>{trigger}</DialogTrigger>
        {surfaceNode}
      </Dialog>
    );
  }

  return (
    <Dialog open={open} modalType={modalType} onOpenChange={(_, state) => onOpenChange(state.open)}>
      {surfaceNode}
    </Dialog>
  );
}
