import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// happy-dom has no layout engine, so `<body>` reports a 0x0 rectangle. Tabster
// (used by Fluent UI dialogs) reads that as "the page is hidden" and treats
// every control as invisible. A Fluent Dialog then finds nothing to focus and
// focuses its own surface instead of its first control. Tabster only marks a
// dialog as the active modal when focus lands inside it, so about 250 ms later
// it hides the open dialog with `aria-hidden`, and role queries for its
// controls fail on slow machines. A real browser gives `<body>` a size, so we
// report the viewport size here to match browser behavior.
const bodyRect = HTMLBodyElement.prototype.getBoundingClientRect;
HTMLBodyElement.prototype.getBoundingClientRect = function getBodyRect(this: HTMLBodyElement) {
  const rect = bodyRect.call(this);
  // Keep any real size happy-dom may report; only fill in the empty case.
  if (rect.width !== 0 || rect.height !== 0) {
    return rect;
  }
  return new DOMRect(0, 0, window.innerWidth || 1024, window.innerHeight || 768);
};

// `vitest.config.ts` sets `globals: false`, so `@testing-library/react`'s
// automatic `afterEach(cleanup)` registration (which relies on detecting a
// global test-framework `afterEach`) never fires. Without it, React trees
// from one test file can be left mounted (or partially torn down) when the
// next file's tests start reusing the same happy-dom `document`.
//
// This matters beyond just leaked DOM nodes: Fluent UI's Dialog uses Tabster
// to mark background content `aria-hidden` while a modal is open, and Tabster
// does not always clear that attribute when a Dialog unmounts ahead of a full
// teardown (see https://github.com/microsoft/fluentui/issues/35139). If a
// stray `aria-hidden` root from an earlier test's dialog survives into the
// next file's render, an unrelated dialog opened later can inherit/interact
// with that stale Tabster bookkeeping and end up `aria-hidden` itself,
// making its contents invisible to role-based queries (and, in a real app,
// to screen readers).
//
// Explicitly cleaning up after every test -- and fully clearing the document
// body -- keeps each test's DOM (and Tabster's view of it) isolated.
afterEach(() => {
  cleanup();
  document.body.innerHTML = '';
});
