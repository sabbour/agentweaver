import { formatAbsoluteTime, formatRelativeTime } from '../utils/relativeTime';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

describe('formatRelativeTime', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-14T04:00:00.000Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('returns "just now" for timestamps within the last 5 seconds', () => {
    expect(formatRelativeTime(Date.now() - 2_000)).toBe('just now');
  });

  it('formats seconds', () => {
    expect(formatRelativeTime(Date.now() - 30_000)).toBe('30s ago');
  });

  it('formats minutes', () => {
    expect(formatRelativeTime(Date.now() - 3 * 60_000)).toBe('3m ago');
  });

  it('formats hours', () => {
    expect(formatRelativeTime(Date.now() - 5 * 60 * 60_000)).toBe('5h ago');
  });

  it('formats days', () => {
    expect(formatRelativeTime(Date.now() - 2 * 24 * 60 * 60_000)).toBe('2d ago');
  });

  it('accepts an ISO string in addition to an epoch-ms number', () => {
    const iso = new Date(Date.now() - 60_000).toISOString();
    expect(formatRelativeTime(iso)).toBe('1m ago');
  });

  it('falls back to the raw string for an unparseable value', () => {
    expect(formatRelativeTime('not-a-date')).toBe('not-a-date');
  });
});

describe('formatAbsoluteTime', () => {
  it('matches Date#toLocaleString for a known epoch-ms value', () => {
    const ms = new Date('2026-07-14T03:55:00-07:00').getTime();
    expect(formatAbsoluteTime(ms)).toBe(new Date(ms).toLocaleString());
  });
});
