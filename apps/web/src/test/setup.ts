import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// Keep AuthContext iframe tests offline while giving their callback frame a loaded page.
window.happyDOM.settings.fetch.interceptor = {
  beforeAsyncRequest: async ({ request }) =>
    new URL(request.url).hostname === 'broker.test'
      ? new window.Response('', { status: 200 })
      : undefined,
};

function createMemoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() { return values.size; },
    clear: () => values.clear(),
    getItem: (key) => values.get(key) ?? null,
    key: (index) => [...values.keys()][index] ?? null,
    removeItem: (key) => values.delete(key),
    setItem: (key, value) => values.set(key, String(value)),
  };
}

// Keep browser storage in memory so tests do not persist state to disk.
for (const name of ['localStorage', 'sessionStorage'] as const) {
  const storage = createMemoryStorage();
  Object.defineProperty(window, name, { configurable: true, value: storage });
  Object.defineProperty(globalThis, name, { configurable: true, value: storage });
}

// happy-dom 21 exposes Web Animations but does not render them. Let Fluent UI use its test no-op.
Object.defineProperty(Element.prototype, 'animate', { configurable: true, value: undefined });

// happy-dom has no layout engine, so an empty body reports 0x0. Give dialog
// focus checks viewport-sized bounds so they behave like they do in a browser.
const bodyRect = HTMLBodyElement.prototype.getBoundingClientRect;
HTMLBodyElement.prototype.getBoundingClientRect = function getBodyRect(this: HTMLBodyElement) {
  const rect = bodyRect.call(this);
  // Keep any real size happy-dom may report; only fill in the empty case.
  if (rect.width !== 0 || rect.height !== 0) {
    return rect;
  }
  return new DOMRect(0, 0, window.innerWidth || 1024, window.innerHeight || 768);
};

// `vitest.config.ts` disables globals, so Testing Library does not register
// automatic cleanup. Unmount each render and clear the shared body after a test
// so DOM state does not leak into the next test.
afterEach(() => {
  cleanup();
  document.body.innerHTML = '';
});
