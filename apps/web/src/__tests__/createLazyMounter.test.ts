import { afterEach, describe, expect, it, vi } from 'vitest';
import { createLazyMounter } from '../components/landing/createLazyMounter';

/**
 * Race-safety tests for the framework-neutral lazy mounter used by
 * WorkflowProof.vue. Covers the IO-absent immediate path, observer gating,
 * mount-once/dispose-once, and the critical pending-import teardown where the
 * host is disposed before the dynamic import resolves.
 */

const realIO = (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver;

afterEach(() => {
  (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = realIO;
  vi.restoreAllMocks();
});

function connectedHost(): HTMLElement {
  const el = document.createElement('div');
  document.body.appendChild(el);
  return el;
}

describe('createLazyMounter', () => {
  it('loads and mounts immediately when IntersectionObserver is unavailable', async () => {
    (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = undefined;
    const host = connectedHost();
    let mountedInto: HTMLElement | undefined;
    const unmount = vi.fn();

    const dispose = createLazyMounter({
      host,
      load: async () => (el: HTMLElement) => {
        mountedInto = el;
        return unmount;
      },
    });

    await Promise.resolve();
    await Promise.resolve();

    expect(mountedInto).toBe(host);
    dispose();
    dispose(); // idempotent
    expect(unmount).toHaveBeenCalledTimes(1);
  });

  it('does not mount when disposed before the import resolves', async () => {
    (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = undefined;
    const host = connectedHost();
    let resolveLoad: (m: (el: HTMLElement) => () => void) => void = () => {};
    const mount = vi.fn(() => vi.fn());

    const dispose = createLazyMounter({
      host,
      load: () =>
        new Promise<(el: HTMLElement) => () => void>((resolve) => {
          resolveLoad = resolve;
        }),
    });

    // Tear down while the import is still pending.
    dispose();
    resolveLoad(mount as unknown as (el: HTMLElement) => () => void);
    await Promise.resolve();
    await Promise.resolve();

    expect(mount).not.toHaveBeenCalled();
  });

  it('does not mount into a detached host', async () => {
    (globalThis as unknown as { IntersectionObserver?: unknown }).IntersectionObserver = undefined;
    const host = document.createElement('div'); // never attached → isConnected false
    const mount = vi.fn(() => vi.fn());

    createLazyMounter({ host, load: async () => mount as unknown as (el: HTMLElement) => () => void });
    await Promise.resolve();
    await Promise.resolve();

    expect(mount).not.toHaveBeenCalled();
  });

  it('waits for intersection, then loads once and disconnects the observer', async () => {
    const instances: MockIO[] = [];
    class MockIO {
      cb: IntersectionObserverCallback;
      disconnect = vi.fn();
      observe = vi.fn();
      unobserve = vi.fn();
      constructor(cb: IntersectionObserverCallback) {
        this.cb = cb;
        instances.push(this);
      }
      fire(isIntersecting: boolean) {
        this.cb([{ isIntersecting } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
      }
    }
    (globalThis as unknown as { IntersectionObserver: unknown }).IntersectionObserver = MockIO;

    const host = connectedHost();
    const mount = vi.fn(() => vi.fn());
    createLazyMounter({ host, load: async () => mount as unknown as (el: HTMLElement) => () => void });

    expect(instances).toHaveLength(1);
    expect(mount).not.toHaveBeenCalled(); // not loaded until in view

    instances[0].fire(true);
    await Promise.resolve();
    await Promise.resolve();

    expect(instances[0].disconnect).toHaveBeenCalled();
    expect(mount).toHaveBeenCalledTimes(1);
  });
});
