import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { NewChatComposer } from './NewChatComposer';

function textbox() {
  return screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement;
}

function type(value: string) {
  fireEvent.change(textbox(), { target: { value } });
}

/** Clicks Send and waits for the async submit to settle. */
async function send() {
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Send' })); });
}

describe('NewChatComposer', () => {
  it('disables Send for empty or whitespace-only text', () => {
    render(<NewChatComposer onSubmit={vi.fn()} />);
    const button = screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    type('   ');
    expect(button.disabled).toBe(true);
    type('hi');
    expect(button.disabled).toBe(false);
  });

  it('sends trimmed text and clears the draft after success', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<NewChatComposer onSubmit={onSubmit} />);
    type('  hello  ');
    await send();
    expect(onSubmit).toHaveBeenCalledWith('hello', expect.any(String));
    await waitFor(() => expect(textbox().value).toBe(''));
  });

  it('reuses the idempotency key when a failed send is retried with the same text', async () => {
    const onSubmit = vi.fn()
      .mockRejectedValueOnce(new Error('Timed out'))
      .mockResolvedValueOnce(undefined);
    render(<NewChatComposer onSubmit={onSubmit} />);
    type('hello');
    await send();
    expect(await screen.findByText('Timed out')).toBeTruthy();
    // The draft is kept after a failure so the user can retry.
    expect(textbox().value).toBe('hello');
    await send();
    expect(onSubmit).toHaveBeenCalledTimes(2);
    // Same key on retry, so the server can drop a duplicate if the first send did arrive.
    expect(onSubmit.mock.calls[1][1]).toBe(onSubmit.mock.calls[0][1]);
    await waitFor(() => expect(screen.queryByText('Timed out')).toBeNull());
  });

  it('uses a new key after a successful send', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<NewChatComposer onSubmit={onSubmit} />);
    type('first');
    await send();
    type('first');
    await send();
    // Same text sent twice on purpose is two messages, so the keys must differ.
    expect(onSubmit).toHaveBeenCalledTimes(2);
    expect(onSubmit.mock.calls[1][1]).not.toBe(onSubmit.mock.calls[0][1]);
  });

  it('uses a new key when the text changes after a failure', async () => {
    const onSubmit = vi.fn().mockRejectedValueOnce(new Error('Timed out')).mockResolvedValueOnce(undefined);
    render(<NewChatComposer onSubmit={onSubmit} />);
    type('hello');
    await send();
    await screen.findByText('Timed out');
    type('hello again');
    await send();
    expect(onSubmit.mock.calls[1][0]).toBe('hello again');
    expect(onSubmit.mock.calls[1][1]).not.toBe(onSubmit.mock.calls[0][1]);
  });

  it('submits on Ctrl+Enter and Cmd+Enter but not on plain Enter', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<NewChatComposer onSubmit={onSubmit} />);
    type('hello');
    await act(async () => { fireEvent.keyDown(textbox(), { key: 'Enter' }); });
    expect(onSubmit).not.toHaveBeenCalled();
    await act(async () => { fireEvent.keyDown(textbox(), { key: 'Enter', ctrlKey: true }); });
    expect(onSubmit).toHaveBeenCalledWith('hello', expect.any(String));
    type('again');
    await act(async () => { fireEvent.keyDown(textbox(), { key: 'Enter', metaKey: true }); });
    expect(onSubmit).toHaveBeenCalledTimes(2);
  });

  it('keeps Send disabled and ignores Ctrl+Enter while blocked', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<NewChatComposer onSubmit={onSubmit} blocked submitLabel="Post" label="Reply" />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Reply' }), { target: { value: 'hello' } });
    expect((screen.getByRole('button', { name: 'Post' }) as HTMLButtonElement).disabled).toBe(true);
    await act(async () => { fireEvent.keyDown(screen.getByRole('textbox', { name: 'Reply' }), { key: 'Enter', ctrlKey: true }); });
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('shows Sending… and disables Send while a send is in flight', async () => {
    // Keep the send pending until the test resolves it.
    let resolve!: () => void;
    const onSubmit = vi.fn(() => new Promise<void>((done) => { resolve = done; }));
    render(<NewChatComposer onSubmit={onSubmit} />);
    type('hello');
    await send();
    const button = screen.getByRole('button', { name: 'Sending…' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    await act(async () => { resolve(); });
    expect(screen.getByRole('button', { name: 'Send' })).toBeTruthy();
  });
});
