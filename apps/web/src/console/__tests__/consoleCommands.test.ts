import { describe, it, expect } from 'vitest';
import { parseConsoleCommand, CONSOLE_COMMANDS, DEFERRED_COMMANDS } from '../consoleCommands';

describe('parseConsoleCommand', () => {
  it('recognizes help aliases', () => {
    for (const s of ['help', 'HELP', '?', 'commands', 'h']) {
      expect(parseConsoleCommand(s)).toEqual({ kind: 'help' });
    }
  });

  it('lists projects', () => {
    for (const s of ['projects', 'list projects', 'show projects', 'ls projects']) {
      expect(parseConsoleCommand(s)).toEqual({ kind: 'list_projects' });
    }
  });

  it('does not confuse "runs" with the "run <goal>" orchestration alias', () => {
    expect(parseConsoleCommand('runs')).toEqual({ kind: 'list_runs' });
    expect(parseConsoleCommand('orchestrations')).toEqual({ kind: 'list_runs' });
    expect(parseConsoleCommand('run add oauth')).toEqual({ kind: 'start_orchestration', goal: 'add oauth' });
  });

  it('selects a project and asks for clarification when empty', () => {
    expect(parseConsoleCommand('use alpha')).toEqual({ kind: 'use_project', query: 'alpha' });
    expect(parseConsoleCommand('select project My App')).toEqual({ kind: 'use_project', query: 'My App' });
    expect(parseConsoleCommand('use').kind).toBe('clarify');
  });

  it('captures backlog items with optional description', () => {
    expect(parseConsoleCommand('add backlog Fix login')).toEqual({ kind: 'create_backlog', title: 'Fix login' });
    expect(parseConsoleCommand('add backlog Fix login :: it 500s on submit')).toEqual({
      kind: 'create_backlog',
      title: 'Fix login',
      description: 'it 500s on submit',
    });
    expect(parseConsoleCommand('capture Improve docs').kind).toBe('create_backlog');
    expect(parseConsoleCommand('add backlog').kind).toBe('clarify');
  });

  it('promotes backlog items to ready and clarifies when empty', () => {
    expect(parseConsoleCommand('ready Fix login')).toEqual({ kind: 'promote_backlog', query: 'Fix login' });
    expect(parseConsoleCommand('promote task-123')).toEqual({ kind: 'promote_backlog', query: 'task-123' });
    expect(parseConsoleCommand('ready').kind).toBe('clarify');
  });

  it('starts orchestrations and clarifies when the goal is missing', () => {
    expect(parseConsoleCommand('orchestrate ship the feature')).toEqual({ kind: 'start_orchestration', goal: 'ship the feature' });
    expect(parseConsoleCommand('start orchestration do the thing')).toEqual({ kind: 'start_orchestration', goal: 'do the thing' });
    expect(parseConsoleCommand('orchestrate').kind).toBe('clarify');
    expect(parseConsoleCommand('start').kind).toBe('clarify');
  });

  it('monitors runs by id and clarifies when missing', () => {
    expect(parseConsoleCommand('monitor run-9')).toEqual({ kind: 'monitor', runId: 'run-9' });
    expect(parseConsoleCommand('watch run-9 extra')).toEqual({ kind: 'monitor', runId: 'run-9' });
    expect(parseConsoleCommand('monitor').kind).toBe('clarify');
  });

  it('returns unknown for unrecognized input', () => {
    expect(parseConsoleCommand('do a barrel roll')).toEqual({ kind: 'unknown', input: 'do a barrel roll' });
  });

  it('exposes available and deferred command catalogs', () => {
    expect(CONSOLE_COMMANDS.every((c) => c.status === 'available')).toBe(true);
    expect(DEFERRED_COMMANDS.every((c) => c.status === 'deferred')).toBe(true);
    // Gates (approve/review/merge) must be flagged deferred so the console never bypasses them.
    expect(DEFERRED_COMMANDS.some((c) => /approve|review|merge/i.test(c.usage))).toBe(true);
  });
});
