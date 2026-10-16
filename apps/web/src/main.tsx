/** Browser bootstrap for the v1 app, including development focus support and a top-level render fallback. */
import './index.css';
import App from './App.tsx';
import { Component, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import type { ErrorInfo, ReactNode } from 'react';
if (import.meta.env.DEV) {
  // Load the live-preview focus guard only in development so it stays out of production startup.
  const focusGuardModule = '/src/dev/impeccableLiveFocusGuard.ts';
  void import(/* @vite-ignore */ focusGuardModule).then((module: typeof import('./dev/impeccableLiveFocusGuard')) => {
    const { installImpeccableLiveFocusGuard } = module;
    const cleanup = installImpeccableLiveFocusGuard();
    import.meta.hot?.dispose(cleanup);
  });
}

/** Replaces a failed React tree with a reload prompt while keeping render errors inside the app. */
class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Uncaught render error', error, info);
  }

  render() {
    if (this.state.error) {
      return (
        <main style={{ minHeight: '100vh', display: 'grid', placeItems: 'center', padding: 24 }}>
          <section style={{ maxWidth: 520 }}>
            <h1>Something went wrong</h1>
            <p>Agentweaver hit an unexpected UI error. Reload the page to try again.</p>
            <button type="button" onClick={() => window.location.reload()}>Reload</button>
          </section>
        </main>
      );
    }
    return this.props.children;
  }
}

// Mount the application in strict mode and keep uncaught component errors at the UI boundary.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
)
