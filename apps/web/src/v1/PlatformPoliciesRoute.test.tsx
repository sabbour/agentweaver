import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthorizationCallbackMessage, AuthorizationTransaction } from './authProtocol';
import { AgentweaverGatewayClient, gatewayClient } from './api';
import {
  firstRunTourStorageKey,
  markFirstRunTourComplete,
} from '../components/onboarding/firstRunTourStorage';
import { savePendingRun } from './pendingRun';

const authMocks = vi.hoisted(() => ({
  buildAuthorizeUrl: vi.fn((
    _config: unknown,
    transaction: { state: string },
    _challenge: string,
    prompt?: 'none',
  ) => `https://broker.test/authorize?state=${encodeURIComponent(transaction.state)}${prompt ? '&prompt=none' : ''}`),
  exchangeAuthorizationCode: vi.fn(),
  parseAuthorizationCallbackMessage: (value: unknown) => value as AuthorizationCallbackMessage,
  refreshBrokerToken: vi.fn(),
}));

vi.mock('./authProtocol', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./authProtocol')>();
  return {
    ...actual,
    buildAuthorizeUrl: authMocks.buildAuthorizeUrl,
    clearAuthorizationTransaction: vi.fn(),
    defaultAuthorizationConfig: () => ({
      brokerUrl: 'https://broker.test',
      clientId: 'client-1',
      redirectUri: 'https://app.test/auth/callback',
      scopes: ['agentweaver.api'],
    }),
    exchangeAuthorizationCode: authMocks.exchangeAuthorizationCode,
    isConsentPrompt: vi.fn(() => false),
    parseAuthorizationCallbackMessage: authMocks.parseAuthorizationCallbackMessage,
    parseAuthorizationCallbackParameters: vi.fn(() => null),
    parseAuthorizationResult: (responseUrl: string, expectedState: string) => {
      const url = new URL(responseUrl);
      if (url.searchParams.get('state') !== expectedState) return null;
      const code = url.searchParams.get('code');
      return code ? { code } : null;
    },
    pkceChallenge: async () => 'challenge',
    randomState: () => 'state-1',
    randomVerifier: () => 'verifier-1',
    readAuthorizationTransaction: vi.fn<() => AuthorizationTransaction | null>(() => null),
    refreshBrokerToken: authMocks.refreshBrokerToken,
    storeAuthorizationTransaction: vi.fn(),
    submitBrokerConsent: vi.fn(),
  };
});

vi.mock('./config', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./config')>()),
  gatewayBaseUrl: '/api/v1',
  brokerBaseUrl: 'https://broker.test',
  oauthClientId: 'client-1',
  oauthRedirectUri: 'https://app.test/auth/callback',
  oauthScopes: ['agentweaver.api'],
  missingAuthConfiguration: () => [],
}));

import App from './App';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider, useAuth } from './AuthContext';
import {
  buildPlatformPolicyValidationArtifact,
  createPlatformPolicyTextBuffer,
  parsePlatformPolicyValidationRequestFile,
} from './platformPolicyContracts';
import {
  PlatformPoliciesControllerProvider,
  usePlatformPoliciesController,
} from './PlatformPoliciesController';
import { findPlatformPolicyDiagnosticDraftFile } from './platformPolicyDraftNavigation';

const hash = 'a'.repeat(64);
const policyText = '<img src=x onerror=alert(1)>';

function bytes(value: string): number[] {
  return [...new TextEncoder().encode(value)];
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function policyHistory() {
  return {
    scopeType: 'platform',
    scopeId: 'default',
    policyId: 'policy-1',
    snapshotRevision: 2,
    items: omitSnapshotHead
      ? [{ revision: 1, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' }]
      : [
          { revision: 2, contentSha256: hash, createdAt: '2026-01-03T03:04:05Z' },
          { revision: 1, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' },
        ],
    nextCursor: null,
  };
}

function policyRevision() {
  return {
    scopeType: 'platform',
    scopeId: 'default',
    policyId: 'policy-1',
    revision: 2,
    contentSha256: hash,
    createdAt: '2026-01-03T03:04:05Z',
    artifact: {
      manifestBytes: bytes('name: saved-policy\n'),
      files: [{ relativePath: 'rules/example.yaml', bytes: bytes(policyText) }],
      contentSha256: hash,
    },
  };
}

function olderPolicyRevision() {
  return {
    ...policyRevision(),
    revision: 1,
    createdAt: '2026-01-02T03:04:05Z',
    artifact: {
      manifestBytes: bytes('\uFEFFname: saved-policy\r\n'),
      files: [{
        relativePath: 'rules/example.yaml',
        bytes: bytes('saved \t\\n\r\n'),
      }],
      contentSha256: hash,
    },
  };
}

let popup: Window;
let policyStatus: number;
let validationStatus: number;
let omitSnapshotHead: boolean;
let delayHistory: boolean;
let unauthorizedTenantOnce: string | null;
let unauthorizedValidationTenantOnce: string | null;
let resolveHistory: ((response: Response) => void) | null;
let resolvePlatformValidation: ((response: Response) => void) | null;
let requests: { url: string; init?: RequestInit }[];
let projectValidationStatus: number;
let projectAuthority: boolean;
let unauthorizedProjectValidationOnce: boolean;
let abortProjectValidationOnce: boolean;
let resolveProjectValidation: ((response: Response) => void) | null;
let validationDiagnostics: Array<{
  code: string;
  message: string;
  relativePath: string | null;
  line: number | null;
  column: number | null;
}>;

async function completeSignIn(code = 'code-1'): Promise<void> {
  await waitFor(() => expect(fetch).toHaveBeenCalled());
  await act(async () => {
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://app.test',
      source: popup,
      data: {
        type: 'agentweaver.identity.callback',
        code,
        state: 'state-1',
      },
    }));
  });
}

function policyRequests() {
  return requests.filter(({ url }) => url.includes('/policies/platform/revisions'));
}

function policyValidationRequests() {
  return requests.filter(({ url }) => url.includes('/policies/platform/validate'));
}

async function completePolicySignIn(): Promise<void> {
  await waitFor(() => expect(
    document.querySelector('iframe[title="Platform Policy sign-in"]'),
  ).not.toBeNull());
  const iframe = document.querySelector<HTMLIFrameElement>('iframe[title="Platform Policy sign-in"]')!;
  await act(async () => {
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://app.test',
      source: iframe.contentWindow,
      data: {
        type: 'agentweaver.identity.callback',
        code: 'policy-code',
        state: 'state-1',
      },
    }));
  });
}

function PolicyRecoveryHarness() {
  const auth = useAuth();
  const policy = usePlatformPoliciesController();
  return (
    <>
      <button type="button" onClick={() => { void auth.authorize({ projectId: 'project-1', runId: 'run-1' }); }}>
        Sign in for active run
      </button>
      <button type="button" onClick={policy.retry}>Retry policy</button>
      <label>
        Tenant ID
        <input
          value={policy.tenantId}
          onChange={(event) => policy.setTenantId(event.target.value)}
        />
      </label>
      <output>session:{auth.session?.accessToken ?? 'signed-out'}</output>
      <output>policy-status:{policy.status}</output>
      <output>policy-authorized:{String(policy.authorized)}</output>
    </>
  );
}

function renderPolicyRecoveryHarness() {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={['/policies']}>
        <PlatformPoliciesControllerProvider>
          <PolicyRecoveryHarness />
        </PlatformPoliciesControllerProvider>
      </MemoryRouter>
    </AuthProvider>,
  );
}

async function openProjectPolicyDraft(): Promise<void> {
  projectAuthority = true;
  render(<App />);
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await completeSignIn();
  await screen.findByText('Saved Platform Policy revisions');
  fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
    target: { value: 'project' },
  });
  const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
  await screen.findByRole('option', { name: /Project One/ });
  fireEvent.change(projectPicker, { target: { value: 'project-1' } });
  await waitFor(() => expect(
    (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
  ).toBe(false));
}

