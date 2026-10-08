/**
 * Framework-neutral lazy mounter for the landing scenario theater.
 *
 * The heavy React player is only imported and mounted once its `host` element is
 * near the viewport, keeping the docs bundle light and avoiding work off-screen.
 *
 * Race-safety contract (reviewed): a single `disposed` flag and a single `started`
 * flag guard every async edge. The IntersectionObserver disconnects on the first
 * intersection, the dynamic import is re-checked against `disposed`/host-connection
 * after it resolves, and `unmount` is invoked at most once. Calling the returned
 * dispose function before the import resolves must never mount into a detached host
 * and must never throw.
 */
export interface LazyMounterOptions {
  /** Element the player mounts into and whose visibility gates the load. */
  host: HTMLElement;
  /** Resolves to the mount function (e.g. `mountLandingWorkflowDemo`). */
  load: () => Promise<(el: HTMLElement) => () => void>;
  /** How far outside the viewport to begin loading. Defaults to 200px. */
  rootMargin?: string;
}

/**
 * Observe `host`; when it approaches the viewport, run `load()` and mount the
 * returned component. Returns a dispose function that tears everything down
 * exactly once and is safe to call at any point in the lifecycle.
 */
export function createLazyMounter(options: LazyMounterOptions): () => void {
  const { host, load, rootMargin = '200px' } = options;

  let disposed = false;
  let started = false;
  let unmount: (() => void) | undefined;

  const start = () => {
    if (disposed || started) return;
    started = true;
    void load()
      .then((mount) => {
        // The import is async: bail if we were disposed while it was in flight,
        // or if the host was detached from the document in the meantime.
        if (disposed || !host.isConnected) return;
        unmount = mount(host);
      })
      .catch(() => {
        // Swallow import/mount failures — the no-JS fallback already covers this
        // surface, and a rejected dynamic import must not crash the docs page.
      });
  };

  let observer: IntersectionObserver | undefined;

  if (typeof IntersectionObserver === 'undefined') {
    // No observer available (older engines / SSR hand-off): load immediately so
    // the surface is never left blank.
    start();
  } else {
    observer = new IntersectionObserver(
      (entries) => {
        if (disposed || started) return;
        if (entries.some((entry) => entry.isIntersecting)) {
          observer?.disconnect();
          start();
        }
      },
      { rootMargin },
    );
    observer.observe(host);
  }

  return () => {
    if (disposed) return;
    disposed = true;
    observer?.disconnect();
    unmount?.();
    unmount = undefined;
  };
}
