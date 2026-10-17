/** Removes listeners and the mutation observer installed by the development-only focus guard. */
type Cleanup = () => void;

declare global {
  interface Window {
    __agentweaverImpeccableFocusGuard?: { cleanup: Cleanup };
  }
}

const STEER_FOCUS_WINDOW_MS = 2500;
const STEER_INPUT_SELECTOR = '#impeccable-live-page-chat-input';
const STEER_CONTAINER_SELECTOR = '#impeccable-live-page-chat';
const GLOBAL_BAR_SELECTOR = '#impeccable-live-global-bar';
const DIALOG_SELECTOR = '[role="dialog"][aria-modal="true"], .fui-DialogSurface';
const FOCUSABLE_DIALOG_SELECTOR = [
  'input:not([disabled])',
  'textarea:not([disabled])',
  'select:not([disabled])',
  'button:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

function isElement(node: EventTarget | null): node is Element {
  return node instanceof Element;
}

/** Returns the current modal surface, if one is mounted. */
function getActiveDialog(): HTMLElement | null {
  return document.querySelector<HTMLElement>(DIALOG_SELECTOR);
}

/** Finds the first enabled control in the current modal for restoring focus. */
function getFocusableInDialog(): HTMLElement | null {
  return getActiveDialog()?.querySelector<HTMLElement>(FOCUSABLE_DIALOG_SELECTOR) ?? null;
}

/** Recognizes pointer targets inside the live preview's global toolbar. */
function isInsideGlobalBar(node: EventTarget | null): boolean {
  return isElement(node) && Boolean(node.closest(GLOBAL_BAR_SELECTOR));
}

/** Recognizes the live-preview steering input or one of its containing elements. */
function isSteerInput(node: EventTarget | null): boolean {
  return isElement(node) && (node.matches(STEER_INPUT_SELECTOR) || Boolean(node.closest(STEER_CONTAINER_SELECTOR)));
}

/**
 * Installs document listeners and an observer that keep the live steering input from stealing modal focus.
 * Replaces any prior installation and returns a cleanup callback for hot reload or unmount.
 */
export function installImpeccableLiveFocusGuard(): Cleanup {
  window.__agentweaverImpeccableFocusGuard?.cleanup();

  let allowBarFocusUntil = 0;
  let lastModalFocus: HTMLElement | null = null;

  const updateSteerTabIndex = () => {
    const input = document.querySelector<HTMLElement>(STEER_INPUT_SELECTOR);
    if (!input) return;

    const modalIsOpen = Boolean(getActiveDialog());
    input.setAttribute('tabindex', !modalIsOpen || Date.now() < allowBarFocusUntil ? '0' : '-1');
  };

  const restoreModalFocus = () => {
    const target = lastModalFocus?.isConnected ? lastModalFocus : getFocusableInDialog();
    if (!target) return;

    queueMicrotask(() => target.focus({ preventScroll: true }));
  };

  const onPointerDown = (event: PointerEvent) => {
    if (isInsideGlobalBar(event.target)) {
      allowBarFocusUntil = Date.now() + STEER_FOCUS_WINDOW_MS;
      updateSteerTabIndex();
      return;
    }

    allowBarFocusUntil = 0;
    updateSteerTabIndex();
  };

  const onFocusIn = (event: FocusEvent) => {
    const activeDialog = getActiveDialog();

    if (activeDialog?.contains(event.target as Node | null) && isElement(event.target)) {
      lastModalFocus = event.target as HTMLElement;
      allowBarFocusUntil = 0;
      updateSteerTabIndex();
      return;
    }

    if (activeDialog && isSteerInput(event.target) && Date.now() > allowBarFocusUntil) {
      event.stopPropagation();
      restoreModalFocus();
    }
  };

  const onFocusOut = (event: FocusEvent) => {
    if (!isSteerInput(event.target)) return;

    window.setTimeout(() => {
      allowBarFocusUntil = 0;
      updateSteerTabIndex();
    }, 0);
  };

  const observer = new MutationObserver(updateSteerTabIndex);
  observer.observe(document.documentElement, { childList: true, subtree: true });

  document.addEventListener('pointerdown', onPointerDown, true);
  document.addEventListener('focusin', onFocusIn, true);
  document.addEventListener('focusout', onFocusOut, true);
  updateSteerTabIndex();

  const cleanup = () => {
    observer.disconnect();
    document.removeEventListener('pointerdown', onPointerDown, true);
    document.removeEventListener('focusin', onFocusIn, true);
    document.removeEventListener('focusout', onFocusOut, true);
    delete window.__agentweaverImpeccableFocusGuard;
  };

  window.__agentweaverImpeccableFocusGuard = { cleanup };
  return cleanup;
}
