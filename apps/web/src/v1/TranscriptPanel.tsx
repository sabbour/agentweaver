import { Badge, Button, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { Panel } from './pageParts';
import type { TranscriptTurn, TurnTranscript } from './turnTranscript';

/** Text shown in place of a message that is not loaded. */
const STATE_TEXT: Record<Exclude<TranscriptTurn['state'], 'loaded'>, string> = {
  loading: 'Loading…',
  notFound: 'Not found',
  unavailable: 'Unavailable',
  failed: 'Could not load this message.',
};

/** Message shown after a 403 on a turn read. No message text stays on the page. */
export const TRANSCRIPT_ACCESS_LOST = 'You can no longer read this run. Messages are hidden.';

/**
 * Renders a run's user and assistant turns in journal order. Text comes only from the
 * owner turn read; turns that can't be read show their state, never stored copies.
 */
export function TranscriptPanel({ transcript }: { transcript: TurnTranscript }) {
  const { turns, accessLost, retry } = transcript;
  // Offer Retry when access was lost or at least one turn failed to load.
  // Not-found and unavailable turns are final states, so they do not need it.
  const failed = turns.some((turn) => turn.state === 'failed');
  return (
    <Panel
      title="Messages"
      description="User and assistant messages in this run, oldest first."
      actions={accessLost || failed ? <Button appearance="secondary" onClick={retry}>Retry</Button> : undefined}
    >
      {accessLost ? (
        <MessageBar intent="warning"><MessageBarBody>{TRANSCRIPT_ACCESS_LOST}</MessageBarBody></MessageBar>
      ) : turns.length === 0 ? (
        <p className="v1-muted">No messages yet.</p>
      ) : (
        <ol className="v1-chat-list" aria-label="Messages">
          {turns.map((turn) => (
            <li key={turn.eventId} aria-label={turn.role === 'user' ? 'User message' : 'Assistant message'}>
              <div>
                <Badge appearance="outline">{turn.role === 'user' ? 'User' : 'Assistant'}</Badge>
                <time dateTime={turn.occurredAt}>{new Date(turn.occurredAt).toLocaleString()}</time>
              </div>
              {turn.state === 'loaded'
                ? <p style={{ whiteSpace: 'pre-wrap' }}>{turn.text}</p>
                : <p className="v1-muted">{STATE_TEXT[turn.state]}</p>}
            </li>
          ))}
        </ol>
      )}
    </Panel>
  );
}
