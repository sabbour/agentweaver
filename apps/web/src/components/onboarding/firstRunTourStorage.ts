const TOUR_VERSION = 'v1';
const REQUIRED_SETUP_PENDING_KEY = 'agentweaver.requiredSetup.pending';
const REQUIRED_SETUP_RETURN_TO_KEY = 'agentweaver.requiredSetup.returnTo';

export function firstRunTourStorageKey(userKey: string | null | undefined): string {
  const normalized = userKey?.trim().toLowerCase() || 'anonymous';
  return `agentweaver.firstRunTour.${TOUR_VERSION}.${encodeURIComponent(normalized)}`;
}

export function hasCompletedFirstRunTour(storageKey: string): boolean {
  try {
    return localStorage.getItem(storageKey) === 'complete';
  } catch {
    return false;
  }
}

export function markFirstRunTourComplete(storageKey: string): void {
  try {
    localStorage.setItem(storageKey, 'complete');
  } catch {
    // The tour remains dismissible when storage is not available.
  }
}

export function hasRequiredSetupPending(): boolean {
  try {
    return sessionStorage.getItem(REQUIRED_SETUP_PENDING_KEY) === '1';
  } catch {
    return false;
  }
}

export function markRequiredSetupPending(): void {
  try {
    sessionStorage.setItem(REQUIRED_SETUP_PENDING_KEY, '1');
  } catch {
    // The current setup page remains available if storage is not available.
  }
}

export function clearRequiredSetupPending(): void {
  try {
    sessionStorage.removeItem(REQUIRED_SETUP_PENDING_KEY);
  } catch {
    // The caller still continues with the current in-memory state.
  }
}

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

export function getRequiredSetupReturnTo(): string | null {
  try {
    const returnTo = sessionStorage.getItem(REQUIRED_SETUP_RETURN_TO_KEY);
    return returnTo?.startsWith('/') && !returnTo.startsWith('//') ? returnTo : null;
  } catch {
    return null;
  }
}

export function clearRequiredSetupReturnTo(): void {
  try {
    sessionStorage.removeItem(REQUIRED_SETUP_RETURN_TO_KEY);
  } catch {
    // The caller still continues to the current in-memory destination.
  }
}
