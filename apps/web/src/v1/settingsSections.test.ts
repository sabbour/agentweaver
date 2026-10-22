import { describe, expect, it } from 'vitest';
import { CONFIGURATION_SECTIONS, SETTINGS_SECTIONS, settingsSection } from './settingsSections';

describe('settings sections', () => {
  it('returns each known section key unchanged', () => {
    for (const section of SETTINGS_SECTIONS) expect(settingsSection(section.key)).toBe(section.key);
  });

  it('falls back to General for missing, empty, or unknown values', () => {
    expect(settingsSection(null)).toBe('general');
    expect(settingsSection('')).toBe('general');
    expect(settingsSection('nope')).toBe('general');
    // Keys are case-sensitive, so a label is not accepted as a key.
    expect(settingsSection('Models')).toBe('general');
  });

  it('keeps the rail order and unique keys', () => {
    expect(SETTINGS_SECTIONS.map((section) => section.key)).toEqual([
      'general', 'access', 'repository', 'models', 'components', 'limits', 'connections', 'history', 'danger', 'advanced',
    ]);
    expect(new Set(SETTINGS_SECTIONS.map((section) => section.key)).size).toBe(SETTINGS_SECTIONS.length);
  });

  it('marks only the shared-draft sections as configuration sections', () => {
    // These sections share one Save button. The others save on their own or are read-only.
    expect([...CONFIGURATION_SECTIONS].sort()).toEqual(['advanced', 'components', 'limits', 'models', 'repository']);
    for (const key of ['general', 'access', 'connections', 'history', 'danger'] as const)
      expect(CONFIGURATION_SECTIONS.has(key)).toBe(false);
  });
});
