import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CopilotPopupCallbackPage, CopilotUserConnectionPanel } from './CopilotUserConnectionPanel';
import type { CopilotConnectionReceipt } from './contracts';

const mocks = vi.hoisted(() => ({
  apiCall: vi.fn(),
  getAuthorizationContext: vi.fn(),
  beginCopilotConnection: vi.fn(),
  completeCopilotConnection: vi.fn(),
  getCopilotConnection: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({
    session: { accessToken: 'broker-token' },
    apiCall: mocks.apiCall,
  }),
}));

vi.mock('./api', () => ({
  gatewayClient: {
    getAuthorizationContext: mocks.getAuthorizationContext,
    beginCopilotConnection: mocks.beginCopilotConnection,
    completeCopilotConnection: mocks.completeCopilotConnection,
    getCopilotConnection: mocks.getCopilotConnection,
  },
  GatewayError: class GatewayError extends Error {
    status = 0;
    code?: string;
  },
}));

const callbackUri = 'https://app.example.test/auth/github/copilot-app/callback';
const receipt = {
  connectionId: '12c8f32e-3340-4820-a712-d10bda31ab8a',
  revision: 1,
  scope: 'project' as const,
  scopeId: 'project-1',
  state: 'pending' as const,
  freshUntil: '2026-10-08T12:00:00Z',
};

function deferred<T>() {
  let resolvePromise: (value: T) => void = () => {
    throw new Error('The deferred promise was not initialized.');
  };
  const promise = new Promise<T>((resolve) => {
    resolvePromise = resolve;
  });
  return { promise, resolve: resolvePromise };
}

function createPopup() {
  const popup = {
    closed: false,
    location: { replace: vi.fn() },
    close: vi.fn(),
  };
  popup.close.mockImplementation(() => { popup.closed = true; });
  return popup;
}

