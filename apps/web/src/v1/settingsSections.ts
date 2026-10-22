/** Project settings sections in rail order. The `key` is the `?section=` value. */
export const SETTINGS_SECTIONS = [
  { key: 'general', label: 'General' },
  { key: 'access', label: 'Access' },
  { key: 'repository', label: 'Repository' },
  { key: 'models', label: 'Models' },
  { key: 'components', label: 'Platform components' },
  { key: 'limits', label: 'Run limits' },
  { key: 'connections', label: 'Connections' },
  { key: 'history', label: 'History' },
  { key: 'danger', label: 'Danger zone' },
  { key: 'advanced', label: 'Advanced' },
] as const;

/** One `?section=` value. */
export type SettingsSection = typeof SETTINGS_SECTIONS[number]['key'];

/** Sections whose fields edit the shared configuration draft and use one Save button. */
export const CONFIGURATION_SECTIONS: ReadonlySet<SettingsSection> = new Set(['repository', 'models', 'components', 'limits', 'advanced']);

/** Returns a known section, or General for a missing or unknown value. */
export function settingsSection(value: string | null): SettingsSection {
  // Matching is exact and case-sensitive. A bad link opens General instead of a blank page.
  return SETTINGS_SECTIONS.find((section) => section.key === value)?.key ?? 'general';
}
