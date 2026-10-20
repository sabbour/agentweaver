/**
 * Message composer shared by the run chat today and by "start a new chat" once
 * the service can create runs (gap G1). It owns the draft text and one
 * idempotency key per draft: a retry of the same text reuses the key, so a
 * timed-out send cannot post twice. The key changes after a successful send or
 * when the text changes. Callers supply any pickers (sessions now, a project
 * picker later) through `children`.
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
  children?: ReactNode;
}

/** Text box and send button with retry-safe idempotency. */
export function NewChatComposer({
  onSubmit, blocked = false, label = 'Message', placeholder = 'Write a message', submitLabel = 'Send', children,
}: NewChatComposerProps) {
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const key = useRef<string | null>(null);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const value = text.trim();
    if (!value || busy || blocked) return;
    key.current ??= crypto.randomUUID();
    setBusy(true);
    setError(null);
    try {
      await onSubmit(value, key.current);
      key.current = null;
      setText('');
    } catch (reason) {
      setError(reason);
    } finally {
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
