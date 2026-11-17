import { useEffect, useLayoutEffect, useRef } from 'react';
import { Textarea } from '@fluentui/react-components';
import {
  updatePlatformPolicyTextBuffer,
} from './platformPolicyContracts';
import type {
  PlatformPolicyTextBuffer,
  PlatformPolicyTextEdit,
} from './platformPolicyContracts';

interface PlatformPolicyTextareaProps {
  label: string;
  buffer: PlatformPolicyTextBuffer;
  rows: number;
  onChange: (buffer: PlatformPolicyTextBuffer) => void;
}

interface PendingInput {
  start: number;
  end: number;
  inputType: string;
}

function lineCount(value: string): number {
  let count = 0;
  for (const character of value)
    if (character === '\n') count += 1;
  return count;
}

function rememberSeparators(history: string[][], separators: string[]) {
  history.push(separators);
}

function takeSeparatorsForLineCount(history: string[][], count: number): string[] | null {
  for (let index = history.length - 1; index >= 0; index -= 1) {
    const separators = history[index];
    if (separators?.length === count) {
      history.splice(index);
      return separators;
    }
  }
  return null;
}

function editRange(
  before: PendingInput,
  previousValue: string,
  nextValue: string,
  nextEnd: number,
): PlatformPolicyTextEdit {
  const deletedLength = previousValue.length - nextValue.length;
  if (before.start === before.end &&
      deletedLength > 0 &&
      before.inputType.endsWith('Backward'))
    return { start: nextEnd, end: before.start, nextEnd };
  if (before.start === before.end &&
      deletedLength > 0 &&
      before.inputType.endsWith('Forward'))
    return { start: before.start, end: before.start + deletedLength, nextEnd };
  return { start: before.start, end: before.end, nextEnd };
}

export function PlatformPolicyTextarea({
  label,
  buffer,
  rows,
  onChange,
}: PlatformPolicyTextareaProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const pendingInputRef = useRef<PendingInput | null>(null);
  const bufferRef = useRef(buffer);
  const emittedBufferRef = useRef<PlatformPolicyTextBuffer | null>(null);
  const undoSeparatorsRef = useRef<string[][]>([]);
  const redoSeparatorsRef = useRef<string[][]>([]);

  useEffect(() => {
    const textarea = textareaRef.current;
    if (!textarea) return;
    const onBeforeInput = (event: Event) => {
      const input = event as InputEvent;
      pendingInputRef.current = {
        start: textarea.selectionStart,
        end: textarea.selectionEnd,
        inputType: input.inputType,
      };
    };
    textarea.addEventListener('beforeinput', onBeforeInput);
    return () => textarea.removeEventListener('beforeinput', onBeforeInput);
  }, []);

  useLayoutEffect(() => {
    if (emittedBufferRef.current !== buffer && bufferRef.current !== buffer) {
      undoSeparatorsRef.current = [];
      redoSeparatorsRef.current = [];
      pendingInputRef.current = null;
    }
    if (emittedBufferRef.current === buffer)
      emittedBufferRef.current = null;
    bufferRef.current = buffer;
  }, [buffer]);

  const updateBuffer = (next: PlatformPolicyTextBuffer) => {
    bufferRef.current = next;
    emittedBufferRef.current = next;
    onChange(next);
  };

  return (
    <Textarea
      ref={textareaRef}
      aria-label={label}
      value={buffer.value}
      onChange={(event, data) => {
        const previous = bufferRef.current;
        const pending = pendingInputRef.current;
        pendingInputRef.current = null;
        const inputType = pending?.inputType ?? '';
        const nextLineCount = lineCount(data.value);

        if (inputType === 'historyUndo' || inputType === 'historyRedo') {
          const history = inputType === 'historyUndo'
            ? undoSeparatorsRef.current
            : redoSeparatorsRef.current;
          const oppositeHistory = inputType === 'historyUndo'
            ? redoSeparatorsRef.current
            : undoSeparatorsRef.current;
          const separators = nextLineCount === previous.lineEndings.length
            ? previous.lineEndings
            : takeSeparatorsForLineCount(history, nextLineCount);
          if (separators) {
            if (separators !== previous.lineEndings)
              rememberSeparators(oppositeHistory, previous.lineEndings);
            updateBuffer({ value: data.value, lineEndings: separators });
            return;
          }
        }

        const edit = pending
          ? editRange(pending, previous.value, data.value, event.currentTarget.selectionStart)
          : undefined;
        const next = updatePlatformPolicyTextBuffer(previous, data.value, edit);
        if (next.lineEndings.length !== previous.lineEndings.length)
          rememberSeparators(undoSeparatorsRef.current, previous.lineEndings);
        redoSeparatorsRef.current = [];
        updateBuffer(next);
      }}
      rows={rows}
      resize="vertical"
      spellCheck={false}
    />
  );
}
