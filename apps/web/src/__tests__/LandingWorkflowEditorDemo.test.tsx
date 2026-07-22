import { describe, expect, it } from 'vitest';
import {
  WORKFLOW_PRESETS,
  workflowGraphEdges,
  yamlLines,
} from '../components/LandingWorkflowEditorDemo';

describe('landing workflow editor definitions', () => {
  it('renders each preset using the real workflow YAML shape', () => {
    for (const preset of WORKFLOW_PRESETS) {
      const yaml = yamlLines(preset, null).map((line) => line.text).join('\n');

      expect(yaml).toContain(`start: ${preset.start}`);
      expect(yaml).toContain('nodes:');
      expect(yaml).toContain('edges:');
      expect(yaml).toMatch(/\n {4}when: /);
      expect(yaml).not.toContain('schedule:');
    }
  });

  it('builds graph edges from each preset declaration instead of a synthetic chain', () => {
    const graphEdges = WORKFLOW_PRESETS.map((preset) => workflowGraphEdges(preset));

    for (let index = 0; index < WORKFLOW_PRESETS.length; index += 1) {
      expect(graphEdges[index]).toHaveLength(WORKFLOW_PRESETS[index].edges.length + 1);
      expect(graphEdges[index].some((edge) => edge.type === 'loopback')).toBe(true);
    }

    expect(new Set(graphEdges.map((edges) => edges.length)).size).toBe(WORKFLOW_PRESETS.length);
    expect(graphEdges[0].map((edge) => edge.label)).not.toEqual(graphEdges[1].map((edge) => edge.label));
  });
});
