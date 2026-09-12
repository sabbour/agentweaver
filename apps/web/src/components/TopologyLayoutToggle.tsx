import { Radio, RadioGroup, Text } from '@fluentui/react-components';
import type { TopologyLayoutEngine } from '../utils/dagLayout';

export function TopologyLayoutToggle({
  engine,
  onChange,
}: {
  engine: TopologyLayoutEngine;
  onChange: (engine: TopologyLayoutEngine) => void;
}) {
  return (
    <div>
      <Text weight="semibold" size={200} id="topology-layout-label">
        Topology layout
      </Text>
      <RadioGroup
        aria-labelledby="topology-layout-label"
        value={engine}
        onChange={(_, data) => onChange(data.value as TopologyLayoutEngine)}
      >
        <Radio value="balanced-grid" label="Balanced grid (current)" />
        <Radio value="legacy-staircase" label="Legacy staircase (comparison)" />
      </RadioGroup>
      <Text as="p" size={100}>
        Balanced grid is the default rollout engine. Legacy staircase changes only card placement for visual comparison; nodes and edges are unchanged.
      </Text>
    </div>
  );
}
