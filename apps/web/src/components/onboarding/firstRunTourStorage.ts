const TOUR_VERSION = 'v1';
const REQUIRED_SETUP_PENDING_KEY = 'agentweaver.requiredSetup.pending';

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
