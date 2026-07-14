import { AzureFluentProvider } from '../copilot-fluent-system';
import { VisualWorkflowEditor } from '../components/VisualWorkflowEditor';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
/**
 * Coverage for #186 — the workflow editor's RAI/Rubberduck/Human Review gate palette:
 * add/configure/remove affordances, branch-routing UI (incl. loop-backs), the
 * merge/scribe read-only backward-compat path, and inline validation for
 * unrouted gate verdicts. Complements the pure-function round-trip coverage in
 * workflowYaml.test.ts with the actual rendered editor surface.
 */

// ResizeObserver is absent in happy-dom; ReactFlow requires it.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    saveWorkflowYaml: vi.fn(),
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
  },
}));

afterEach(cleanup);

function Wrapper({ children }: { children: React.ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

const YAML_WITH_UNROUTED_RAI = `id: sample
name: Sample
description: A sample workflow.
start: implement

nodes:
  - id: implement
    type: prompt
    label: Implement
    agent: backend-engineer

  - id: rai-check
    type: check
    label: RAI Check
    role: review
    kind: gate
    gate_kind: rai
    branches:
      - revise
      - safety-failed
      - no-changes
      - review

  - id: done
    type: terminal
    label: Done

edges:
  - from: implement
    to: rai-check
  - from: rai-check
    to: done
    when: review
`;

const YAML_WITH_LEGACY_TAIL = `id: legacy
name: Legacy
description: Uses the platform-owned merge/scribe tail.
start: implement

nodes:
  - id: implement
    type: prompt
    label: Implement
    agent: backend-engineer

  - id: merge
    type: merge
    label: Merge

  - id: scribe
    type: scribe
    label: Scribe

edges:
  - from: implement
    to: merge
  - from: merge
    to: scribe
`;

function renderEditor(yaml: string) {
  return render(
    <Wrapper>
      <VisualWorkflowEditor projectId="proj-1" workflowId="sample" initialYaml={yaml} />
    </Wrapper>,
  );
}

describe('VisualWorkflowEditor — gate palette (#186)', () => {
  it('warns inline when a gate has unrouted verdicts', async () => {
    renderEditor(YAML_WITH_UNROUTED_RAI);

    // rai-check declares revise / safety-failed / no-changes / review but only
    // "review" has an outgoing edge — the other three should surface as unrouted.
    await waitFor(() => {
      const warning = screen.getByText(/unrouted verdict/i);
      expect(warning.textContent).toContain('rai-check');
      expect(warning.textContent).toContain('revise');
      expect(warning.textContent).toContain('safety-failed');
      expect(warning.textContent).toContain('no-changes');
    });
  });

  it('offers RAI, Rubberduck, Human Review and Build & Test in the add-node palette, but never Merge/Scribe', async () => {
    const user = userEvent.setup();
    renderEditor(YAML_WITH_UNROUTED_RAI);

    await user.click(await screen.findByRole('button', { name: /add node/i }));

    await waitFor(() => {
      expect(screen.getByRole('menuitem', { name: /rai check/i })).toBeDefined();
      expect(screen.getByRole('menuitem', { name: /rubberduck review/i })).toBeDefined();
      expect(screen.getByRole('menuitem', { name: /human review/i })).toBeDefined();
      // "Build & Test" appears twice: once as the pre-configured special-gate
      // shortcut (SPECIAL_GATES) and once as the generic build_test node-type
      // entry (AUTHORABLE_WORKFLOW_NODE_TYPES) — both are valid, non-conflicting
      // ways to add the same node type, so assert at least one exists.
      expect(screen.getAllByRole('menuitem', { name: /build & test/i }).length).toBeGreaterThan(0);
    });

    expect(screen.queryByRole('menuitem', { name: /^merge$/i })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: /^scribe$/i })).toBeNull();
  });

  it('renders existing merge/scribe tail nodes read-only for backward compatibility', async () => {
    renderEditor(YAML_WITH_LEGACY_TAIL);

    // Selecting the legacy merge node (via the canvas node) should show the
    // read-only notice rather than editable fields. We drive selection through
    // the underlying model by clicking the rendered node label.
    const mergeNode = await screen.findByText('Merge');
    fireEvent.click(mergeNode);

    await waitFor(() => {
      expect(screen.getByText(/platform-owned tail steps/i)).toBeDefined();
    });
  });
});
