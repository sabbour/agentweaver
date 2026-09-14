import { readFileSync } from 'node:fs';

export const ICONCLOUD_CATALOG = JSON.parse(readFileSync(
  new URL('../../docs/diagrams/drawio/iconcloud-fluent-icons.json', import.meta.url),
  'utf8',
));

const rules = ICONCLOUD_CATALOG.semanticRules.map(rule => ({
  icon: rule.icon,
  pattern: new RegExp(rule.pattern, 'i'),
}));

export function resolveFluentIcon(node) {
  if (node.fluentIcon) {
    if (!ICONCLOUD_CATALOG.icons[node.fluentIcon]) {
      throw new Error(`Unknown IconCloud Fluent icon: ${node.fluentIcon}`);
    }
    return node.fluentIcon;
  }
  const semanticText = `${node.id ?? ''} ${node.label ?? ''} ${node.subLabel ?? ''} ${node.meta ?? ''}`;
  const matched = rules.find(rule => rule.pattern.test(semanticText));
  if (matched) return matched.icon;
  const alias = ICONCLOUD_CATALOG.legacyAliases[node.icon ?? 'box'];
  if (alias) return alias;
  throw new Error(`No IconCloud Fluent icon mapping for ${node.id ?? node.label ?? 'node'}`);
}

export function iconCloudSourceUrl(icon) {
  const entry = ICONCLOUD_CATALOG.icons[icon];
  if (!entry) throw new Error(`Unknown IconCloud Fluent icon: ${icon}`);
  const query = encodeURIComponent(entry.query);
  return `https://iconcloud.design/search?q=${query}&library=fluent-system-library&collection=fluent-regular`;
}
