import type { ReactElement, ReactNode } from 'react';
export type AzfDensity = 'compact' | 'cozy';
export type AzfTone = 'neutral' | 'brand' | 'success' | 'warning' | 'danger' | 'info';

export interface AzfAction {
  id: string;
  label: string;
  icon?: ReactElement;
  disabled?: boolean;
  loading?: boolean;
  appearance?: 'primary' | 'secondary' | 'subtle' | 'outline' | 'transparent';
  destructive?: boolean;
  onClick?: () => void;
}

export interface AzfOption {
  id: string;
  label: string;
  icon?: ReactElement;
  description?: ReactNode;
  disabled?: boolean;
}

export interface AzfFilter {
  id: string;
  label: string;
  value?: ReactNode;
  selected?: boolean;
  removable?: boolean;
  disabled?: boolean;
  onRemove?: () => void;
}

export type AzfSortDirection = 'ascending' | 'descending';

export interface AzfSortState {
  columnId: string;
  direction: AzfSortDirection;
}

export interface AzfColumn<T> {
  columnId: string;
  header: ReactNode;
  renderCell: (item: T) => ReactNode;
  width?: string;
  sortable?: boolean;
  sortValue?: (item: T) => string | number | Date | null | undefined;
  ariaLabel?: string;
}

export interface AzfSummaryMetric {
  id: string;
  label: ReactNode;
  value: ReactNode;
  /** Optional leading status dot tone. Omit for no dot. */
  tone?: 'neutral' | 'success' | 'warning' | 'danger' | 'info';
}

export interface AzfPropertyItem {
  id: string;
  label: ReactNode;
  value: ReactNode;
}

export interface AzfResourceTagRow {
  id: string;
  name: string;
  value: string;
  resourceId?: string;
}

export interface AzfServiceMenuItem {
  id: string;
  label: string;
  icon?: ReactElement;
  child?: boolean;
  favorite?: boolean;
  disabled?: boolean;
  badge?: ReactNode;
  items?: AzfServiceMenuItem[];
}

export interface AzfServiceMenuGroup {
  id: string;
  label: string;
  icon?: ReactNode;
  items: AzfServiceMenuItem[];
  defaultOpen?: boolean;
}

export interface AzfPagerState {
  page: number;
  pageSize: number;
  totalItems: number;
}

export interface AzfAttachment {
  id: string;
  name: string;
  description?: ReactNode;
  onRemove?: () => void;
}

export type AzfCopyButtonVisualState = 'rest' | 'hover' | 'copied';

export type AzfFileUploadState = 'default' | 'selected' | 'progress' | 'success' | 'dragdrop';

export interface AzfAccordionItem {
  id: string;
  title: ReactNode;
  content?: ReactNode;
  icon?: ReactNode;
  disabled?: boolean;
}

export type AzfCodeSnippetTokenTone = 'plain' | 'key' | 'string' | 'keyword' | 'number' | 'comment' | 'operator' | 'muted';

export interface AzfCodeSnippetToken {
  text: string;
  tone?: AzfCodeSnippetTokenTone;
}

export interface AzfCodeSnippetLine {
  id?: string;
  lineNumber?: number;
  text?: string;
  tokens?: readonly AzfCodeSnippetToken[];
  foldState?: 'expanded' | 'collapsed';
  indentLevel?: number;
}

export type AzfResponsePart =
  | {
    id: string;
    type: 'text';
    content: ReactNode;
    author?: 'assistant' | 'user';
    title?: ReactNode;
    badge?: ReactNode;
    supportingText?: ReactNode;
    footerActions?: AzfAction[];
  }
  | { id: string; type: 'choices'; label: ReactNode; choices: AzfOption[]; multiple?: boolean; submitLabel?: string; onSubmit?: (ids: string[]) => void }
  | { id: string; type: 'confirmation'; content: ReactNode; confirmLabel: string; cancelLabel?: string; onConfirm?: () => void; onCancel?: () => void };

export interface AzfStepBadge {
  label: ReactNode;
  tone?: 'success' | 'warning' | 'danger' | 'info';
}

export interface AzfAgentStep {
  id: string;
  title: ReactNode;
  body?: ReactNode;
  status?: 'pending' | 'running' | 'complete' | 'warning' | 'blocked' | 'error';
  needsInput?: boolean;
  riskText?: ReactNode;
  /** Caption shown under the approval body (e.g. "Denying will immediately stop reasoning"). */
  disclaimer?: ReactNode;
  /** Primary approval button label (defaults to "Approve"). */
  approveLabel?: ReactNode;
  /** Secondary deny button label (defaults to "Deny"). */
  denyLabel?: ReactNode;
  /** Inline semantic badge rendered after the step title (e.g. "Approved by user"). */
  badge?: AzfStepBadge;
  /** Whether the step's sub-content is expanded on first render. */
  defaultOpen?: boolean;
  artifacts?: AzfArtifact[];
}

export interface AzfArtifact {
  id: string;
  title: ReactNode;
  type?: ReactNode;
  /** File size meta rendered after the type (e.g. "1KB") as "type · size". */
  size?: ReactNode;
  icon?: ReactElement;
  onOpen?: () => void;
  onDownload?: () => void;
}
