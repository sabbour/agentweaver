import { describe, it, expect } from 'vitest';
import { parseInput, SLASH_COMMANDS, DEFERRED_COMMANDS } from '../consoleCommands';

describe('parseInput (console tokenizer)', () => {
  it('treats a leading slash as the explicit command channel', () => {
    const p = parseInput('/orchestrate ship the feature');
    expect(p.channel).toBe('command');
    if (p.channel === 'command') {
      expect(p.name).toBe('orchestrate');
      expect(p.arg).toBe('ship the feature');
    }
  });

  it('resolves aliases to the canonical command name', () => {
    const p = parseInput('/watch run-123');
    expect(p.channel).toBe('command');
    if (p.channel === 'command') {
      expect(p.name).toBe('monitor');
      expect(p.arg).toBe('run-123');
    }
  });

  it('routes non-slash input to the prose (coordinator) channel', () => {
    const p = parseInput('please add a dark mode toggle');
    expect(p.channel).toBe('prose');
    if (p.channel === 'prose') expect(p.text).toBe('please add a dark mode toggle');
  });

  it('flags an unknown slash token instead of guessing', () => {
    const p = parseInput('/frobnicate x');
    expect(p.channel).toBe('unknown-command');
    if (p.channel === 'unknown-command') expect(p.token).toBe('frobnicate');
  });

  it('parses a command with no argument', () => {
    const p = parseInput('/help');
    expect(p.channel).toBe('command');
    if (p.channel === 'command') {
      expect(p.name).toBe('help');
      expect(p.arg).toBe('');
    }
  });

  it('is case-insensitive on the command token but preserves argument case', () => {
    const p = parseInput('/USE Alpha Beta');
    expect(p.channel).toBe('command');
    if (p.channel === 'command') {
      expect(p.name).toBe('use');
      expect(p.arg).toBe('Alpha Beta');
    }
  });
});

describe('slash command catalog (single source of truth)', () => {
  it('every command names the MCP tool family it wraps', () => {
    for (const c of SLASH_COMMANDS) {
      expect(c.mcp.length).toBeGreaterThan(0);
    }
  });

  it('has unique names and aliases', () => {
    const tokens = new Set<string>();
    for (const c of SLASH_COMMANDS) {
      for (const t of [c.name, ...c.aliases]) {
        expect(tokens.has(t)).toBe(false);
        tokens.add(t);
      }
    }
  });

  it('surfaces deferred capabilities so the boundary is explicit', () => {
    expect(DEFERRED_COMMANDS.length).toBeGreaterThan(0);
    expect(DEFERRED_COMMANDS.some((d) => /review|merge/i.test(d.label + d.summary))).toBe(true);
  });
});
