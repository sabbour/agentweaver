import { brokerBaseUrl } from './config';

const REPO_APP_ROUTE = '/auth/github/repo-app';
/** Gateway-root callback page used to report successful Repo App setup. */
export const REPO_APP_CALLBACK_PATH = '/settings/source-control';
/** Same-origin popup message discriminator for GitHub App connection callbacks. */
export const REPO_APP_CALLBACK_MESSAGE_TYPE = 'agentweaver.repo-app.connected';

/** Durable Identity Broker state for a user's GitHub Repo App connection. */
export type RepoAppConnectionState =
  | 'not_connected'
  | 'connected'
  | 'revoked'
  | 'rotation_uncertain';

/** Local readiness of credentials used to access GitHub through the connection. */
export type RepoAppLocalReadiness =
  | 'not_connected'
  | 'reauthorization_required'
  | 'rotation_uncertain'
  | 'refresh_in_progress'
  | 'access_token_available'
  | 'refresh_required';

/** Redacted Repo App connection status returned to the browser. */
export interface RepoAppConnectionStatus {
  state: RepoAppConnectionState;
  localReadiness: RepoAppLocalReadiness;
  connectionId: string | null;
  connectionRevision: number | null;
  githubLogin: string | null;
  accessTokenExpiresAt: string | null;
  updatedAt: string | null;
}

/** Repository metadata visible through the current GitHub installation. */
export interface RepoAppRepository {
  installationId: number;
  repositoryId: number;
  fullName: string;
  ownerLogin: string;
  isPrivate: boolean;
  defaultBranch: string;
}

/** Repository list with the connection revision used to fetch it. */
export interface RepoAppRepositoryList {
  connectionId: string;
  connectionRevision: number;
  githubLogin: string;
  repositories: RepoAppRepository[];
}

/** Short-lived server code binding a selected repository to a connection revision. */
export interface RepoAppRepositorySelection {
  code: string;
  connectionId: string;
  connectionRevision: number;
  installationId: number;
  repositoryId: number;
  repositoryFullName: string;
}

/** Same-origin popup notification that a Repo App callback completed. */
export interface RepoAppConnectedCallbackMessage {
  type: typeof REPO_APP_CALLBACK_MESSAGE_TYPE;
}

declare global {
  interface Window {
    __AGENTWEAVER_REPO_APP_CALLBACK__?: unknown;
  }
}

/** Error returned by the Broker client, preserving status and a safe error code. */
export class RepoAppBrokerError extends Error {
  readonly status: number;
  readonly code?: string;

  constructor(status: number, code: string | undefined, message: string) {
    super(message);
    this.name = 'RepoAppBrokerError';
    this.status = status;
    this.code = code;
  }
}

/** Narrows JSON data to a plain object before validating its exact contract. */
function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** Rejects response objects that include fields outside their declared shape. */
function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

function isNullableDateTime(value: unknown): value is string | null {
  return value === null || (typeof value === 'string' && Number.isFinite(Date.parse(value)));
}

function isPositiveSafeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && Number(value) > 0;
}

/** Validates the bounded and redacted status contract returned by the Broker. */
function isRepoAppConnectionStatus(value: unknown): value is RepoAppConnectionStatus {
  if (!isObject(value) || !hasExactKeys(value, [
    'state',
    'localReadiness',
    'connectionId',
    'connectionRevision',
    'githubLogin',
    'accessTokenExpiresAt',
    'updatedAt',
  ])) return false;

  return (
    ['not_connected', 'connected', 'revoked', 'rotation_uncertain'].includes(String(value.state)) &&
    [
      'not_connected',
      'reauthorization_required',
      'rotation_uncertain',
      'refresh_in_progress',
      'access_token_available',
      'refresh_required',
    ].includes(String(value.localReadiness)) &&
    (value.connectionId === null || isNonEmptyString(value.connectionId)) &&
    (value.connectionRevision === null || isPositiveSafeInteger(value.connectionRevision)) &&
    (value.githubLogin === null || isNonEmptyString(value.githubLogin)) &&
    isNullableDateTime(value.accessTokenExpiresAt) &&
    isNullableDateTime(value.updatedAt)
  );
}

/** Validates one repository entry before it reaches the project UI. */
function isRepoAppRepository(value: unknown): value is RepoAppRepository {
  return isObject(value) &&
    hasExactKeys(value, [
      'installationId',
      'repositoryId',
      'fullName',
      'ownerLogin',
      'isPrivate',
      'defaultBranch',
    ]) &&
    isPositiveSafeInteger(value.installationId) &&
    isPositiveSafeInteger(value.repositoryId) &&
    isNonEmptyString(value.fullName) &&
    isNonEmptyString(value.ownerLogin) &&
    typeof value.isPrivate === 'boolean' &&
    isNonEmptyString(value.defaultBranch);
}

