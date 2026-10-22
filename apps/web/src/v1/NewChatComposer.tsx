/**
 * Message composer shared by the run chat and by the Chief of Staff new chat.
 * It owns the draft text and one
 * idempotency key per draft: a retry of the same text reuses the key, so a
 * timed-out send cannot post twice. The key changes after a successful send or
 * when the text changes. Callers supply any pickers (sessions now, a project
 * picker for a new chat) through `children`.
 */
import { Button, Field, Textarea } from '@fluentui/react-components';
import { SendRegular } from '@fluentui/react-icons';
import { useRef, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { ErrorNotice } from './pageParts';
import { errorCode, errorMessage } from './errors';

export interface NewChatComposerProps {
  /** Sends the trimmed text. Throw to keep the draft and show the error. */
  onSubmit: (text: string, idempotencyKey: string) => Promise<void>;
  /** Extra reasons the send button must stay off, such as a missing recipient. */
  blocked?: boolean;
  label?: string;
  placeholder?: string;
  submitLabel?: string;
  /**
   * Identifies what the draft is sent to, such as the picked project. A different
   * scope gets a new idempotency key; the text stays.
   */
  draftScope?: string;
  children?: ReactNode;
}

/** Text box and send button with retry-safe idempotency. */
export function NewChatComposer({
  onSubmit, blocked = false, label = 'Message', placeholder = 'Write a message', submitLabel = 'Send', draftScope = '', children,
}: NewChatComposerProps) {
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  // Idempotency key for the current draft. null means "make a new one on the next send".
  const key = useRef<string | null>(null);
  // The draft scope the current key was made for.
  const keyScope = useRef(draftScope);
  // State updates are async; this ref blocks a second submit fired before the re-render.
  const sending = useRef(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const value = text.trim();
    if (!value || sending.current || blocked) return;
    // The target changed (for example another project was picked). The old key
    // belongs to a different request, so drop it and make a new one.
    if (keyScope.current !== draftScope) {
      key.current = null;
      keyScope.current = draftScope;
    }
    // Reuse the key on a retry of the same draft; make one on the first send.
    key.current ??= crypto.randomUUID();
    sending.current = true;
    setBusy(true);
    setError(null);
    try {
      await onSubmit(value, key.current);
      // Sent: clear the draft and its key.
      key.current = null;
      setText('');
    } catch (reason) {
      // Keep the text and the key, so pressing Send again retries the same request.
      setError(reason);
    } finally {
      sending.current = false;
      setBusy(false);
    }
  };

  return (
    <form className="v1-form v1-composer" onSubmit={(event) => void submit(event)}>
      {children}
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      <Field label={label}>
        <Textarea
          value={text}
          placeholder={placeholder}
          rows={3}
          resize="vertical"
          onChange={(_, data) => { setText(data.value); key.current = null; }}
          onKeyDown={(event) => {
            // Ctrl+Enter (Cmd+Enter on Mac) sends. requestSubmit runs the normal form submit path.
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
              event.preventDefault();
              event.currentTarget.form?.requestSubmit();
            }
          }}
        />
      </Field>
      <div className="v1-actions">
        <Button appearance="primary" type="submit" icon={<SendRegular />} disabled={busy || blocked || !text.trim()}>
          {busy ? 'Sending…' : submitLabel}
        </Button>
      </div>
    </form>
  );
}
