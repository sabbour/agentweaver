# components/ui/agentic

Native Fluent v9 components for rendering **Agentic Progress** — the run-activity, tool-call, and human-approval surface for Agentweaver. Uses `@fluentui/react-components` and theme tokens only; no external kit imports.

## Data model

### `AgentStep`

```ts
interface AgentStep {
  id: string;
  title: ReactNode;
  body?: ReactNode;
  status?: 'pending' | 'running' | 'complete' | 'warning' | 'blocked';
  needsInput?: boolean;       // surfaces an inline ApprovalGate
  riskText?: ReactNode;       // plainly-worded consequence shown before the human decides
  disclaimer?: ReactNode;     // secondary note below the approval body
  approveLabel?: ReactNode;   // default "Approve"
  denyLabel?: ReactNode;      // default "Deny"
  defaultOpen?: boolean;      // panel open on first render; auto-true for running/needsInput
  artifacts?: AgentArtifact[];
}
```

### `AgentArtifact`

```ts
interface AgentArtifact {
  id: string;
  title: ReactNode;
  type?: ReactNode;     // e.g. "JSON", "Plan"
  size?: ReactNode;     // e.g. "1.2 KB"
  icon?: ReactElement;
  onOpen?: () => void;
  onDownload?: () => void;
}
```

### `ToolCall`

```ts
interface ToolCall {
  id: string;
  name: string;
  inputSummary?: ReactNode;
  resultSummary?: ReactNode;
  status?: 'running' | 'complete' | 'error';
  artifacts?: AgentArtifact[];
}
```

## Components

### `AgentStepList`

Vertical expandable timeline of `AgentStep` items.

```tsx
<AgentStepList
  steps={steps}
  onApprove={(stepId) => approve(stepId)}
  onDeny={(stepId) => deny(stepId)}
  aria-label="Run steps"
/>
```

Props: `steps`, `onApprove`, `onDeny`, `className`, `aria-label`.

Steps with `status="running"` or `needsInput=true` open by default. Status is always conveyed by **icon + label**, never color alone. All transitions respect `prefers-reduced-motion`.

### `AgentStepItem`

Single step row — use inside a custom list if needed.

```tsx
<AgentStepItem step={step} onApprove={onApprove} onDeny={onDeny} />
```

### `ApprovalGate`

Inline human-in-the-loop approval block. Renders `riskText` plainly and exposes **Approve / Deny** buttons. Preferred over a modal; no dialog required for the common case.

```tsx
<ApprovalGate
  stepId={step.id}
  body="The agent will write to three production files."
  riskText="Approving lets the agent write; you can review the diff before merge."
  disclaimer="Denying stops the run immediately."
  onApprove={onApprove}
  onDeny={onDeny}
/>
```

### `ArtifactChip`

Clickable chip for a single artifact. Shows icon + title + type label.

```tsx
<ArtifactChip artifact={{ id: 'plan', title: 'plan.md', type: 'Plan', onOpen: openPlan }} />
```

### `ToolCallRow`

Compact, expandable row for a single tool call and its result. Shows a monospace tool name, status icon, optional input/result summary, and artifact chips.

```tsx
<ToolCallRow toolCall={{ id: 'rf1', name: 'read_file', inputSummary: 'src/app.ts', resultSummary: '180 lines', status: 'complete' }} />
```

## Design principles

- Warm-monochrome tokens only — no blue, no Azure vocabulary.
- Status always has an icon **and** a text label (`Complete`, `Running`, `Needs input`, `Blocked`, `Pending`).
- `needsInput` steps show the `ApprovalGate` inline; the risk is stated plainly before the human decides.
- All `@keyframes` animations check `prefers-reduced-motion`.
- No uppercase section headings; no dense resource-grid density.
