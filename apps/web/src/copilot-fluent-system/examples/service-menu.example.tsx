import { useMemo, useState } from 'react';
import { BotRegular, CubeRegular, DataTrendingRegular, SettingsRegular, ShieldTaskRegular } from '@fluentui/react-icons';
import { Button, ServiceMenu, Text, type AzfServiceMenuGroup } from '..';
const initialGroups: AzfServiceMenuGroup[] = [
  {
    id: 'overview',
    label: 'Overview',
    items: [
      { id: 'summary', label: 'Summary', icon: <CubeRegular />, favorite: true },
      { id: 'activity', label: 'Activity log', icon: <DataTrendingRegular />, badge: '12' },
    ],
  },
  {
    id: 'operations',
    label: 'Operations',
    items: [
      {
        id: 'automation',
        label: 'Automation',
        icon: <BotRegular />,
        items: [
          { id: 'runbooks', label: 'Runbooks', icon: <BotRegular /> },
          { id: 'approvals', label: 'Approvals', icon: <ShieldTaskRegular />, favorite: true },
        ],
      },
      { id: 'settings', label: 'Settings', icon: <SettingsRegular /> },
    ],
  },
];

function updateFavorite(groups: AzfServiceMenuGroup[], id: string): AzfServiceMenuGroup[] {
  return groups.map((group) => ({
    ...group,
    items: group.items.map((item) => {
      if (item.id === id) {
        return { ...item, favorite: !item.favorite };
      }

      if (!item.items) {
        return item;
      }

      return {
        ...item,
        items: item.items.map((child) => (child.id === id ? { ...child, favorite: !child.favorite } : child)),
      };
    }),
  }));
}

function flattenGroups(groups: AzfServiceMenuGroup[]) {
  return groups.flatMap((group) =>
    group.items.flatMap((item) => [item, ...(item.items ?? [])]),
  );
}

export function ServiceMenuExample() {
  const [groups, setGroups] = useState(initialGroups);
  const [selectedId, setSelectedId] = useState('summary');
  const [searchValue, setSearchValue] = useState('');
  const [collapsed, setCollapsed] = useState(false);

  const selectedItem = useMemo(
    () => flattenGroups(groups).find((item) => item.id === selectedId),
    [groups, selectedId],
  );

  return (
    <div className="azf-pattern-grid">
      <div className="azf-stack azf-gap-s">
        <Button appearance="subtle" onClick={() => setCollapsed((current) => !current)}>
          {collapsed ? 'Expand navigation' : 'Collapse navigation'}
        </Button>
        <ServiceMenu
          groups={groups}
          selectedId={selectedId}
          onSelect={setSelectedId}
          searchable
          collapsed={collapsed}
          searchValue={searchValue}
          onSearchChange={setSearchValue}
          onToggleFavorite={(id) => setGroups((current) => updateFavorite(current, id))}
        />
      </div>

      <section className="azf-stack azf-gap-s" aria-label="Selected navigation target">
        <Text as="h2" weight="semibold">
          {selectedItem?.label ?? 'Choose a menu item'}
        </Text>
        <Text className="azf-muted">
          Use the menu to drive the host app routing state. Search, nested items, favorites, and collapsed navigation all stay controlled outside the library.
        </Text>
        <Text>
          Current route key: <strong>{selectedId}</strong>
        </Text>
      </section>
    </div>
  );
}
