import type { ReactElement, ReactNode } from 'react';

export type AgentStepStatus = 'pending' | 'running' | 'complete' | 'warning' | 'blocked';

export interface AgentArtifact {
  id: string;
  title: ReactNode;
  type?: ReactNode;
  /** File size label, e.g. "1.2 KB". */
  size?: ReactNode;
  icon?: ReactElement;
  onOpen?: () => void;
  onDownload?: () => void;
}

export interface AgentStep {
  id: string;
  title: ReactNode;
  body?: ReactNode;
  status?: AgentStepStatus;
  /** When true, renders an inline ApprovalGate showing riskText + Approve / Deny. */
  needsInput?: boolean;
  /** Plain-language description of what will happen if approved. */
  riskText?: ReactNode;
  /** Additional disclaimer note rendered below the approval body. */
  disclaimer?: ReactNode;
  /** Label for the approve button (default: "Approve"). */
  approveLabel?: ReactNode;
  /** Label for the deny button (default: "Deny"). */
  denyLabel?: ReactNode;
  /** Whether the step panel is open on first render. Defaults to true when status is "running" or needsInput is true. */
  defaultOpen?: boolean;
  artifacts?: AgentArtifact[];
}

export interface ToolCall {
  id: string;
  name: string;
  /** Short summary of the input args — not raw JSON. */
  inputSummary?: ReactNode;
  /** Short summary of the result. */
  resultSummary?: ReactNode;
  status?: 'running' | 'complete' | 'error';
  artifacts?: AgentArtifact[];
}
