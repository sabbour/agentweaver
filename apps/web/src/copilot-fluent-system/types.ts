/** Shared prop contracts for the Azure Fluent System's data, form, Copilot, and approval components. */
import type { ReactElement, ReactNode } from 'react';
/** Supported layout density presets applied by the system provider. */
export type AzfDensity = 'compact' | 'cozy';
/** Semantic tone names shared by status labels, messages, and data surfaces. */
export type AzfTone = 'neutral' | 'brand' | 'success' | 'warning' | 'danger' | 'info';

/** Command metadata shared by toolbar, header, footer, and response actions. */
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

/** Selectable item shape used by combo boxes, menus, and other option lists. */
export interface AzfOption {
  id: string;
  label: string;
  icon?: ReactElement;
  description?: ReactNode;
  disabled?: boolean;
}

/** Filter chip state and optional callback used by filter bars. */
export interface AzfFilter {
  id: string;
  label: string;
  value?: ReactNode;
  selected?: boolean;
  removable?: boolean;
  disabled?: boolean;
  onRemove?: () => void;
}

/** Sort order accepted by data-grid sort state. */
export type AzfSortDirection = 'ascending' | 'descending';

/** Active sort column and direction controlled by a data-grid host. */
export interface AzfSortState {
  columnId: string;
  direction: AzfSortDirection;
}

/** Column renderer and optional comparable value used by the generic data grid. */
export interface AzfColumn<T> {
  columnId: string;
  header: ReactNode;
  renderCell: (item: T) => ReactNode;
  width?: string;
  sortable?: boolean;
  sortValue?: (item: T) => string | number | Date | null | undefined;
  ariaLabel?: string;
}

/** Labeled summary value with an optional semantic status marker. */
export interface AzfSummaryMetric {
  id: string;
  label: ReactNode;
  value: ReactNode;
  /** Optional leading status dot tone. Omit for no dot. */
  tone?: 'neutral' | 'success' | 'warning' | 'danger' | 'info';
}

/** A labeled property row used in resource detail panels. */
export interface AzfPropertyItem {
  id: string;
  label: ReactNode;
  value: ReactNode;
}

/** Editable tag data associated with a resource identifier. */
export interface AzfResourceTagRow {
  id: string;
  name: string;
  value: string;
  resourceId?: string;
}

/** A navigable service-menu item; `items` carries nested children. */
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

/** A labeled service-menu group with an ordered list of navigation items. */
export interface AzfServiceMenuGroup {
  id: string;
  label: string;
  icon?: ReactNode;
  items: AzfServiceMenuItem[];
  defaultOpen?: boolean;
}

/** Current page, page size, and total row count used by a pager. */
export interface AzfPagerState {
  page: number;
  pageSize: number;
  totalItems: number;
}

/** Attachment metadata and optional removal action for Copilot composers. */
export interface AzfAttachment {
  id: string;
  name: string;
  description?: ReactNode;
  onRemove?: () => void;
}

/** Visual states for copy controls, including an externally controlled copied state. */
export type AzfCopyButtonVisualState = 'rest' | 'hover' | 'copied';

/** Named interaction states rendered by the file-upload control. */
export type AzfFileUploadState = 'default' | 'selected' | 'progress' | 'success' | 'dragdrop';

/** One collapsible row in the system accordion. */
export interface AzfAccordionItem {
  id: string;
  title: ReactNode;
  content?: ReactNode;
  icon?: ReactNode;
  disabled?: boolean;
}

/** Syntax color categories supported by the code snippet renderer. */
export type AzfCodeSnippetTokenTone = 'plain' | 'key' | 'string' | 'keyword' | 'number' | 'comment' | 'operator' | 'muted';

/** Text fragment and optional syntax category for a code line. */
export interface AzfCodeSnippetToken {
  text: string;
  tone?: AzfCodeSnippetTokenTone;
}

/** One code row, optionally tokenized and annotated with line, fold, or indentation metadata. */
export interface AzfCodeSnippetLine {
  id?: string;
  lineNumber?: number;
  text?: string;
  tokens?: readonly AzfCodeSnippetToken[];
  foldState?: 'expanded' | 'collapsed';
  indentLevel?: number;
}

/** A response segment rendered as assistant/user text, a choice prompt, or a confirmation gate. */
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

/** Small semantic badge attached to an agent step. */
export interface AzfStepBadge {
  label: ReactNode;
  tone?: 'success' | 'warning' | 'danger' | 'info';
}

/** Progress and approval state for one automation step shown in agentic progress surfaces. */
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
  /** Inline "request changes" button label. When set, a Change button appears that expands a feedback textarea. */
  changeLabel?: ReactNode;
  /** Inline semantic badge rendered after the step title (e.g. "Approved by user"). */
  badge?: AzfStepBadge;
  /** Whether the step's sub-content is expanded on first render. */
  defaultOpen?: boolean;
  artifacts?: AzfArtifact[];
}

/** File or other output associated with a step, with optional open and download actions. */
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