/** Validates the full repository list and its connection-version metadata. */
function isRepoAppRepositoryList(value: unknown): value is RepoAppRepositoryList {
  return isObject(value) &&
    hasExactKeys(value, ['connectionId', 'connectionRevision', 'githubLogin', 'repositories']) &&
    isNonEmptyString(value.connectionId) &&
    isPositiveSafeInteger(value.connectionRevision) &&
    isNonEmptyString(value.githubLogin) &&
    Array.isArray(value.repositories) &&
    value.repositories.every(isRepoAppRepository);
}

/** Validates a repository-selection code and the connection revision it pins. */
function isRepoAppRepositorySelection(value: unknown): value is RepoAppRepositorySelection {
  return isObject(value) &&
    hasExactKeys(value, [
      'code',
      'connectionId',
      'connectionRevision',
      'installationId',
      'repositoryId',
      'repositoryFullName',
    ]) &&
    isNonEmptyString(value.code) &&
    isNonEmptyString(value.connectionId) &&
    isPositiveSafeInteger(value.connectionRevision) &&
    isPositiveSafeInteger(value.installationId) &&
    isPositiveSafeInteger(value.repositoryId) &&
    isNonEmptyString(value.repositoryFullName);
}

function isCsrfResponse(value: unknown): value is { csrf_token: string } {
  return isObject(value) &&
    hasExactKeys(value, ['csrf_token']) &&
    isNonEmptyString(value.csrf_token);
}

function errorCode(value: unknown): string | undefined {
  return isObject(value) && typeof value.error === 'string' ? value.error : undefined;
}

function errorMessage(value: unknown, status: number): string {
  const code = errorCode(value);
  return code
    ? `Identity Broker Repo App request failed with HTTP ${status} (${code}).`
    : `Identity Broker Repo App request failed with HTTP ${status}.`;
}

/** Narrows a message to the exact Repo App callback notification shape. */
export function isRepoAppConnectedCallbackMessage(
  value: unknown,
): value is RepoAppConnectedCallbackMessage {
  return isObject(value) &&
    hasExactKeys(value, ['type']) &&
    value.type === REPO_APP_CALLBACK_MESSAGE_TYPE;
}

/** Accepts only the tracked popup's same-origin message for the active attempt. */
export function isCurrentRepoAppPopupCallback(
  event: Pick<MessageEvent<unknown>, 'origin' | 'source' | 'data'>,
  expectedOrigin: string,
  trackedPopup: Window | null,
  trackedGeneration: number | null,
  currentGeneration: number,
): boolean {
  return trackedPopup !== null &&
    event.origin === expectedOrigin &&
    event.source === trackedPopup &&
    trackedGeneration !== null &&
    trackedGeneration === currentGeneration &&
    isRepoAppConnectedCallbackMessage(event.data);
}

/**
 * Browser client for Identity Broker Repo App operations.
 * Reads include credentials and no-store caching; mutations obtain an antiforgery
 * token first and validate exact response contracts before returning data.
 */
export class IdentityBrokerRepoAppClient {
  private readonly baseUrl: string;
  private readonly fetcher: typeof fetch;
  private readonly documentRef: Document;

  constructor(
    baseUrl = brokerBaseUrl,
    fetcher: typeof fetch = fetch,
    documentRef: Document = document,
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.fetcher = fetcher;
    this.documentRef = documentRef;
  }

  /** Builds a Broker URL or fails when the Broker endpoint is not configured. */
  private url(path: string): string {
    if (!this.baseUrl) {
      throw new RepoAppBrokerError(
        0,
        'configuration_missing',
        'Configure the Identity Broker URL before using GitHub Repo App.',
      );
    }
    return `${this.baseUrl}${REPO_APP_ROUTE}${path}`;
  }