describe('Copilot user connection panel', () => {
  beforeEach(async () => {
    const happyDOM = Reflect.get(window, 'happyDOM') as {
      setURL(url: string): void | Promise<void>;
    };
    await happyDOM.setURL('https://app.example.test/projects/project-1/settings');
    mocks.apiCall.mockReset().mockImplementation(
      (operation: (token: string, tenantSelector: string) => Promise<unknown>) =>
        operation('broker-token', 'tenant-1'),
    );
    mocks.getAuthorizationContext.mockReset().mockResolvedValue({ actorId: 'actor-1' });
    mocks.beginCopilotConnection.mockReset().mockResolvedValue({
      connection: receipt,
      authorizationUri: `https://github.com/login/oauth/authorize?redirect_uri=${encodeURIComponent(callbackUri)}&state=request-1`,
    });
    mocks.completeCopilotConnection.mockReset().mockResolvedValue({
      ...receipt,
      state: 'connected',
      revision: 2,
    });
    mocks.getCopilotConnection.mockReset().mockResolvedValue({
      ...receipt,
      state: 'connected',
      revision: 2,
    });
  });

  it('completes only a matching popup callback and displays the owner status', async () => {
    const popup = createPopup();
    const open = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    try {
      render(<CopilotUserConnectionPanel projectId="project-1" />);
      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Copilot' }));

      await waitFor(() => expect(popup.location.replace).toHaveBeenCalledWith(
        expect.stringContaining('https://github.com/login/oauth/authorize'),
      ));
      expect(open).toHaveBeenCalledWith(
        'about:blank',
        'agentweaver-copilot-user',
        'popup,width=560,height=720',
      );

      const callback = {
        type: 'agentweaver.copilot-user.callback',
        state: 'request-1',
        code: 'one-time-code',
      };
      act(() => {
        window.dispatchEvent(new MessageEvent('message', {
          data: callback,
          origin: 'https://untrusted.example.test',
          source: popup as unknown as MessageEventSource,
        }));
      });
      expect(mocks.completeCopilotConnection).not.toHaveBeenCalled();

      act(() => {
        window.dispatchEvent(new MessageEvent('message', {
          data: callback,
          origin: window.location.origin,
          source: popup as unknown as MessageEventSource,
        }));
      });

      await screen.findByText('Owner status:', { exact: false });
      await waitFor(() => expect(mocks.completeCopilotConnection).toHaveBeenCalledWith(
        'broker-token',
        callback,
        'tenant-1',
      ));
      expect(mocks.getCopilotConnection).toHaveBeenCalledWith(
        'broker-token',
        receipt.connectionId,
        'tenant-1',
      );
      expect(await screen.findByText('Connected', { selector: 'strong' })).toBeTruthy();
      expect(popup.close).toHaveBeenCalledOnce();
    } finally {
      open.mockRestore();
    }
  });

  it('discards and closes a pending Begin when the selected project changes', async () => {
    const pendingBegin = deferred<{
      connection: typeof receipt;
      authorizationUri: string;
    }>();
    mocks.beginCopilotConnection.mockReturnValueOnce(pendingBegin.promise);
    const popup = createPopup();
    const open = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    try {
      const { rerender } = render(<CopilotUserConnectionPanel projectId="project-1" />);
      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Copilot' }));
      await waitFor(() => expect(mocks.beginCopilotConnection).toHaveBeenCalledOnce());

      rerender(<CopilotUserConnectionPanel projectId="project-2" />);
      expect(popup.close).toHaveBeenCalledOnce();

      await act(async () => {
        pendingBegin.resolve({
          connection: receipt,
          authorizationUri: `https://github.com/login/oauth/authorize?redirect_uri=${encodeURIComponent(callbackUri)}&state=request-1`,
        });
        await pendingBegin.promise;
      });

      expect(popup.location.replace).not.toHaveBeenCalled();
      expect(screen.queryByText(/Connection ID:/)).toBeNull();
      expect(mocks.completeCopilotConnection).not.toHaveBeenCalled();
    } finally {
      open.mockRestore();
    }
  });

  it('discards a project status response after switching projects', async () => {
    const popup = createPopup();
    const open = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    const staleStatus = deferred<CopilotConnectionReceipt>();
    try {
      const { rerender } = render(<CopilotUserConnectionPanel projectId="project-1" />);
      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Copilot' }));
      await waitFor(() => expect(popup.location.replace).toHaveBeenCalledOnce());
      act(() => {
        window.dispatchEvent(new MessageEvent('message', {
          data: {
            type: 'agentweaver.copilot-user.callback',
            state: 'request-1',
            code: 'one-time-code',
          },
          origin: window.location.origin,
          source: popup as unknown as MessageEventSource,
        }));
      });
      await screen.findByText('Connected', { selector: 'strong' });

      mocks.getCopilotConnection.mockReturnValueOnce(staleStatus.promise);
      fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
      await waitFor(() => expect(mocks.getCopilotConnection).toHaveBeenCalledTimes(2));

      rerender(<CopilotUserConnectionPanel projectId="project-2" />);
      await waitFor(() => expect(screen.queryByText(/Owner status:/)).toBeNull());
      await act(async () => {
        staleStatus.resolve({ ...receipt, state: 'revoked', revision: 3 });
        await staleStatus.promise;
      });

      expect(screen.queryByText(/Owner status:/)).toBeNull();
      expect(screen.queryByText(/Connection ID:/)).toBeNull();
    } finally {
      open.mockRestore();
    }
  });

  it('fails and closes the owned pending popup when authorization navigation throws', async () => {
    const popup = createPopup();
    popup.location.replace.mockImplementation(() => {
      throw new Error('popup navigation failed');
    });
    const open = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    try {
      render(<CopilotUserConnectionPanel projectId="project-1" />);
      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Copilot' }));

      const alert = await screen.findByRole('alert');
      expect(alert.textContent).toContain('popup navigation failed');
      expect(popup.close).toHaveBeenCalledOnce();
      expect(screen.queryByText('Waiting for GitHub authorization…')).toBeNull();
      expect((screen.getByRole('button', { name: 'Connect GitHub Copilot' }) as HTMLButtonElement).disabled)
        .toBe(false);
    } finally {
      open.mockRestore();
    }
  });

  it('keeps status refresh available after reconnecting while an old refresh is pending', async () => {
    const firstPopup = createPopup();
    const secondPopup = createPopup();
    const open = vi.spyOn(window, 'open')
      .mockReturnValueOnce(firstPopup as unknown as Window)
      .mockReturnValueOnce(secondPopup as unknown as Window);
    const staleStatus = deferred<CopilotConnectionReceipt>();
    try {
      render(<CopilotUserConnectionPanel projectId="project-1" />);
      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Copilot' }));
      await waitFor(() => expect(firstPopup.location.replace).toHaveBeenCalledOnce());
      act(() => {
        window.dispatchEvent(new MessageEvent('message', {
          data: {
            type: 'agentweaver.copilot-user.callback',
            state: 'request-1',
            code: 'one-time-code',
          },
          origin: window.location.origin,
          source: firstPopup as unknown as MessageEventSource,
        }));
      });
      await screen.findByText('Connected', { selector: 'strong' });

      mocks.getCopilotConnection.mockReturnValueOnce(staleStatus.promise);
      fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
      await waitFor(() => expect(mocks.getCopilotConnection).toHaveBeenCalledTimes(2));
      expect((screen.getByRole('button', { name: 'Refreshing…' }) as HTMLButtonElement).disabled)
        .toBe(true);

      fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Copilot' }));
      await waitFor(() => expect(secondPopup.location.replace).toHaveBeenCalledOnce());
      act(() => {
        window.dispatchEvent(new MessageEvent('message', {
          data: {
            type: 'agentweaver.copilot-user.callback',
            state: 'request-1',
            code: 'one-time-code',
          },
          origin: window.location.origin,
          source: secondPopup as unknown as MessageEventSource,
        }));
      });
      await screen.findByText('Connected', { selector: 'strong' });
      await waitFor(() => expect(
        (screen.getByRole('button', { name: 'Refresh status' }) as HTMLButtonElement).disabled,
      ).toBe(false));

      await act(async () => {
        staleStatus.resolve({ ...receipt, state: 'revoked', revision: 3 });
        await staleStatus.promise;
      });
      expect(screen.getByText('Connected', { selector: 'strong' })).toBeTruthy();
      expect((screen.getByRole('button', { name: 'Refresh status' }) as HTMLButtonElement).disabled)
        .toBe(false);
    } finally {
      open.mockRestore();
    }
  });

  it('relays only the ephemeral callback message to its opener', () => {
    const previousOpener = Object.getOwnPropertyDescriptor(window, 'opener');
    const opener = { postMessage: vi.fn() } as unknown as Window;
    Object.defineProperty(window, 'opener', { configurable: true, value: opener });
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});
    const previousCallback = window.__AGENTWEAVER_COPILOT_CALLBACK__;
    window.__AGENTWEAVER_COPILOT_CALLBACK__ = {
      type: 'agentweaver.copilot-user.callback',
      state: 'request-1',
      code: 'one-time-code',
    };
    try {
      render(<CopilotPopupCallbackPage />);
      expect(opener.postMessage).toHaveBeenCalledWith(
        {
          type: 'agentweaver.copilot-user.callback',
          state: 'request-1',
          code: 'one-time-code',
        },
        window.location.origin,
      );
      expect(window.__AGENTWEAVER_COPILOT_CALLBACK__).toBeUndefined();
      expect(close).toHaveBeenCalledOnce();
    } finally {
      close.mockRestore();
      window.__AGENTWEAVER_COPILOT_CALLBACK__ = previousCallback;
      if (previousOpener)
        Object.defineProperty(window, 'opener', previousOpener);
      else
        Reflect.deleteProperty(window, 'opener');
    }
  });
});
