import { useState } from 'react';
import { FluentProvider, webLightTheme, Text, tokens } from '@fluentui/react-components';
import type { RunResponse } from './api/client';
import { SubmitRunPage } from './pages/SubmitRunPage';
import { RunWatchPage } from './pages/RunWatchPage';
import { ReviewPage } from './pages/ReviewPage';

type Page =
  | { name: 'submit' }
  | { name: 'watch'; runId: string }
  | { name: 'review'; run: RunResponse };

/**
 * T059: App root with state-based navigation.
 * Submit -> Watch -> Review flow.
 * No emojis (NFR-002).
 */
function App() {
  const [page, setPage] = useState<Page>({ name: 'submit' });

  const handleRunCreated = (run: RunResponse) => {
    setPage({ name: 'watch', runId: run.id });
  };

  const handleReview = (run: RunResponse) => {
    setPage({ name: 'review', run });
  };

  const handleReviewComplete = () => {
    setPage({ name: 'submit' });
  };

  return (
    <FluentProvider theme={webLightTheme}>
      <div
        style={{
          minHeight: '100vh',
          backgroundColor: tokens.colorNeutralBackground1,
        }}
      >
        {/* Header */}
        <header
          style={{
            padding: '12px 24px',
            borderBottom: `1px solid ${tokens.colorNeutralStroke1}`,
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
          }}
        >
          <Text
            size={600}
            weight="semibold"
            onClick={() => setPage({ name: 'submit' })}
            style={{ cursor: 'pointer' }}
          >
            Scaffolder
          </Text>
        </header>

        {/* Page content */}
        <main>
          {page.name === 'submit' && (
            <SubmitRunPage onRunCreated={handleRunCreated} />
          )}
          {page.name === 'watch' && (
            <RunWatchPage runId={page.runId} onReview={handleReview} />
          )}
          {page.name === 'review' && (
            <ReviewPage run={page.run} onComplete={handleReviewComplete} />
          )}
        </main>
      </div>
    </FluentProvider>
  );
}

export default App;