  /** Sends one Broker request and rejects non-success or malformed JSON responses. */
  private async requestJson<T>(
    path: string,
    init: RequestInit,
    isResponse: (value: unknown) => value is T,
  ): Promise<T> {
    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json');
    if (init.body !== undefined) headers.set('Content-Type', 'application/json');
    let response: Response;
    try {
      response = await this.fetcher.call(globalThis, this.url(path), {
        ...init,
        headers,
        credentials: 'include',
        cache: 'no-store',
      });
    } catch (reason) {
      throw reason instanceof Error
        ? reason
        : new Error('Identity Broker Repo App request failed before receiving a response.');
    }

    if (!response.ok) {
      const problem: unknown = await response.json().catch(() => null);
      const code = errorCode(problem);
      throw new RepoAppBrokerError(response.status, code, errorMessage(problem, response.status));
    }

    let payload: unknown;
    try {
      payload = await response.json();
    } catch {
      throw new RepoAppBrokerError(
        502,
        'broker_contract_invalid',
        'Identity Broker returned an invalid Repo App JSON response.',
      );
    }
    if (!isResponse(payload)) {
      throw new RepoAppBrokerError(
        502,
        'broker_contract_invalid',
        'Identity Broker returned an invalid Repo App contract.',
      );
    }
    return payload;
  }

  /** Obtains an antiforgery token for a subsequent state-changing request. */
  private async csrfToken(signal?: AbortSignal): Promise<string> {
    const result = await this.requestJson('/csrf', { method: 'GET', signal }, isCsrfResponse);
    return result.csrf_token;
  }

  /** Reads the current redacted Repo App connection and credential readiness. */
  getStatus(): Promise<RepoAppConnectionStatus> {
    return this.requestJson('/status', { method: 'GET' }, isRepoAppConnectionStatus);
  }

  /** Reads repositories available to the active GitHub installation. */
  getRepositories(): Promise<RepoAppRepositoryList> {
    return this.requestJson('/repositories', { method: 'GET' }, isRepoAppRepositoryList);
  }

  /** Starts connection authorization by submitting a Broker form into the tracked popup. */
  async beginConnect(
    popup: Pick<Window, 'name' | 'closed'>,
    signal?: AbortSignal,
  ): Promise<void> {
    await this.submitOAuthForm('connect', popup, signal);
  }

  /** Opens the installation setup flow in the tracked Broker popup. */
  async beginInstallationSetup(
    popup: Pick<Window, 'name' | 'closed'>,
    signal?: AbortSignal,
  ): Promise<void> {
    await this.submitOAuthForm('install', popup, signal);
  }

  /** Disconnects only if the connection still has the caller's expected revision. */
  async disconnect(
    connectionId: string,
    expectedConnectionRevision: number,
  ): Promise<RepoAppConnectionStatus> {
    const csrfToken = await this.csrfToken();
    return this.requestJson('/disconnect', {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': csrfToken },
      body: JSON.stringify({ connectionId, expectedConnectionRevision }),
    }, isRepoAppConnectionStatus);
  }

  /** Creates a short-lived selection code for the chosen installation repository. */
  async createRepositorySelection(
    installationId: number,
    repositoryId: number,
  ): Promise<RepoAppRepositorySelection> {
    const csrfToken = await this.csrfToken();
    return this.requestJson('/selection', {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': csrfToken },
      body: JSON.stringify({ installationId, repositoryId }),
    }, isRepoAppRepositorySelection);
  }

  /** Fetches CSRF state and submits the Broker authorization form to the named popup. */
  private async submitOAuthForm(
    action: 'connect' | 'install',
    popup: Pick<Window, 'name' | 'closed'>,
    signal?: AbortSignal,
  ): Promise<void> {
    if (popup.closed || !popup.name || popup.name.startsWith('_')) {
      throw new Error('Open the tracked GitHub Repo App popup before starting authorization.');
    }
    if (signal?.aborted) {
      throw new Error('The GitHub Repo App authorization request was cancelled.');
    }
    const csrfToken = await this.csrfToken(signal);
    if (signal?.aborted) {
      throw new Error('The GitHub Repo App authorization request was cancelled.');
    }
    if (popup.closed) {
      throw new Error('The GitHub Repo App popup was closed before authorization could start.');
    }
    const form = this.documentRef.createElement('form');
    form.method = 'post';
    form.action = this.url(`/${action}`);
    form.target = popup.name;
    form.hidden = true;
    form.setAttribute('aria-hidden', 'true');
    const tokenField = this.documentRef.createElement('input');
    tokenField.type = 'hidden';
    tokenField.name = '__RequestVerificationToken';
    tokenField.value = csrfToken;
    form.append(tokenField);

    if (!this.documentRef.body) {
      throw new Error('The page cannot submit the GitHub Repo App authorization form.');
    }
    if (signal?.aborted) {
      throw new Error('The GitHub Repo App authorization request was cancelled.');
    }
    this.documentRef.body.append(form);
    try {
      form.submit();
    } finally {
      form.remove();
    }
  }
}

/** Shared Browser client for Identity Broker Repo App endpoints. */
export const repoAppBrokerClient = new IdentityBrokerRepoAppClient();
