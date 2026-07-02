import { useEffect, useRef, useState } from 'react';
import {
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { SendRegular, StopRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';

// Maps a successful steer response status to a compact confirmation line.
function steerStatusMessage(status: string): string {
  if (status === 'applied') return 'Applied — re-running the affected work with your guidance.';
  if (status === 'queued') return 'Queued — applies at the next step.';
  return 'Steering message sent.';
}

interface ChatMessage {
  id: number;
  role: 'user' | 'system';
  text: string;
  intent?: 'success' | 'error';
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    height: '100%',
    minHeight: 0,
  },
  history: {
    flex: 1,
    minHeight: '160px',
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  empty: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    margin: 'auto',
    textAlign: 'center',
  },
  bubbleUser: {
    alignSelf: 'flex-end',
    maxWidth: '85%',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    fontSize: tokens.fontSizeBase300,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  bubbleSystem: {
    alignSelf: 'flex-start',
    maxWidth: '85%',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  bubbleError: {
    border: `1px solid ${tokens.colorPaletteRedBorder2}`,
    color: tokens.colorPaletteRedForeground1,
  },
  composer: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    flexShrink: 0,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  spacer: { flex: 1 },
  scopeNote: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
});

export interface SteerChatPanelProps {
  runId: string;
  /** When false the composer is disabled (e.g. the orchestration is terminal). */
  canSteer?: boolean;
  /** Called after a successful steer so the page can reconnect the stream. */
  onSteered?: () => void;
}

/**
 * Chat-style coordinator steering panel. A scrollable message history of the course-corrections the
 * operator has sent plus the coordinator's acknowledgement, a text input, and a send button. Send
 * routes through the same steering API (kind: 'send') used everywhere else; Stop halts the run.
 */
export function SteerChatPanel({ runId, canSteer = true, onSteered }: SteerChatPanelProps) {
  const styles = useStyles();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const historyRef = useRef<HTMLDivElement>(null);
  const nextId = useRef(1);

  useEffect(() => {
    const el = historyRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages]);

  const append = (msg: Omit<ChatMessage, 'id'>) =>
    setMessages((prev) => [...prev, { id: nextId.current++, ...msg }]);

  const errText = (err: unknown): string =>
    err instanceof ApiError
      ? `Steer failed (${err.status}): ${err.body}`
      : err instanceof Error ? err.message : String(err);

  const send = async () => {
    const instruction = text.trim();
    if (!instruction || busy || !canSteer) return;
    append({ role: 'user', text: instruction });
    setText('');
    setBusy(true);
    try {
      const res = await apiClient.steerCoordinator(runId, { kind: 'send', instruction });
      append({ role: 'system', text: steerStatusMessage(res.status), intent: 'success' });
      onSteered?.();
    } catch (err) {
      append({ role: 'system', text: errText(err), intent: 'error' });
    } finally {
      setBusy(false);
    }
  };

  const stop = async () => {
    if (busy || !canSteer) return;
    setBusy(true);
    try {
      await apiClient.steerCoordinator(runId, { kind: 'stop' });
      append({ role: 'system', text: 'Stop requested — no further work will be dispatched.', intent: 'success' });
      onSteered?.();
    } catch (err) {
      append({ role: 'system', text: errText(err), intent: 'error' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.root} data-testid="steer-chat-panel">
      <Text className={styles.scopeNote}>
        Send a course-correction to the coordinator. It applies at the next step of the affected
        subtasks. Applies to all active subtasks.
      </Text>

      <div className={styles.history} ref={historyRef} aria-label="Steering message history">
        {messages.length === 0 ? (
          <Text className={styles.empty}>No steering messages yet.</Text>
        ) : (
          messages.map((m) =>
            m.role === 'user' ? (
              <div key={m.id} className={styles.bubbleUser}>{m.text}</div>
            ) : (
              <div
                key={m.id}
                className={`${styles.bubbleSystem}${m.intent === 'error' ? ` ${styles.bubbleError}` : ''}`}
              >
                {m.text}
              </div>
            ),
          )
        )}
      </div>

      {!canSteer && (
        <MessageBar intent="info">
          <MessageBarBody>This orchestration is no longer active and cannot be steered.</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.composer}>
        <Textarea
          value={text}
          onChange={(_, v) => setText(v.value)}
          placeholder="Message the coordinator with a course-correction…"
          disabled={busy || !canSteer}
          rows={3}
          resize="vertical"
          data-testid="steer-chat-input"
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey && text.trim()) {
              e.preventDefault();
              void send();
            }
          }}
        />
        <div className={styles.actions}>
          <Button
            appearance="primary"
            size="small"
            icon={<SendRegular />}
            disabled={busy || !canSteer || !text.trim()}
            onClick={() => void send()}
            data-testid="steer-chat-send"
          >
            Send
          </Button>
          <div className={styles.spacer} />
          <Button
            appearance="subtle"
            size="small"
            icon={<StopRegular />}
            disabled={busy || !canSteer}
            onClick={() => void stop()}
            data-testid="steer-chat-stop"
          >
            Stop
          </Button>
          {busy && <Spinner size="extra-tiny" aria-label="Steering" />}
        </div>
      </div>
    </div>
  );
}
