import { Component, StrictMode, type ErrorInfo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import './standalone.css';
import { AzureFluentShowcaseApp } from './AzureFluentShowcaseApp';
class ShowcaseErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Azure Fluent showcase render error', error, info);
  }

  render() {
    if (this.state.error) {
      return (
        <main style={{ minHeight: '100vh', display: 'grid', placeItems: 'center', padding: 24 }}>
          <section style={{ maxWidth: 520 }}>
            <h1>Azure Fluent showcase failed to load</h1>
            <p>Reload the page to try the standalone demo again.</p>
            <button type="button" onClick={() => window.location.reload()}>Reload</button>
          </section>
        </main>
      );
    }
    return this.props.children;
  }
}

const rootElement = document.getElementById('root');

if (!rootElement) {
  throw new Error('Azure Fluent showcase root element was not found.');
}

createRoot(rootElement).render(
  <StrictMode>
    <ShowcaseErrorBoundary>
      <AzureFluentShowcaseApp />
    </ShowcaseErrorBoundary>
  </StrictMode>,
);
