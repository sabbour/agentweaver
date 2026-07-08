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

export type AzfResponsePart =
  | { id: string; type: 'text'; content: ReactNode }
  | { id: string; type: 'choices'; label: ReactNode; choices: AzfOption[]; multiple?: boolean; submitLabel?: string; onSubmit?: (ids: string[]) => void }
  | { id: string; type: 'confirmation'; content: ReactNode; confirmLabel: string; cancelLabel?: string; onConfirm?: () => void; onCancel?: () => void };

export interface AzfAgentStep {
  id: string;
  title: ReactNode;
  body?: ReactNode;
  status?: 'pending' | 'running' | 'complete' | 'warning' | 'blocked' | 'error';
  needsInput?: boolean;
  riskText?: ReactNode;
  artifacts?: AzfArtifact[];
}

export interface AzfArtifact {
  id: string;
  title: ReactNode;
  type?: ReactNode;
  icon?: ReactElement;
  onOpen?: () => void;
}