async function openPlatformPolicyDraft(): Promise<void> {
  render(<App />);
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await completeSignIn();
  await screen.findByText('Saved Platform Policy revisions');
  await waitFor(() => expect(
    (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
  ).toBe(false));
}

async function startRunBoundPolicySession(): Promise<void> {
  renderPolicyRecoveryHarness();
  fireEvent.click(screen.getByRole('button', { name: 'Sign in for active run' }));
  await completeSignIn('run-code');
  await screen.findByText('session:run-token');
  await completePolicySignIn();
  await waitFor(() => expect(
    document.querySelector('iframe[title="Platform Policy sign-in"]'),
  ).toBeNull());
  await waitFor(() => expect(policyRequests().length).toBeGreaterThan(0));
}

describe('Platform Policies served route', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/policies');
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    window.localStorage.clear();
    window.sessionStorage.clear();
    vi.clearAllMocks();
    policyStatus = 200;
    validationStatus = 200;
    projectValidationStatus = 200;
    projectAuthority = false;
    unauthorizedProjectValidationOnce = false;
    abortProjectValidationOnce = false;
    resolveProjectValidation = null;
    validationDiagnostics = [{
      code: 'yaml.invalid',
      message: 'The manifest is invalid.',
      relativePath: null,
      line: 2,
      column: 1,
    }];
    omitSnapshotHead = false;
    delayHistory = false;
    unauthorizedTenantOnce = null;
    unauthorizedValidationTenantOnce = null;
    resolveHistory = null;
    resolvePlatformValidation = null;
    requests = [];
    authMocks.exchangeAuthorizationCode.mockResolvedValue({
      access_token: 'access-1',
      refresh_token: 'refresh-1',
      token_type: 'Bearer',
      expires_in: 3600,
    });
    authMocks.refreshBrokerToken.mockResolvedValue({
      access_token: 'access-refreshed',
      refresh_token: 'refresh-refreshed',
      token_type: 'Bearer',
      expires_in: 3600,
    });
    popup = {
      closed: false,
      location: { replace: vi.fn() },
      close: vi.fn(),
    } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      type: 'opaqueredirect',
      status: 0,
      ok: false,
      url: '',
      json: async () => null,
    }));

    const fetcher = vi.fn<typeof fetch>().mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'https://app.test');
      requests.push({ url: url.href, init });
      if (url.pathname === '/api/v1/authorization/context')
      {
        const tenantSelector = new Headers(init?.headers).get('X-Agentweaver-Tenant');
        const token = new Headers(init?.headers).get('Authorization')?.replace(/^Bearer /, '');
        const runBinding = token === 'run-token';
        return jsonResponse({
          contractVersion: 1,
          issuer: 'projects-config',
          actorId: 'actor-1',
          tenantId: tenantSelector ?? 'tenant-1',
          membershipRevision: 1,
          boundProjectId: runBinding ? 'project-1' : null,
          boundRunId: runBinding ? 'run-1' : null,
          effectiveAuthority: projectAuthority ? [{
            resourceType: 'project',
            resourceId: 'project-1',
            permissions: [{ permission: 'writeProjects', roleRevision: 1 }],
          }] : [],
        });
      }
      if (url.pathname === '/api/v1/policies/platform/revisions') {
        const tenantSelector = new Headers(init?.headers).get('X-Agentweaver-Tenant');
        if (unauthorizedTenantOnce === tenantSelector) {
          unauthorizedTenantOnce = null;
          return jsonResponse({ detail: 'private server detail' }, 401);
        }
        if (delayHistory)
          return new Promise<Response>((resolve) => { resolveHistory = resolve; });
        return policyStatus === 200
          ? jsonResponse(policyHistory())
          : jsonResponse({ detail: 'private server detail' }, policyStatus);
      }
      if (url.pathname === '/api/v1/policies/platform/revisions/2')
        return jsonResponse(policyRevision());
      if (url.pathname === '/api/v1/policies/platform/revisions/1')
        return jsonResponse(olderPolicyRevision());
      if (url.pathname === '/api/v1/policies/platform/validate') {
        const tenantSelector = new Headers(init?.headers).get('X-Agentweaver-Tenant');
        if (unauthorizedValidationTenantOnce === tenantSelector) {
          unauthorizedValidationTenantOnce = null;
          return jsonResponse({ detail: 'private server detail' }, 401);
        }
        if (validationStatus === 202)
          return new Promise<Response>((resolve) => {
            resolvePlatformValidation = resolve;
          });
        return validationStatus === 422
          ? jsonResponse({
              title: 'native_acs_policy_invalid',
              status: 422,
              code: 'native_acs_policy_invalid',
              diagnostics: validationDiagnostics,
            }, 422)
          : validationStatus === 200
            ? jsonResponse({ isValid: true, diagnostics: [] })
            : jsonResponse({ detail: 'private server detail' }, validationStatus);
      }
      if (url.pathname === '/api/v1/policies/projects/project-1/validate') {
        if (abortProjectValidationOnce) {
          abortProjectValidationOnce = false;
          throw new DOMException('The request was cancelled.', 'AbortError');
        }
        if (unauthorizedProjectValidationOnce) {
          unauthorizedProjectValidationOnce = false;
          return jsonResponse({ detail: 'private server detail' }, 401);
        }
        if (resolveProjectValidation === null && projectValidationStatus === 202)
          return new Promise<Response>((resolve) => {
            resolveProjectValidation = resolve;
          });
        return projectValidationStatus === 422
            ? jsonResponse({
                title: 'native_acs_policy_invalid',
                status: 422,
                code: 'native_acs_policy_invalid',
                diagnostics: [],
              }, 422)
            : jsonResponse(
                projectValidationStatus === 200
                  ? { isValid: true, diagnostics: [] }
                  : { detail: 'private server detail' },
                projectValidationStatus,
              );
      }
      return jsonResponse({ items: [], count: 0, nextCursor: null });
    });
    const controlledClient = new AgentweaverGatewayClient('/api/v1', fetcher);
    vi.spyOn(gatewayClient, 'getAuthorizationContext').mockImplementation(
      (token, tenant, signal) =>
        controlledClient.getAuthorizationContext(token, tenant, signal),
    );
    vi.spyOn(gatewayClient, 'getPlatformPolicyRevisionHistory').mockImplementation(
      (token, tenant, options) =>
        controlledClient.getPlatformPolicyRevisionHistory(token, tenant, options),
    );
    vi.spyOn(gatewayClient, 'readPlatformPolicyRevision').mockImplementation(
      (token, revision, tenant, signal) =>
        controlledClient.readPlatformPolicyRevision(token, revision, tenant, signal),
    );
    vi.spyOn(gatewayClient, 'validatePlatformPolicyArtifact').mockImplementation(
      (token, tenant, artifact, signal) =>
        controlledClient.validatePlatformPolicyArtifact(token, tenant, artifact, signal),
    );
    vi.spyOn(gatewayClient, 'validateProjectPolicyArtifact').mockImplementation(
      (token, tenant, projectId, artifact, signal) =>
        controlledClient.validateProjectPolicyArtifact(
          token,
          tenant,
          projectId,
          artifact,
          signal,
        ),
    );
    vi.spyOn(gatewayClient, 'listProjects').mockImplementation(async () =>
      projectAuthority ? [{
        projectId: 'project-1',
        name: 'Project One',
        state: 'active',
        revision: 1,
        configurationRevision: 1,
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        effectiveRoles: [{
          resourceType: 'project',
          resourceId: 'project-1',
          role: 'owner',
        }],
      }] : []);
    vi.spyOn(gatewayClient, 'listNotifications').mockResolvedValue({
      items: [],
      count: 0,
      nextCursor: null,
    });
    markFirstRunTourComplete(firstRunTourStorageKey(JSON.stringify(['projects-config', 'actor-1'])));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('reads the saved snapshot through App and renders source as text', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();

    await screen.findByRole('heading', { name: 'Platform Policies' });
    await screen.findByText('Saved Platform Policy revisions');
    expect(await screen.findByRole('link', { name: 'Platform Policies' })).toBeTruthy();
    expect(screen.getAllByText('2', { exact: true }).length).toBeGreaterThan(0);

    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    expect(await screen.findByText(policyText)).toBeTruthy();
    expect(document.querySelector('img[src="x"]')).toBeNull();
    expect(screen.getByText('name: saved-policy')).toBeTruthy();

    const policyCalls = policyRequests();
    expect(policyCalls.length).toBeGreaterThanOrEqual(2);
    for (const { init } of policyCalls)
      expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('compares the selected older revision without replacing the draft or making a request', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    expect(screen.getByText('Select View on a saved revision to compare.')).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[1]);
    await screen.findAllByText(/saved/);
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'name: draft\n' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'rules/example.yaml' },
    });
    fireEvent.change(screen.getByRole('textbox', {
      name: 'Content for rules/example.yaml',
    }), {
      target: { value: 'draft\t\\n \n' },
    });
    const requestCount = policyRequests().length;

    fireEvent.click(screen.getByRole('button', { name: 'Compare draft' }));
    expect(await screen.findByRole('heading', {
      name: 'Compare draft with revision 1',
    })).toBeTruthy();
    expect(policyRequests()).toHaveLength(requestCount);
    expect((screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('name: draft\n');
    expect((screen.getByRole('textbox', {
      name: 'Content for rules/example.yaml',
    }) as HTMLTextAreaElement).value).toBe('draft\t\\n \n');

    fireEvent.change(screen.getByRole('combobox', { name: 'Changed Policy item' }), {
      target: { value: 'file:rules/example.yaml' },
    });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Show invisible characters' }));
    const saved = screen.getByRole('region', { name: 'Saved comparison source' });
    const draft = screen.getByRole('region', { name: 'Draft comparison source' });
    expect(saved.textContent).toContain('saved\\x20\\t\\\\n\\r\\n');
    expect(draft.textContent).toContain('draft\\t\\\\n\\x20\\n');
    expect(saved.textContent).not.toBe(draft.textContent);

    fireEvent.change(screen.getByRole('combobox', { name: 'Changed Policy item' }), {
      target: { value: 'item:manifest' },
    });
    const savedManifest = screen.getByRole('region', {
      name: 'Saved comparison source',
    });
    const draftManifest = screen.getByRole('region', {
      name: 'Draft comparison source',
    });
    expect(savedManifest.textContent).toContain('\\uFEFFname:');
    expect(draftManifest.textContent).not.toContain('\\uFEFF');

    fireEvent.change(screen.getByRole('textbox', {
      name: 'Content for rules/example.yaml',
    }), {
      target: { value: 'edited after compare' },
    });
    expect(screen.queryByRole('heading', {
      name: 'Compare draft with revision 1',
    })).toBeNull();
    expect(screen.queryByText('draft\\t\\\\n\\x20\\n')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getAllByRole('textbox', { name: 'Relative path' })[1], {
      target: { value: 'rules/example.yaml' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Compare draft' }));
    expect(screen.getByRole('alert').textContent).toContain('must be unique');
    fireEvent.change(screen.getAllByRole('textbox', { name: 'Relative path' })[1], {
      target: { value: 'rules/second.yaml' },
    });
    expect(screen.queryByText(/must be unique/)).toBeNull();
    expect(screen.queryByRole('heading', {
      name: 'Compare draft with revision 1',
    })).toBeNull();
  });

  it('keeps the manifest picker item distinct from a changed file named manifest', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'changed manifest' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'manifest' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Content for manifest' }), {
      target: { value: 'file content' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Compare draft' }));

    const picker = screen.getByRole('combobox', {
      name: 'Changed Policy item',
    }) as HTMLSelectElement;
    expect([...picker.options].map(({ value }) => value)).toEqual([
      'item:manifest',
      'file:manifest',
      'file:rules/example.yaml',
    ]);
    fireEvent.change(picker, { target: { value: 'file:manifest' } });
    expect(screen.getByRole('region', {
      name: 'Draft comparison source',
    }).textContent).toContain('file content');
    fireEvent.change(picker, { target: { value: 'item:manifest' } });
    expect(screen.getByRole('region', {
      name: 'Draft comparison source',
    }).textContent).toContain('changed manifest');
  });

  it('validates an imported Project draft through App without reading a Policy head', async () => {
    projectAuthority = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    const platformCallCount = policyRequests().length;

    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect((projectPicker as HTMLSelectElement).value).toBe('project-1'));
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));

    const manifest = createPlatformPolicyTextBuffer('\uFEFFname: project-policy\r\n');
    const artifact = await buildPlatformPolicyValidationArtifact(manifest, [
      {
        relativePath: 'rules/example.rego',
        content: createPlatformPolicyTextBuffer('allow: true\r'),
      },
    ]);
    const request = `\uFEFF${JSON.stringify({ artifact })}\n`;
    fireEvent.change(screen.getByLabelText('Project Policy validation request'), {
      target: { files: [new File([request], 'project-policy.json', { type: 'application/json' })] },
    });
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe('\uFEFFname: project-policy\n'));

    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    expect(await screen.findByText('The Project Policy draft is valid.')).toBeTruthy();
    expect(policyRequests()).toHaveLength(platformCallCount);
    expect(screen.getByRole('link', { name: 'Platform Policies' })).toBeTruthy();

    const validationCall = requests.filter(({ url }) =>
      url.includes('/policies/projects/project-1/validate')).at(-1);
    expect(validationCall?.url).toBe(
      'https://app.test/api/v1/policies/projects/project-1/validate',
    );
    expect(new Headers(validationCall?.init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(new Headers(validationCall?.init?.headers).get('Authorization')).toBe('Bearer access-1');
    expect(JSON.parse(String(validationCall?.init?.body))).toEqual({ artifact });
    expect(requests.some(({ url }) =>
      url.includes('/policies/projects/project-1') && !url.endsWith('/validate'))).toBe(false);
  });

  it('downloads and reopens an exact Platform draft without changing the viewed revision', async () => {
    await openPlatformPolicyDraft();
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    const expected = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer('\uFEFFname: platform-download\r\n'),
      [
        {
          relativePath: 'rules/mixed.rego',
          content: createPlatformPolicyTextBuffer('allow: true\r'),
        },
        {
          relativePath: 'rules/crlf.rego',
          content: createPlatformPolicyTextBuffer('deny: false\r\n'),
        },
      ],
    );
    const importInput = screen.getByLabelText(
      'Platform Policy validation request',
    ) as HTMLInputElement;
    fireEvent.change(importInput, {
      target: {
        files: [new File([JSON.stringify({ artifact: expected })], 'import.json', {
          type: 'application/json',
        })],
      },
    });
    expect(importInput.value).toBe('');
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe('\uFEFFname: platform-download\n'));
    expect(screen.getByText(policyText)).toBeTruthy();

    let downloaded: Blob | null = null;
    Object.assign(URL, {
      createObjectURL: vi.fn((blob: Blob) => {
        downloaded = blob;
        return 'blob:platform-policy';
      }),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      expect(this.download).toBe('platform-policy-draft.json');
    });
    const requestCount = requests.length;

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));

    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(requests).toHaveLength(requestCount);
    const downloadedBytes = new Uint8Array(await downloaded!.arrayBuffer());
    const imported = await parsePlatformPolicyValidationRequestFile(downloadedBytes);
    expect(await buildPlatformPolicyValidationArtifact(
      imported.manifest,
      imported.files,
    )).toEqual(expected);

    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByText('The Platform Policy draft is valid.');
    fireEvent.click(screen.getByRole('button', { name: 'Compare draft' }));
    expect(await screen.findByRole('heading', {
      name: 'Compare draft with revision 2',
    })).toBeTruthy();

    const invalidArtifact = { ...expected, contentSha256: 'b'.repeat(64) };
    fireEvent.change(importInput, {
      target: {
        files: [new File([JSON.stringify({ artifact: invalidArtifact })], 'invalid.json', {
          type: 'application/json',
        })],
      },
    });
    expect(await screen.findByText(
      'The Policy validation request hash does not match its artifact bytes.',
    )).toBeTruthy();
    expect(screen.getByText('The Platform Policy draft is valid.')).toBeTruthy();
    expect(screen.getByRole('heading', {
      name: 'Compare draft with revision 2',
    })).toBeTruthy();
    expect((screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('\uFEFFname: platform-download\n');

    fireEvent.change(importInput, {
      target: {
        files: [new File([downloaded!], 'platform-policy-draft.json', {
          type: 'application/json',
        })],
      },
    });
    await waitFor(() => expect(
      screen.queryByText('The Platform Policy draft is valid.'),
    ).toBeNull());
    expect(screen.queryByRole('heading', {
      name: 'Compare draft with revision 2',
    })).toBeNull();
    expect(screen.getByText(policyText)).toBeTruthy();
    expect(importInput.value).toBe('');
  });

  it('retains the Platform draft after invalid and oversized local files without a request', async () => {
    await openPlatformPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'name: retained' },
    });
    const requestCount = requests.length;
    const input = screen.getByLabelText('Platform Policy validation request');

    fireEvent.change(input, {
      target: {
        files: [new File(['not-json'], 'invalid.json', { type: 'application/json' })],
      },
    });
    expect(await screen.findByText(
      'The selected Policy validation request is not valid JSON.',
    )).toBeTruthy();

    fireEvent.change(input, {
      target: {
        files: [new File(['x'.repeat(5 * 1024 * 1024 + 1)], 'large.json', {
          type: 'application/json',
        })],
      },
    });
    expect(await screen.findByText(
      'The Policy validation request exceeds 5,242,880 bytes.',
    )).toBeTruthy();
    expect((screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('name: retained');
    expect(requests).toHaveLength(requestCount);
  });

  it('ignores stale Platform local-file work after edits and scope changes', async () => {
    await openPlatformPolicyDraft();
    const artifact = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer('name: delayed-import'),
      [],
    );
    const reading = deferred<ArrayBuffer>();
    const delayedFile = new File([], 'delayed.json', { type: 'application/json' });
    vi.spyOn(delayedFile, 'arrayBuffer').mockReturnValue(reading.promise);

    fireEvent.change(screen.getByLabelText('Platform Policy validation request'), {
      target: { files: [delayedFile] },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'newer edit' },
    });
    await act(async () => {
      reading.resolve(new TextEncoder().encode(
        JSON.stringify({ artifact }),
      ).buffer);
      await reading.promise;
    });
    expect((screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('newer edit');

    const secondReading = deferred<ArrayBuffer>();
    const secondFile = new File([], 'second.json', { type: 'application/json' });
    vi.spyOn(secondFile, 'arrayBuffer').mockReturnValue(secondReading.promise);
    fireEvent.change(screen.getByLabelText('Platform Policy validation request'), {
      target: { files: [secondFile] },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    await screen.findByRole('combobox', { name: 'Project' });
    await act(async () => {
      secondReading.resolve(new TextEncoder().encode(
        JSON.stringify({ artifact }),
      ).buffer);
      await secondReading.promise;
    });
    expect(screen.queryByRole('textbox', {
      name: 'Platform Policy manifest',
    })).toBeNull();
    expect(screen.queryByText('name: delayed-import')).toBeNull();
  });

  it('does not import or report errors after the Platform sign-in session changes', async () => {
    const artifact = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer('name: stale-session-import'),
      [],
    );
    await openPlatformPolicyDraft();
    const hashing = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);

    fireEvent.change(screen.getByLabelText('Platform Policy validation request'), {
      target: {
        files: [new File([JSON.stringify({ artifact })], 'stale-session.json', {
          type: 'application/json',
        })],
      },
    });
    await waitFor(() => expect(globalThis.crypto.subtle.digest).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: 'Account' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await screen.findByRole('button', { name: 'Sign in' });
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(screen.queryByText(
      'The selected Policy validation request hash does not match its artifact bytes.',
    )).toBeNull();
    expect(screen.queryByText('name: stale-session-import')).toBeNull();
    expect(screen.queryByLabelText('Platform Policy validation request')).toBeNull();
  });

  it('lets a newer Platform download cancel an earlier import', async () => {
    const artifact = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer('name: canceled-import'),
      [],
    );
    await openPlatformPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'name: retained-download-source' },
    });
    const reading = deferred<ArrayBuffer>();
    const delayedFile = new File([], 'delayed.json', { type: 'application/json' });
    vi.spyOn(delayedFile, 'arrayBuffer').mockReturnValue(reading.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:platform-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.change(screen.getByLabelText('Platform Policy validation request'), {
      target: { files: [delayedFile] },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    await act(async () => {
      reading.resolve(new TextEncoder().encode(
        JSON.stringify({ artifact }),
      ).buffer);
      await reading.promise;
    });

    expect((screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('name: retained-download-source');
  });

  it('cancels stale Platform downloads after tenant and access changes', async () => {
    await openPlatformPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'name: platform-download' },
    });
    const tenantHash = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValueOnce(tenantHash.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:platform-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    await act(async () => {
      tenantHash.resolve(new ArrayBuffer(32));
      await tenantHash.promise;
    });
    expect(click).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'name: platform-download-two' },
    });
    const accessHash = deferred<ArrayBuffer>();
    vi.mocked(globalThis.crypto.subtle.digest).mockReturnValueOnce(accessHash.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    policyStatus = 403;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText(
      'You do not have permission to access Platform Policies.',
    )).toBeTruthy();
    await act(async () => {
      accessHash.resolve(new ArrayBuffer(32));
      await accessHash.promise;
    });
    expect(click).not.toHaveBeenCalled();
  });

  it('downloads the exact Project draft as an importable compact JSON file without a request', async () => {
    await openProjectPolicyDraft();
    const expected = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer('\uFEFFname: download\r\n'),
      [{
        relativePath: 'rules/download.rego',
        content: createPlatformPolicyTextBuffer('allow: true\r\n'),
      }],
    );
    fireEvent.change(screen.getByLabelText('Project Policy validation request'), {
      target: {
        files: [new File([JSON.stringify({ artifact: expected })], 'import.json', {
          type: 'application/json',
        })],
      },
    });
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe('\uFEFFname: download\n'));
    let downloaded: Blob | null = null;
    const createObjectURL = vi.fn((blob: Blob) => {
      downloaded = blob;
      return 'blob:project-policy';
    });
    const revokeObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      expect(this.download).toBe('project-policy-draft.json');
    });
    const requestCount = requests.length;

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));

    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    const bytes = new Uint8Array(await downloaded!.arrayBuffer());
    const text = new TextDecoder().decode(bytes);
    expect(text).toBe(JSON.stringify({ artifact: expected }));
    expect(bytes.byteLength).toBe(new TextEncoder().encode(text).byteLength);
    expect(bytes.byteLength).toBeLessThanOrEqual(5 * 1024 * 1024);
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:project-policy');
    expect(requests).toHaveLength(requestCount);
    const imported = await parsePlatformPolicyValidationRequestFile(bytes);
    expect(await buildPlatformPolicyValidationArtifact(
      imported.manifest,
      imported.files,
    )).toEqual(expected);

    fireEvent.click(screen.getByRole('button', {
      name: 'Remove rules/download.rego',
    }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'source removed' },
    });
    expect(screen.queryByRole('textbox', {
      name: 'Content for rules/download.rego',
    })).toBeNull();
    fireEvent.change(screen.getByLabelText('Project Policy validation request'), {
      target: {
        files: [new File([downloaded!], 'project-policy-draft.json', {
          type: 'application/json',
        })],
      },
    });
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe('\uFEFFname: download\n'));
    click.mockClear();
    downloaded = null;
    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    const roundtripBytes = new Uint8Array(await downloaded!.arrayBuffer());
    const roundtrip = await parsePlatformPolicyValidationRequestFile(roundtripBytes);
    expect(roundtripBytes).toEqual(bytes);
    expect(await buildPlatformPolicyValidationArtifact(
      roundtrip.manifest,
      roundtrip.files,
    )).toEqual(expected);
  });

  it('downloads a permitted Project draft when the selected tenant differs from the active context', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'platform' },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: tenant-two' },
    });
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));

    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(requests.some(({ url, init }) =>
      url.endsWith('/authorization/context') &&
      new Headers(init?.headers).get('X-Agentweaver-Tenant') === 'tenant-2')).toBe(true);
  });

  it('rejects an empty Project draft download and keeps the draft editor', async () => {
    await openProjectPolicyDraft();
    const createObjectURL = vi.fn(() => 'blob:project-policy');
    Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() });
    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    expect(await screen.findByText(
      'The Policy validation request needs a non-empty manifest.',
    )).toBeTruthy();
    expect(createObjectURL).not.toHaveBeenCalled();
    expect(screen.getByRole('textbox', { name: 'Project Policy manifest' })).toBeTruthy();
  });

  it('rejects an invalid Project draft path before download and keeps the file', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: retained' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: '../unsafe.rego' },
    });
    const createObjectURL = vi.fn(() => 'blob:project-policy');
    Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() });

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));

    expect(await screen.findByText(
      'Each Platform Policy file needs a valid relative path.',
    )).toBeTruthy();
    expect(createObjectURL).not.toHaveBeenCalled();
    expect((screen.getByRole('textbox', {
      name: 'Relative path',
    }) as HTMLInputElement).value).toBe('../unsafe.rego');
  });

  it('keeps the Project draft and revokes the URL when the browser download fails', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: retained' },
    });
    const revokeObjectURL = vi.fn();
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL,
    });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {
      throw new Error('The browser blocked the download.');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));

    expect(await screen.findByText('The browser blocked the download.')).toBeTruthy();
    expect((screen.getByRole('textbox', {
      name: 'Project Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('name: retained');
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:project-policy');
  });

  it('lets only the newest Project draft download click finish hashing', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: current' },
    });
    const first = deferred<ArrayBuffer>();
    const second = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest')
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    await act(async () => {
      first.resolve(new ArrayBuffer(32));
      await first.promise;
    });
    expect(click).not.toHaveBeenCalled();
    await act(async () => {
      second.resolve(new ArrayBuffer(32));
      await second.promise;
    });
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
  });

  it('cancels hashing immediately when a selected import starts reading', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: before-import' },
    });
    const hashing = deferred<ArrayBuffer>();
    const reading = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);
    const delayedFile = new File([], 'delayed.json', { type: 'application/json' });
    vi.spyOn(delayedFile, 'arrayBuffer').mockReturnValue(reading.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    fireEvent.change(screen.getByLabelText('Project Policy validation request'), {
      target: { files: [delayedFile] },
    });
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(click).not.toHaveBeenCalled();
    await act(async () => {
      reading.reject(new Error('stop delayed read'));
      await reading.promise.catch(() => {});
    });
  });

  it('cancels a Project draft download when the draft changes during hashing', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: before-download' },
    });
    const hashing = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: after-download' },
    });
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(click).not.toHaveBeenCalled();
    expect((screen.getByRole('textbox', {
      name: 'Project Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('name: after-download');
  });

  it('cancels a Project draft download when the Project changes during hashing', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: context-fenced' },
    });
    const hashing = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Project' }), {
      target: { value: '' },
    });
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(click).not.toHaveBeenCalled();
  });

  it('cancels a Project draft download when the route changes during hashing', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: route-fenced' },
    });
    const hashing = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    act(() => {
      window.history.pushState({}, '', '/');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(click).not.toHaveBeenCalled();
  });

  it('cancels a Project draft download when the session ends during hashing', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: session-fenced' },
    });
    const hashing = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    fireEvent.click(screen.getByRole('button', { name: 'Account' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await screen.findByRole('button', { name: 'Sign in' });
    click.mockClear();
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(click).not.toHaveBeenCalled();
  });

  it('cancels a Project draft download when access changes during hashing', async () => {
    await openProjectPolicyDraft();
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: access-fenced' },
    });
    const hashing = deferred<ArrayBuffer>();
    vi.spyOn(globalThis.crypto.subtle, 'digest').mockReturnValue(hashing.promise);
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:project-policy'),
      revokeObjectURL: vi.fn(),
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));
    projectAuthority = false;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText(
      'You do not have permission to access this Project Policy.',
    )).toBeTruthy();
    await act(async () => {
      hashing.resolve(new ArrayBuffer(32));
      await hashing.promise;
    });

    expect(click).not.toHaveBeenCalled();
  });

  it('keeps an oversized Project draft in the editor and does not download it', async () => {
    await openProjectPolicyDraft();
    const oversized = 'x'.repeat(262_145);
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: oversized' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'rules/oversized.rego' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Content for rules/oversized.rego' }), {
      target: { value: oversized },
    });
    const createObjectURL = vi.fn(() => 'blob:project-policy');
    Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() });

    fireEvent.click(screen.getByRole('button', { name: 'Download draft' }));

    expect(await screen.findByText(
      'Each Platform Policy file must be 262,144 bytes or smaller.',
    )).toBeTruthy();
    expect(createObjectURL).not.toHaveBeenCalled();
    expect((screen.getByRole('textbox', {
      name: 'Content for rules/oversized.rego',
    }) as HTMLTextAreaElement).value).toBe(oversized);
  });

  it('ignores a Project validation response after the draft is edited', async () => {
    projectAuthority = true;
    projectValidationStatus = 202;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, {
      target: { value: 'project-1' },
    });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(resolveProjectValidation).not.toBeNull());
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'changed after validation' },
    });
    await act(async () => {
      resolveProjectValidation?.(jsonResponse({ isValid: true, diagnostics: [] }));
    });
    expect(screen.queryByText('The Project Policy draft is valid.')).toBeNull();
    expect((screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('changed after validation');
  });

  it('clears an edited Project draft after a late denial for the same activation', async () => {
    projectAuthority = true;
    projectValidationStatus = 202;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'before late denial' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(resolveProjectValidation).not.toBeNull());
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'edited before denial' },
    });
    await act(async () => {
      resolveProjectValidation?.(jsonResponse({ detail: 'private server detail' }, 403));
    });

    expect(await screen.findByText(
      'You do not have permission to access this Project Policy.',
    )).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'Project Policy manifest' })).toBeNull();
    expect(requests.filter(({ url }) =>
      url.includes('/policies/projects/project-1/validate'))).toHaveLength(1);
  });

  it('lets a newer edit win while an imported request is still being read', async () => {
    projectAuthority = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));

    const importedArtifact = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer('name: delayed-import\r\n'),
      [],
    );
    const delayed = deferred<ArrayBuffer>();
    const delayedFile = new File([], 'delayed.json', { type: 'application/json' });
    vi.spyOn(delayedFile, 'arrayBuffer').mockReturnValue(delayed.promise);
    fireEvent.change(screen.getByLabelText('Project Policy validation request'), {
      target: { files: [delayedFile] },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'newer editor draft' },
    });
    await act(async () => {
      delayed.resolve(new TextEncoder().encode(
        JSON.stringify({ artifact: importedArtifact }),
      ).buffer);
      await delayed.promise;
    });
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe('newer editor draft'));
  });

  it('clears a Project draft when the App activates another binding for the same actor and tenant', async () => {
    projectAuthority = true;
    authMocks.exchangeAuthorizationCode.mockImplementation(async (_config, _transaction, code) => ({
      access_token: code === 'binding-code' ? 'run-token' : 'access-1',
      refresh_token: code === 'binding-code' ? 'refresh-run' : 'refresh-1',
      token_type: 'Bearer',
      expires_in: 3600,
    }));
    vi.spyOn(gatewayClient, 'bindChiefOfStaffConversation').mockResolvedValue({
      conversationId: 'conversation-1',
      revision: 1,
    } as never);
    vi.spyOn(gatewayClient, 'startProjectRun').mockResolvedValue({
      projectId: 'project-1',
      runId: 'run-1',
      status: 'starting',
    } as never);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      screen.getByRole('textbox', { name: 'Project Policy manifest' }),
    ).toBeTruthy());
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'unbound private draft' },
    });

    savePendingRun({
      projectId: 'project-1',
      runId: 'run-1',
      createdAt: new Date().toISOString(),
    });
    fireEvent.click(screen.getByRole('button', { name: 'New session' }));
    await waitFor(() => expect(gatewayClient.bindChiefOfStaffConversation).toHaveBeenCalled());
    const silentFrame = await waitFor(() =>
      document.querySelector<HTMLIFrameElement>('iframe[title="Silent sign-in"]'));
    await act(async () => {
      window.dispatchEvent(new MessageEvent('message', {
        origin: 'https://app.test',
        source: silentFrame?.contentWindow,
        data: {
          type: 'agentweaver.identity.callback',
          code: 'binding-code',
          state: 'state-1',
        },
      }));
    });
    await waitFor(() => expect(gatewayClient.startProjectRun).toHaveBeenCalledWith(
      'run-token',
      'project-1',
      'run-1',
      null,
    ));

    act(() => {
      window.history.pushState({}, '', '/policies');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await waitFor(() => expect(
      screen.getByRole('textbox', { name: 'Project Policy manifest' }),
    ).toBeTruthy());
    expect((screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('');
  });

  it('does not let an old Project response clear a current Platform draft', async () => {
    projectAuthority = true;
    projectValidationStatus = 202;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(resolveProjectValidation).not.toBeNull());

    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'platform' },
    });
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'current platform draft' },
    });
    await act(async () => {
      resolveProjectValidation?.(jsonResponse({ detail: 'denied' }, 403));
    });
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('current platform draft');
    expect(screen.getByText('Saved Platform Policy revisions')).toBeTruthy();
  });

  it('keeps the rail gate Platform-specific while Project mode is active', async () => {
    projectAuthority = true;
    policyStatus = 403;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('You do not have permission to access Platform Policies.');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      screen.getByRole('textbox', { name: 'Project Policy manifest' }),
    ).toBeTruthy());
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(policyRequests().length).toBe(1);
  });

  it('clears a Project draft when current access is denied after an edit', async () => {
    projectAuthority = true;
    projectValidationStatus = 403;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'local sensitive draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    expect(await screen.findByText(
      'You do not have permission to access this Project Policy.',
    )).toBeTruthy();

    projectValidationStatus = 200;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe(''));
  });

  it('rechecks Project access and resends one unchanged draft after a 401', async () => {
    projectAuthority = true;
    unauthorizedProjectValidationOnce = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, {
      target: { value: 'project-1' },
    });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'name: retry\r\n' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));

    await waitFor(() => expect(requests.filter(({ url }) =>
      url.includes('/policies/projects/project-1/validate'))).toHaveLength(2));
    expect(await screen.findByText('The Project Policy draft is valid.')).toBeTruthy();
    const calls = requests.filter(({ url }) =>
      url.includes('/policies/projects/project-1/validate'));
    expect(calls).toHaveLength(2);
    expect(calls.map(({ init }) => String(init?.body))).toEqual([
      String(calls[1]?.init?.body),
      String(calls[1]?.init?.body),
    ]);
    expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce();
  });

  it('rechecks Project access without resending after a same-scope abort', async () => {
    projectAuthority = true;
    abortProjectValidationOnce = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));
    fireEvent.change(screen.getByRole('textbox', { name: 'Project Policy manifest' }), {
      target: { value: 'keep aborted draft' },
    });
    const contextChecks = () => requests.filter(({ url }) =>
      url.endsWith('/authorization/context')).length;
    const contextCount = contextChecks();
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));

    await waitFor(() => expect(contextChecks()).toBeGreaterThan(contextCount));
    expect(requests.filter(({ url }) =>
      url.includes('/policies/projects/project-1/validate'))).toHaveLength(1);
    expect((screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('keep aborted draft');
  });

  it('loads a saved revision into a local draft and validates it without writing', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    fireEvent.click(screen.getByRole('button', { name: 'Load viewed revision into draft' }));
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('name: saved-policy\n');
    const fileContent = screen.getByRole('textbox', { name: 'Content for rules/example.yaml' });
    expect((fileContent as HTMLTextAreaElement).value).toBe(policyText);
    fireEvent.change(fileContent, { target: { value: `${policyText}\nallow: false` } });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));

    expect(await screen.findByText('The Platform Policy draft is valid.')).toBeTruthy();
    const validationCall = policyValidationRequests().at(-1);
    expect(validationCall?.url).toBe('https://app.test/api/v1/policies/platform/validate');
    expect(validationCall?.init?.method).toBe('POST');
    expect(new Headers(validationCall?.init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    const body = JSON.parse(String(validationCall?.init?.body));
    expect(body.artifact.manifestBytes).toEqual(bytes('name: saved-policy\n'));
    expect(body.artifact.files).toEqual([{
      relativePath: 'rules/example.yaml',
      bytes: bytes(`${policyText}\nallow: false`),
    }]);
    expect(body.artifact.contentSha256).toMatch(/^[0-9a-f]{64}$/);
    expect(requests.some(({ url, init }) =>
      url.includes('/policies/platform/') && init?.method === 'PUT')).toBe(false);
  });

  it('shows bounded server diagnostics from an invalid validation response', async () => {
    validationStatus = 422;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(policyValidationRequests()).toHaveLength(1));
    const diagnostics = await screen.findByRole('alert', { name: 'Validation diagnostics' });
    expect(diagnostics.textContent)
      .toContain('line 2, column 1: The manifest is invalid.');
    expect(document.body.textContent).not.toContain('private server detail');
  });

  it('focuses the exact current draft file by keyboard without changing bytes or requests', async () => {
    const user = userEvent.setup();
    validationStatus = 422;
    validationDiagnostics = [
      {
        code: 'yaml.invalid',
        message: 'The example file is invalid.',
        relativePath: 'rules/example.yaml',
        line: null,
        column: null,
      },
      {
        code: 'yaml.invalid',
        message: 'The defaults file is invalid.',
        relativePath: 'rules/defaults.yaml',
        line: null,
        column: null,
      },
    ];
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'rules/example.yaml' },
    });
    fireEvent.change(document.querySelector<HTMLTextAreaElement>(
      '[data-platform-policy-draft-file-id]',
    )!, {
      target: { value: 'allow: false\r\n' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    const pathInputs = screen.getAllByRole('textbox', { name: 'Relative path' });
    const fileContents = document.querySelectorAll<HTMLTextAreaElement>(
      '[data-platform-policy-draft-file-id]',
    );
    fireEvent.change(pathInputs[1], {
      target: { value: 'rules/defaults.yaml' },
    });
    fireEvent.change(fileContents[1], {
      target: { value: 'enabled: false\r\n' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByRole('alert', { name: 'Validation diagnostics' });

    const textarea = document.querySelector<HTMLTextAreaElement>(
      '[data-platform-policy-draft-file-id]',
    )!;
    const manifest = screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement;
    const beforeManifest = manifest.value;
    const beforeContent = textarea.value;
    const requestCount = requests.length;
    const action = screen.getByRole('button', {
      name: 'Go to draft file: rules/example.yaml',
    });
    expect(screen.getByRole('button', {
      name: 'Go to draft file: rules/defaults.yaml',
    })).toBeTruthy();
    action.focus();

    await user.keyboard('{Enter}');

    expect(document.activeElement).toBe(textarea);
    expect(document.activeElement).toBeInstanceOf(HTMLTextAreaElement);
    expect(textarea.value).toBe(beforeContent);
    expect(manifest.value).toBe(beforeManifest);
    expect(requests).toHaveLength(requestCount);

    const firstBody = JSON.parse(String(policyValidationRequests().at(-1)?.init?.body));
    const validationRequestCount = policyValidationRequests().length;
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(policyValidationRequests()).toHaveLength(
      validationRequestCount + 1,
    ));
    const secondBody = JSON.parse(String(policyValidationRequests().at(-1)?.init?.body));
    expect(secondBody.artifact.files[0].bytes)
      .toEqual(firstBody.artifact.files[0].bytes);
    expect(secondBody.artifact.files[1].bytes)
      .toEqual(firstBody.artifact.files[1].bytes);
  });

  it('keeps unmatched and pathless diagnostics as text', async () => {
    validationStatus = 422;
    validationDiagnostics = [
      {
        code: 'path.missing',
        message: 'No draft file has this path.',
        relativePath: 'rules/missing.yaml',
        line: null,
        column: null,
      },
      {
        code: 'manifest.invalid',
        message: 'The manifest has no file path.',
        relativePath: null,
        line: null,
        column: null,
      },
    ];
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    const diagnostics = await screen.findByRole('alert', {
      name: 'Validation diagnostics',
    });

    expect(diagnostics.textContent).toContain('rules/missing.yaml');
    expect(diagnostics.textContent).toContain('The manifest has no file path.');
    expect(screen.queryByRole('button', { name: /Go to draft file/ })).toBeNull();
  });

  it('does not resolve an ambiguous diagnostic path', () => {
    const content = createPlatformPolicyTextBuffer('');
    const match = findPlatformPolicyDiagnosticDraftFile('rules/duplicate.yaml', [
      { id: 1, relativePath: 'rules/duplicate.yaml', content },
      { id: 2, relativePath: 'rules/duplicate.yaml', content },
    ]);

    expect(match).toBeNull();
  });

  it('clears a matching diagnostic action after edits and access loss', async () => {
    validationStatus = 422;
    validationDiagnostics = [{
      code: 'yaml.invalid',
      message: 'The file is invalid.',
      relativePath: 'rules/example.yaml',
      line: null,
      column: null,
    }];
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'rules/example.yaml' },
    });
    const content = document.querySelector<HTMLTextAreaElement>(
      '[data-platform-policy-draft-file-id]',
    )!;
    fireEvent.change(content, { target: { value: 'invalid' } });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByRole('button', {
      name: 'Go to draft file: rules/example.yaml',
    });

    fireEvent.change(content, { target: { value: 'edited' } });
    expect(screen.queryByRole('alert', { name: 'Validation diagnostics' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Go to draft file/ })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByRole('button', {
      name: 'Go to draft file: rules/example.yaml',
    });
    policyStatus = 403;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await screen.findByText('You do not have permission to access Platform Policies.');
    expect(screen.queryByRole('alert', { name: 'Validation diagnostics' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Go to draft file/ })).toBeNull();
  });

  it('refreshes policy access after a validation 401 and requires validation again', async () => {
    unauthorizedValidationTenantOnce = 'tenant-1';
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'unsaved policy draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    expect(await screen.findByText(
      'Platform Policy access expired. Check access, then validate again.',
    )).toBeTruthy();
    await screen.findByText('Saved Platform Policy revisions');
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('unsaved policy draft');
    expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    const canceledMessage =
      'Platform Policy access changed. Validate the draft again when access is ready.';
    await waitFor(() => expect(
      screen.queryByText('The Platform Policy draft is valid.') ||
        screen.queryByText(canceledMessage),
    ).toBeTruthy());
    if (screen.queryByText(canceledMessage)) {
      await screen.findByText('Saved Platform Policy revisions');
      fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    }
    expect(await screen.findByText('The Platform Policy draft is valid.')).toBeTruthy();

    const calls = policyValidationRequests();
    expect(calls.length).toBeGreaterThanOrEqual(2);
    expect(new Headers(calls[0]?.init?.headers).get('Authorization')).toBe('Bearer access-1');
    expect(new Headers(calls.at(-1)?.init?.headers).get('Authorization')).toBe('Bearer access-refreshed');
  });

  it('preserves a local draft through token refresh for a selected tenant', async () => {
    authMocks.exchangeAuthorizationCode.mockResolvedValue({
      access_token: 'access-1',
      refresh_token: 'refresh-1',
      token_type: 'Bearer',
      expires_in: 31,
    });
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    await waitFor(() => expect(policyRequests().some(({ init }) =>
      new Headers(init?.headers).get('X-Agentweaver-Tenant') === 'tenant-2')).toBe(true));
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'unsaved before token refresh' },
    });
    const historyCount = policyRequests().length;
    delayHistory = true;

    await waitFor(() => expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce(), {
      timeout: 5_000,
    });
    await waitFor(() => expect(policyRequests().length).toBeGreaterThan(historyCount), {
      timeout: 5_000,
    });
    await waitFor(() => expect(resolveHistory).not.toBeNull(), { timeout: 5_000 });
    await waitFor(() => expect(requests.some(({ url, init }) =>
      url.endsWith('/authorization/context') &&
      new Headers(init?.headers).get('Authorization') === 'Bearer access-refreshed')).toBe(true), {
      timeout: 5_000,
    });
    expect(policyRequests().slice(historyCount).some(({ init }) =>
      new Headers(init?.headers).get('X-Agentweaver-Tenant') === 'tenant-2')).toBe(true);
    await act(async () => {
      resolveHistory?.(jsonResponse(policyHistory()));
    });
    expect((await screen.findByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('unsaved before token refresh');
  });

  it('clears saved data and navigation when validation is denied', async () => {
    validationStatus = 403;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'tenant-1 private draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Content for rules/policy-1.yaml' }), {
      target: { value: 'tenant-1 private file' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    expect((await screen.findByText('You do not have permission to access Platform Policies.')).textContent)
      .toBe('You do not have permission to access Platform Policies.');
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(policyValidationRequests().length).toBe(1);
    expect(requests.some(({ url, init }) =>
      url.includes('/policies/platform/') && init?.method === 'PUT')).toBe(false);

    validationStatus = 200;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('');
    expect(screen.queryByRole('textbox', { name: 'Content for rules/policy-1.yaml' })).toBeNull();
  });

  it('hides the route data and navigation when the policy history probe denies access', async () => {
    policyStatus = 403;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();

    expect((await screen.findByRole('alert')).textContent)
      .toBe('You do not have permission to access Platform Policies.');
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(document.body.textContent).not.toContain('private server detail');
    expect(policyRequests().length).toBeGreaterThan(0);
    for (const { init } of policyRequests())
      expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('rejects a history page that does not contain its declared snapshot head', async () => {
    omitSnapshotHead = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();

    expect((await screen.findByRole('alert')).textContent)
      .toBe('Platform Policies could not load. Select Retry.');
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
  });

  it('clears the snapshot and navigation as soon as the tenant changes', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    expect(await screen.findByRole('link', { name: 'Platform Policies' })).toBeTruthy();

    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'tenant-1 private draft' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });

    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect((screen.getByRole('textbox', { name: 'Tenant ID' }) as HTMLInputElement).value)
      .toBe('tenant-2');

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('');
  });

  it('keeps the selected tenant for timer and visible-tab checks', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(
      new Headers(policyRequests().at(-1)?.init?.headers).get('X-Agentweaver-Tenant'),
    ).toBe('tenant-2'));
    await screen.findByText('Saved Platform Policy revisions');

    const timerRequestCount = policyRequests().length;
    act(() => intervalHandlers.at(-1)?.());
    await waitFor(() => expect(policyRequests().length).toBeGreaterThan(timerRequestCount));
    expect(new Headers(policyRequests().at(-1)?.init?.headers).get('X-Agentweaver-Tenant'))
      .toBe('tenant-2');

    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    visibility.mockReturnValue('hidden');
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    visibility.mockReturnValue('visible');
    const visibleRequestCount = policyRequests().length;
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    await waitFor(() => expect(policyRequests().length).toBeGreaterThan(visibleRequestCount));
    expect(new Headers(policyRequests().at(-1)?.init?.headers).get('X-Agentweaver-Tenant'))
      .toBe('tenant-2');
  });

  it('restores focus when timer revalidation revokes access', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByRole('link', { name: 'Platform Policies' });
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    expect(await screen.findByText(policyText)).toBeTruthy();
    const policyLink = screen.getByRole('link', { name: 'Platform Policies' });
    policyLink.focus();
    expect(document.activeElement).toBe(policyLink);

    policyStatus = 403;
    act(() => intervalHandlers.at(-1)?.());
    expect((await screen.findByRole('alert')).textContent)
      .toBe('You do not have permission to access Platform Policies.');
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(screen.queryByText(policyText)).toBeNull();
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    const retry = screen.getByRole('button', { name: 'Retry' });
    expect((retry as HTMLButtonElement).disabled).toBe(false);
    expect([retry, screen.getByRole('main', { name: 'Main content' })])
      .toContain(document.activeElement);
  });

  it('moves focus from a removed diagnostic action during a background access recheck', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    validationStatus = 422;
    validationDiagnostics = [{
      code: 'yaml.invalid',
      message: 'The file is invalid.',
      relativePath: 'rules/example.yaml',
      line: null,
      column: null,
    }];
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'rules/example.yaml' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    const action = await screen.findByRole('button', {
      name: 'Go to draft file: rules/example.yaml',
    });
    action.focus();
    expect(document.activeElement).toBe(action);

    policyStatus = 403;
    act(() => intervalHandlers.at(-1)?.());

    await screen.findByText('You do not have permission to access Platform Policies.');
    expect(screen.queryByRole('button', { name: /Go to draft file/ })).toBeNull();
    expect([
      screen.getByRole('button', { name: 'Retry' }),
      screen.getByRole('main', { name: 'Main content' }),
    ]).toContain(document.activeElement);
  });

  it('does not steal focus from a contextual Policies link during a background recheck', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    const contextualLink = document.createElement('a');
    contextualLink.href = '/policies';
    contextualLink.textContent = 'Open Policies';
    document.body.append(contextualLink);
    contextualLink.focus();

    policyStatus = 403;
    act(() => intervalHandlers.at(-1)?.());

    await screen.findByText('You do not have permission to access Platform Policies.');
    await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 0)); });
    expect(document.activeElement).toBe(contextualLink);
    contextualLink.remove();
  });

  it.each(['timer', 'visibility'] as const)(
    'hides comparison during %s recheck and restores the pinned revision after a head advance',
    async (trigger) => {
      const intervalHandlers: (() => void)[] = [];
      const setInterval = window.setInterval.bind(window);
      vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
        if (delay === 60_000 && typeof handler === 'function')
          intervalHandlers.push(handler as () => void);
        return setInterval(handler, delay);
      }) as typeof window.setInterval);
      render(<App />);
      fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
      await completeSignIn();
      await screen.findByText('Saved Platform Policy revisions');
      fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
      await screen.findByText(policyText);
      fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
        target: { value: 'local comparison draft' },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Compare draft' }));
      const picker = screen.getByRole('combobox', { name: 'Changed Policy item' });
      picker.focus();
      const revisionReads = () => requests.filter(({ url }) =>
        url.endsWith('/policies/platform/revisions/2')).length;
      const readCount = revisionReads();
      delayHistory = true;

      if (trigger === 'timer') {
        act(() => intervalHandlers.at(-1)?.());
      } else {
        const visibility = vi.spyOn(document, 'visibilityState', 'get');
        visibility.mockReturnValue('hidden');
        act(() => document.dispatchEvent(new Event('visibilitychange')));
        visibility.mockReturnValue('visible');
        act(() => document.dispatchEvent(new Event('visibilitychange')));
      }

      await waitFor(() => expect(resolveHistory).not.toBeNull());
      expect(screen.queryByText(policyText)).toBeNull();
      expect(screen.queryByRole('heading', {
        name: 'Compare draft with revision 2',
      })).toBeNull();
      await waitFor(() => expect([
        screen.getByRole('heading', { name: 'Platform Policies' }),
        screen.getByRole('main', { name: 'Main content' }),
      ]).toContain(document.activeElement));

      await act(async () => {
        resolveHistory?.(jsonResponse({
          ...policyHistory(),
          snapshotRevision: 3,
          items: [
            { revision: 3, contentSha256: 'b'.repeat(64), createdAt: '2026-01-04T03:04:05Z' },
            ...policyHistory().items,
          ],
        }));
      });
      await waitFor(() => expect(revisionReads()).toBeGreaterThan(readCount));
      expect(await screen.findByText(policyText)).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Compare draft' })).toBeTruthy();
      expect(screen.queryByRole('heading', {
        name: 'Compare draft with revision 2',
      })).toBeNull();
      expect((screen.getByRole('textbox', {
        name: 'Platform Policy manifest',
      }) as HTMLTextAreaElement).value).toBe('local comparison draft');
    },
  );

  it('does not restore a delayed revision after leaving and returning to the route', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    const revisionReads = () => requests.filter(({ url }) =>
      url.endsWith('/policies/platform/revisions/2')).length;
    const readCount = revisionReads();
    delayHistory = true;
    act(() => intervalHandlers.at(-1)?.());
    await waitFor(() => expect(resolveHistory).not.toBeNull());

    act(() => {
      window.history.pushState({}, '', '/overview');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await act(async () => {
      resolveHistory?.(jsonResponse(policyHistory()));
    });
    expect(screen.queryByText(policyText)).toBeNull();
    expect(revisionReads()).toBe(readCount);

    delayHistory = false;
    act(() => {
      window.history.pushState({}, '', '/policies');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    await screen.findByText('Saved Platform Policy revisions');
    expect(screen.getByText('Select View on a saved revision to compare.')).toBeTruthy();
    expect(screen.queryByText(policyText)).toBeNull();
    expect(revisionReads()).toBe(readCount);
  });

  it('keeps comparison hidden after timer access denial and moves focus to a safe target', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    const compare = screen.getByRole('button', { name: 'Compare draft' });
    compare.focus();
    await userEvent.setup().keyboard('{Enter}');
    expect(screen.getByRole('combobox', { name: 'Changed Policy item' })).toBeTruthy();
    expect(document.activeElement).toBe(compare);

    policyStatus = 403;
    act(() => intervalHandlers.at(-1)?.());
    expect(await screen.findByText(
      'You do not have permission to access Platform Policies.',
    )).toBeTruthy();
    expect(screen.queryByText(policyText)).toBeNull();
    expect(screen.queryByRole('heading', {
      name: 'Compare draft with revision 2',
    })).toBeNull();
    expect([
      screen.getByRole('button', { name: 'Retry' }),
      screen.getByRole('main', { name: 'Main content' }),
    ]).toContain(document.activeElement);
  });

  it('clears Platform source and comparison after a delivered 403 despite a draft edit', async () => {
    validationStatus = 202;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'before validation' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(resolvePlatformValidation).not.toBeNull());
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'edited after validation' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Compare draft' }));
    expect(screen.getByRole('combobox', { name: 'Changed Policy item' })).toBeTruthy();

    await act(async () => {
      resolvePlatformValidation?.(jsonResponse({ detail: 'private server detail' }, 403));
    });
    expect(await screen.findByText(
      'You do not have permission to access Platform Policies.',
    )).toBeTruthy();
    expect(screen.queryByText(policyText)).toBeNull();
    expect(screen.queryByRole('heading', {
      name: 'Compare draft with revision 2',
    })).toBeNull();
    expect(screen.queryByRole('textbox', { name: 'Platform Policy manifest' })).toBeNull();
  });

  it('ignores an old Platform validation denial after the tenant changes', async () => {
    validationStatus = 202;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'old tenant draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(resolvePlatformValidation).not.toBeNull());
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'new tenant draft' },
    });

    await act(async () => {
      resolvePlatformValidation?.(jsonResponse({ detail: 'private server detail' }, 403));
    });
    expect(screen.queryByText(
      'You do not have permission to access Platform Policies.',
    )).toBeNull();
    expect((screen.getByRole('textbox', {
      name: 'Platform Policy manifest',
    }) as HTMLTextAreaElement).value).toBe('new tenant draft');
  });

  it.each([
    ['tenant change first', 'success'],
    ['tenant change first', 'failure'],
    ['refresh first', 'success'],
    ['refresh first', 'failure'],
  ] as const)(
    'keeps the active run session when %s meets a delayed refresh %s',
    async (changeOrder, outcome) => {
      const refresh = deferred<{
        access_token: string;
        refresh_token: string;
        token_type: string;
        expires_in: number;
      }>();
      authMocks.exchangeAuthorizationCode.mockImplementation(async (_config, _transaction, code) => ({
        access_token: code === 'run-code' ? 'run-token' : 'policy-token',
        refresh_token: code === 'run-code' ? 'refresh-run' : 'refresh-policy',
        token_type: 'Bearer',
        expires_in: 3600,
      }));
      authMocks.refreshBrokerToken.mockReturnValue(refresh.promise);
      unauthorizedTenantOnce = 'tenant-1';
      await startRunBoundPolicySession();
      await waitFor(() => expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce());

      const finishRefresh = async () => {
        await act(async () => {
          if (outcome === 'success') {
            refresh.resolve({
              access_token: 'policy-token-refreshed',
              refresh_token: 'refresh-policy-refreshed',
              token_type: 'Bearer',
              expires_in: 3600,
            });
          } else {
            refresh.reject(new Error('refresh denied'));
          }
          await refresh.promise.catch(() => undefined);
        });
      };
      const selectTenantTwo = () => {
        fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
          target: { value: 'tenant-2' },
        });
      };

      let changeBoundary: number;
      if (changeOrder === 'tenant change first') {
        changeBoundary = requests.length;
        selectTenantTwo();
        await finishRefresh();
      } else {
        await finishRefresh();
        if (outcome === 'success')
          await screen.findByText('policy-authorized:true');
        else
          await screen.findByText('policy-status:unavailable');
        changeBoundary = requests.length;
        selectTenantTwo();
        fireEvent.click(screen.getByRole('button', { name: 'Retry policy' }));
      }
      if (outcome === 'failure') {
        await waitFor(() => expect(
          document.querySelector('iframe[title="Platform Policy sign-in"]'),
        ).not.toBeNull());
        await completePolicySignIn();
      }
      await screen.findByText('policy-authorized:true');
      expect(screen.getByText('session:run-token')).toBeTruthy();

      const requestsAfterTenantChange = requests.slice(changeBoundary);
      const policyHistory = requestsAfterTenantChange.filter(({ url }) =>
        url.includes('/policies/platform/revisions'));
      expect(policyHistory.length).toBeGreaterThan(0);
      for (const { init } of policyHistory)
        expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-2');
      for (const { url, init } of requestsAfterTenantChange) {
        if (url.endsWith('/authorization/context'))
          expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-2');
      }
      expect(unauthorizedTenantOnce).toBeNull();
    },
  );

  it('re-resolves and reprobes the same tenant after a Policy 401 refresh', async () => {
    authMocks.exchangeAuthorizationCode.mockImplementation(async (_config, _transaction, code) => ({
      access_token: code === 'run-code' ? 'run-token' : 'policy-token',
      refresh_token: code === 'run-code' ? 'refresh-run' : 'refresh-policy',
      token_type: 'Bearer',
      expires_in: 3600,
    }));
    authMocks.refreshBrokerToken.mockResolvedValue({
      access_token: 'policy-token-refreshed',
      refresh_token: 'refresh-policy-refreshed',
      token_type: 'Bearer',
      expires_in: 3600,
    });
    unauthorizedTenantOnce = 'tenant-1';
    await startRunBoundPolicySession();
    await screen.findByText('policy-authorized:true');

    expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce();
    expect(screen.getByText('session:run-token')).toBeTruthy();
    const refreshedContext = requests.find(({ url, init }) =>
      url.endsWith('/authorization/context') &&
      new Headers(init?.headers).get('Authorization') === 'Bearer policy-token-refreshed');
    expect(refreshedContext).toBeTruthy();
    expect(new Headers(refreshedContext?.init?.headers).get('X-Agentweaver-Tenant'))
      .toBe('tenant-1');
    const policyCalls = policyRequests();
    expect(policyCalls.length).toBeGreaterThanOrEqual(2);
    for (const { init } of policyCalls)
      expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('does not render a successful response after sign-out fences the request', async () => {
    delayHistory = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await waitFor(() => expect(resolveHistory).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Account' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await screen.findByRole('button', { name: 'Sign in' });

    await act(async () => {
      resolveHistory?.(jsonResponse(policyHistory()));
    });
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
  });

  it('shows the Project Skill trust form only after Project validation and preserves exact bytes on update', async () => {
    projectAuthority = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    // Platform scope never shows the Project Skill trust form even after validation passes.
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    const platformPathInput = screen.getByRole('textbox', { name: 'Relative path' });
    fireEvent.change(platformPathInput, { target: { value: 'policy/skills/data.json' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Content for policy/skills/data.json' }), {
      target: { value: '{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByText('The Platform Policy draft is valid.');
    expect(screen.queryByRole('heading', { name: 'Project Skill trust score' })).toBeNull();

    // Switch to Project scope and import a draft with BOM and mixed line endings.
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));

    const manifestText = 'name: project-skills\r\n';
    const skillsRaw =
      '\uFEFF{\r\n' +
      '  "schemaVersion": 1,\n' +
      '  "minimumScore": 500,\r' +
      '  "requireCompletedEvidence": true\r\n' +
      '}\n';
    const actionsRaw = 'package agentweaver.actions\r\ndefault allow := false\r\n';
    const importedArtifact = await buildPlatformPolicyValidationArtifact(
      createPlatformPolicyTextBuffer(manifestText),
      [
        { relativePath: 'policy/actions.rego', content: createPlatformPolicyTextBuffer(actionsRaw) },
        { relativePath: 'policy/skills/data.json', content: createPlatformPolicyTextBuffer(skillsRaw) },
      ],
    );
    const importFile = new File(
      [JSON.stringify({ artifact: importedArtifact })],
      'project-skills.json',
      { type: 'application/json' },
    );
    fireEvent.change(screen.getByLabelText('Project Policy validation request'), {
      target: { files: [importFile] },
    });
    await waitFor(() => expect(
      (screen.getByRole('textbox', { name: 'Project Policy manifest' }) as HTMLTextAreaElement).value,
    ).toBe('name: project-skills\n'));

    // Before validation, the form is not shown.
    expect(screen.queryByRole('heading', { name: 'Project Skill trust score' })).toBeNull();

    // Validate the Project draft.
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByText('The Project Policy draft is valid.');
    expect(await screen.findByRole('heading', { name: 'Project Skill trust score' })).toBeTruthy();

    // Rejects invalid bounds without sending any request or clearing validation.
    const requestsBeforeInvalid = requests.length;
    const scoreInput = screen.getByRole('textbox', { name: 'New minimum trust score' });
    for (const invalidValue of ['500', '499', '1001', '-0', '600.0', '6e2']) {
      fireEvent.change(scoreInput, { target: { value: invalidValue } });
      fireEvent.click(screen.getByRole('button', { name: 'Update draft' }));
      expect(screen.getByRole('alert').textContent).toContain('greater than 500');
      expect(screen.getByText('The Project Policy draft is valid.')).toBeTruthy();
      expect(requests).toHaveLength(requestsBeforeInvalid);
    }

    // Valid update replaces only the score token, sends no request, clears validation, and focuses Validate draft.
    fireEvent.change(scoreInput, { target: { value: '650' } });
    fireEvent.click(screen.getByRole('button', { name: 'Update draft' }));

    expect(requests).toHaveLength(requestsBeforeInvalid);
    expect(screen.queryByText('The Project Policy draft is valid.')).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Project Skill trust score' })).toBeNull();
    const validateButton = screen.getByRole('button', { name: 'Validate draft' });
    expect(document.activeElement).toBe(validateButton);

    // Re-validate and verify the exact serialized bytes sent to the Gateway.
    fireEvent.click(validateButton);
    await screen.findByText('The Project Policy draft is valid.');
    const validateRequests = requests.filter(({ url }) =>
      url.endsWith('/policies/projects/project-1/validate'));
    expect(validateRequests).toHaveLength(2);
    const secondBody = JSON.parse(String(validateRequests[1]?.init?.body ?? '{}')) as {
      artifact?: {
        manifestBytes?: number[];
        files?: { relativePath: string; bytes: number[] }[];
      };
    };
    expect(secondBody.artifact?.manifestBytes).toEqual(bytes(manifestText));
    const sentActions = secondBody.artifact?.files?.find((f) => f.relativePath === 'policy/actions.rego');
    const sentSkills = secondBody.artifact?.files?.find((f) => f.relativePath === 'policy/skills/data.json');
    expect(sentActions?.bytes).toEqual(bytes(actionsRaw));
    expect(sentSkills?.bytes).toEqual(bytes(skillsRaw.replace('500', '650')));
  });

  it('keeps unsupported or ambiguous skills JSON in the raw editor and recovers focus on access loss', async () => {
    projectAuthority = true;
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);

    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('combobox', { name: 'Policy scope' }), {
      target: { value: 'project' },
    });
    const projectPicker = await screen.findByRole('combobox', { name: 'Project' });
    await screen.findByRole('option', { name: /Project One/ });
    fireEvent.change(projectPicker, { target: { value: 'project-1' } });
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Validate draft' }) as HTMLButtonElement).disabled,
    ).toBe(false));

    // Duplicate key JSON stays in raw editor after validation passes.
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Relative path' }), {
      target: { value: 'policy/skills/data.json' },
    });
    const skillsEditor = screen.getByRole('textbox', { name: 'Content for policy/skills/data.json' });
    fireEvent.change(skillsEditor, {
      target: { value: '{"schemaVersion":1,"minimumScore":500,"minimumScore":600,"requireCompletedEvidence":true}' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByText('The Project Policy draft is valid.');
    expect(screen.queryByRole('heading', { name: 'Project Skill trust score' })).toBeNull();

    // Fix to supported JSON and re-validate; form appears.
    fireEvent.change(skillsEditor, {
      target: { value: '{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}' },
    });
    expect(screen.queryByText('The Project Policy draft is valid.')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await screen.findByRole('heading', { name: 'Project Skill trust score' });

    // Focus inside the form; timer access loss hides the form and moves focus to a safe control.
    const scoreInput = screen.getByRole('textbox', { name: 'New minimum trust score' });
    scoreInput.focus();
    expect(document.activeElement).toBe(scoreInput);
    projectAuthority = false;
    act(() => intervalHandlers.at(-1)?.());
    expect(await screen.findByText(
      'You do not have permission to access this Project Policy.',
    )).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Project Skill trust score' })).toBeNull();
    expect([
      screen.getByRole('button', { name: 'Retry' }),
      screen.getByRole('main', { name: 'Main content' }),
    ]).toContain(document.activeElement);
  });
});
