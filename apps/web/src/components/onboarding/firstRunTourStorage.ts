const TOUR_VERSION = 'v1';
const REQUIRED_SETUP_PENDING_KEY = 'agentweaver.requiredSetup.pending';
const REQUIRED_SETUP_RETURN_TO_KEY = 'agentweaver.requiredSetup.returnTo';

/** Builds the versioned browser-storage key for a user-specific first-run tour. */
export function firstRunTourStorageKey(userKey: string | null | undefined): string {
  const normalized = userKey?.trim().toLowerCase() || 'anonymous';
  return `agentweaver.firstRunTour.${TOUR_VERSION}.${encodeURIComponent(normalized)}`;
}

/** Checks whether the browser storage records the tour as complete; unavailable storage is false. */
export function hasCompletedFirstRunTour(storageKey: string): boolean {
  try {
    return localStorage.getItem(storageKey) === 'complete';
  } catch {
    return false;
  }
}

/** Persists tour completion when local storage is available; storage failure is ignored. */
export function markFirstRunTourComplete(storageKey: string): void {
  try {
    localStorage.setItem(storageKey, 'complete');
  } catch {
    // The tour remains dismissible when storage is not available.
  }
}

/** Checks the tab-scoped marker used while the user is completing required setup. */
export function hasRequiredSetupPending(): boolean {
  try {
    return sessionStorage.getItem(REQUIRED_SETUP_PENDING_KEY) === '1';
  } catch {
    return false;
  }
}

/** Marks required setup as pending in session storage, when available. */
export function markRequiredSetupPending(): void {
  try {
    sessionStorage.setItem(REQUIRED_SETUP_PENDING_KEY, '1');
  } catch {
    // The current setup page remains available if storage is not available.
  }
}

/** Clears the tab-scoped required-setup marker without failing when storage is unavailable. */
export function clearRequiredSetupPending(): void {
  try {
    sessionStorage.removeItem(REQUIRED_SETUP_PENDING_KEY);
  } catch {
    // The caller still continues with the current in-memory state.
  }
}

/**
 * Stores a safe same-site return path for after required setup.
 * Rejects external-looking paths and the platform-settings route; storage failures are ignored.
 */
export function rememberRequiredSetupReturnTo(returnTo: string): void {
  if (!returnTo.startsWith('/') || returnTo.startsWith('//')) return;
  const pathname = returnTo.split(/[?#]/, 1)[0];
  if (pathname === '/platform-settings') return;
  try {
    sessionStorage.setItem(REQUIRED_SETUP_RETURN_TO_KEY, returnTo);
  } catch {
    // Setup still falls back to the overview page when storage is not available.
  }
}

/** Returns a stored same-site return path, or `null` when absent, invalid, or unreadable. */
export function getRequiredSetupReturnTo(): string | null {
  try {
    const returnTo = sessionStorage.getItem(REQUIRED_SETUP_RETURN_TO_KEY);
    return returnTo?.startsWith('/') && !returnTo.startsWith('//') ? returnTo : null;
  } catch {
    return null;
  }
}

/** Removes the stored setup return path when session storage is available. */
export function clearRequiredSetupReturnTo(): void {
  try {
    sessionStorage.removeItem(REQUIRED_SETUP_RETURN_TO_KEY);
  } catch {
    // The caller still continues to the current in-memory destination.
  }
}
