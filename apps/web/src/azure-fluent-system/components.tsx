import type { KeyboardEvent, ReactElement, ReactNode } from 'react';
import { useId, useMemo, useState } from 'react';
import {
  Accordion, AccordionHeader, AccordionItem, AccordionPanel, Badge, Button, Card, Checkbox, Combobox, Field, Input, Link,
  MessageBar, MessageBarBody, Option, Popover, PopoverSurface, PopoverTrigger, ProgressBar, Radio, RadioGroup, SearchBox,
  Spinner, Tab, TabList, Tag, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea,
  ToggleButton, Toolbar, ToolbarButton, Tooltip, mergeClasses,
} from '@fluentui/react-components';
import {
  AddRegular, ArrowRightRegular, CheckmarkCircleRegular, ChevronDownRegular, ChevronLeftRegular, ChevronRightRegular,
  DeleteRegular, DismissRegular, DocumentRegular, ErrorCircleRegular, InfoRegular, MoreHorizontalRegular, PinRegular,
  SearchRegular, SparkleRegular, StarRegular, StopRegular, ThumbDislikeRegular, ThumbLikeRegular, WarningRegular,
} from '@fluentui/react-icons';
import './tokens.css';
import type {
  AzfAction, AzfAgentStep, AzfArtifact, AzfAttachment, AzfColumn, AzfFilter, AzfOption, AzfPagerState,
  AzfResourceTagRow, AzfResponsePart, AzfServiceMenuGroup, AzfServiceMenuItem, AzfSortState, AzfTone,
} from './types';
import { AzureIcon } from './icons';

function buttonAppearance(action?: Pick<AzfAction, 'appearance' | 'destructive'>) {
  if (action?.destructive) return 'outline' as const;
  return action?.appearance ?? 'subtle';
}

function optionText(label: ReactNode) {
  return typeof label === 'string' || typeof label === 'number' ? String(label) : 'Option';
}

function defaultSort<T>(items: T[], columns: AzfColumn<T>[], sortState?: AzfSortState) {
  if (!sortState) return items;
  const column = columns.find((candidate) => candidate.columnId === sortState.columnId);
  if (!column?.sortValue) return items;
  return [...items].sort((left, right) => {
    const a = column.sortValue?.(left);
    const b = column.sortValue?.(right);
    if (a == null && b == null) return 0;
    if (a == null) return 1;
    if (b == null) return -1;
    const result = a instanceof Date || b instanceof Date ? Number(a) - Number(b) : String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
    return sortState.direction === 'ascending' ? result : -result;
  });
}

function renderAction(action: AzfAction, defaultAppearance?: AzfAction['appearance']) {
  const icon = action.loading ? <Spinner size="tiny" /> : action.icon;
  const button = (
    <Button
      key={action.id}
      appearance={action.appearance ?? defaultAppearance ?? buttonAppearance(action)}
      icon={icon}
      disabled={action.disabled || action.loading}
      onClick={action.onClick}
      aria-label={typeof action.label === 'string' && action.icon ? action.label : undefined}
      data-destructive={action.destructive || undefined}
    >
      {action.icon ? undefined : action.label}
    </Button>
  );
  return action.icon ? <Tooltip key={action.id} content={action.label} relationship="label">{button}</Tooltip> : button;
}

export interface IconActionButtonProps extends AzfAction { size?: 'small' | 'medium' | 'large'; }

export function IconActionButton({ size = 'medium', ...action }: IconActionButtonProps) {
  return (
    <Tooltip content={action.label} relationship="label">
      <Button
        appearance={buttonAppearance(action)}
        size={size}
        icon={action.loading ? <Spinner size="tiny" /> : action.icon ?? <MoreHorizontalRegular />}
        disabled={action.disabled || action.loading}
        onClick={action.onClick}
        aria-label={action.label}
        data-destructive={action.destructive || undefined}
      />
    </Tooltip>
  );
}

export interface StatusIconTextProps { status?: AzfTone; icon?: ReactNode; children?: ReactNode; className?: string; }

export function StatusIconText({ status = 'neutral', icon, children, className }: StatusIconTextProps) {
  const fallback = status === 'success' ? <CheckmarkCircleRegular /> : status === 'danger' ? <ErrorCircleRegular /> : status === 'warning' ? <WarningRegular /> : <InfoRegular />;
  return <span className={mergeClasses('azf-row azf-gap-xs azf-status-text', className)} data-status={status}><AzureIcon size={12} icon={icon ?? fallback} />{children && <Text>{children}</Text>}</span>;
}

export interface BladeHeaderProps {
  title: ReactNode;
  subtitle?: ReactNode;
  resourceIcon?: ReactNode;
  menuLabel?: string;
  menu?: ReactNode;
  actions?: AzfAction[];
  overflowActions?: AzfAction[];
  onDismiss?: () => void;
  promptRibbon?: ReactNode;
  size?: 'large' | 'compact';
  loading?: boolean;
  className?: string;
}

export function BladeHeader({ title, subtitle, resourceIcon, menuLabel, menu, actions = [], overflowActions = [], onDismiss, promptRibbon, size = 'large', loading, className }: BladeHeaderProps) {
  const visibleActions = actions.slice(0, 3);
  const overflow = [...actions.slice(3), ...overflowActions];
  return (
    <header className={mergeClasses('azf-stack azf-blade-header', className)} aria-busy={loading || undefined}>
      <div className="azf-row azf-blade-header__main">
        <div className="azf-row azf-blade-header__title">
          {resourceIcon && <AzureIcon className="azf-blade-header__icon" size={32} icon={resourceIcon} label={menuLabel} />}
          <div className="azf-stack azf-gap-xs azf-blade-header__text">
            <Text as="h1" size={size === 'compact' ? 500 : 700} weight="semibold">{title}</Text>
            {subtitle && <Text className="azf-muted">{subtitle}</Text>}
          </div>
          {menu && <div className="azf-blade-header__menu">{menu}</div>}
        </div>
        <div className="azf-row azf-blade-header__actions" aria-label="Blade actions">
          {loading && <Spinner size="tiny" labelPosition="after" label="Loading" />}
          {visibleActions.map((action) => renderAction(action))}
          {overflow.length > 0 && (
            <Popover positioning="below-end">
              <PopoverTrigger disableButtonEnhancement><Button appearance="subtle" icon={<MoreHorizontalRegular />} aria-label="More actions" /></PopoverTrigger>
              <PopoverSurface className="azf-stack azf-action-overflow">{overflow.map((action) => <Button key={action.id} appearance="subtle" icon={action.icon} disabled={action.disabled || action.loading} onClick={action.onClick}>{action.label}</Button>)}</PopoverSurface>
            </Popover>
          )}
          {onDismiss && <IconActionButton id="dismiss" label="Close" icon={<DismissRegular />} onClick={onDismiss} />}
        </div>
      </div>
      {promptRibbon && <div className="azf-blade-header__ribbon">{promptRibbon}</div>}
    </header>
  );
}

export interface ServiceMenuProps {
  groups: AzfServiceMenuGroup[];
  selectedId?: string;
  onSelect?: (id: string) => void;
  searchable?: boolean;
  collapsed?: boolean;
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  onToggleFavorite?: (id: string) => void;
  ariaLabel?: string;
  className?: string;
}

function flattenMenuItem(item: AzfServiceMenuItem, depth = 0): Array<AzfServiceMenuItem & { depth: number }> {
  return [{ ...item, depth }, ...(item.items ?? []).flatMap((child) => flattenMenuItem(child, depth + 1))];
}

export function ServiceMenu({ groups, selectedId, onSelect, searchable = true, collapsed = false, searchValue, onSearchChange, onToggleFavorite, ariaLabel = 'Service navigation', className }: ServiceMenuProps) {
  const [internalSearch, setInternalSearch] = useState('');
  const query = (searchValue ?? internalSearch).trim().toLowerCase();
  const setQuery = (value: string) => (onSearchChange ?? setInternalSearch)(value);
  return (
    <nav className={mergeClasses('azf-stack azf-service-menu', collapsed && 'azf-service-menu--collapsed', className)} aria-label={ariaLabel}>
      {searchable && !collapsed && <SearchBox contentBefore={<SearchRegular />} aria-label="Filter navigation" placeholder="Search" value={searchValue ?? internalSearch} onChange={(_, data) => setQuery(data.value)} />}
      {groups.map((group) => {
        const items = group.items.flatMap((item) => flattenMenuItem(item)).filter((item) => !query || item.label.toLowerCase().includes(query));
        if (items.length === 0) return null;
        return (
          <div className="azf-stack azf-service-menu__group" key={group.id}>
            {!collapsed && <Text className="azf-service-menu__group-title azf-muted" weight="semibold">{group.label}</Text>}
            {items.map((item) => {
              const selected = item.id === selectedId;
              const content = (
                <Button appearance="subtle" className="azf-service-menu__item" data-selected={selected || undefined} data-child={item.child || item.depth > 0 || undefined} style={{ paddingInlineStart: collapsed ? undefined : 8 + item.depth * 16 }} icon={<span className="azf-service-menu__icon">{item.icon}</span>} disabled={item.disabled} aria-current={selected ? 'page' : undefined} aria-label={collapsed ? item.label : undefined} onClick={() => onSelect?.(item.id)}>
                  {collapsed ? undefined : <span className="azf-service-menu__label">{item.label}</span>}
                  {!collapsed && item.badge && <Badge size="small">{item.badge}</Badge>}
                </Button>
              );
              return <div className="azf-row azf-service-menu__item-row" key={item.id}>{collapsed ? <Tooltip content={item.label} relationship="label">{content}</Tooltip> : content}{!collapsed && onToggleFavorite && <IconActionButton id={`favorite-${item.id}`} label={item.favorite ? `Unfavorite ${item.label}` : `Favorite ${item.label}`} icon={item.favorite ? <StarRegular /> : <PinRegular />} size="small" appearance="subtle" onClick={() => onToggleFavorite(item.id)} />}</div>;
            })}
          </div>
        );
      })}
    </nav>
  );
}
export interface DataToolbarProps { title?: ReactNode; actions?: AzfAction[]; children?: ReactNode; className?: string; ariaLabel?: string; }

export function DataToolbar({ title, actions = [], children, className, ariaLabel = 'Data actions' }: DataToolbarProps) {
  return (
    <div className={mergeClasses('azf-row azf-wrap azf-toolbar', className)}>
      <div className="azf-row azf-wrap azf-gap-s">{title && <Text weight="semibold">{title}</Text>}{children}</div>
      {actions.length > 0 && <Toolbar aria-label={ariaLabel}>{actions.map((action) => <ToolbarButton key={action.id} icon={action.loading ? <Spinner size="tiny" /> : action.icon} disabled={action.disabled || action.loading} onClick={action.onClick}>{action.label}</ToolbarButton>)}</Toolbar>}
    </div>
  );
}

export interface FilterBarProps { filters: AzfFilter[]; searchValue?: string; onSearchChange?: (value: string) => void; searchPlaceholder?: string; children?: ReactNode; className?: string; }

export function FilterBar({ filters, searchValue, onSearchChange, searchPlaceholder = 'Search', children, className }: FilterBarProps) {
  return (
    <div className={mergeClasses('azf-row azf-wrap azf-filter-bar', className)}>
      {onSearchChange && <SearchBox value={searchValue} placeholder={searchPlaceholder} onChange={(_, data) => onSearchChange(data.value)} />}
      <div className="azf-row azf-wrap azf-gap-xs" aria-label="Selected filters">
        {filters.map((filter) => <span key={filter.id} className="azf-row azf-filter-pill"><Tag appearance={filter.selected ? 'brand' : 'filled'} disabled={filter.disabled}>{filter.label}{filter.value ? <>: {filter.value}</> : null}</Tag>{filter.removable && <Button size="small" appearance="subtle" icon={<DismissRegular />} aria-label={`Remove ${filter.label}`} disabled={filter.disabled} onClick={filter.onRemove} />}</span>)}
      </div>
      {children}
    </div>
  );
}

export interface AzureDataGridProps<T> {
  items: T[];
  columns: AzfColumn<T>[];
  getRowId?: (item: T, index: number) => string;
  selectedRowId?: string;
  onRowClick?: (item: T) => void;
  sortState?: AzfSortState;
  defaultSortState?: AzfSortState;
  onSortChange?: (sortState: AzfSortState) => void;
  loading?: boolean;
  error?: ReactNode;
  emptyState?: ReactNode;
  ariaLabel?: string;
  className?: string;
}

export function AzureDataGrid<T>({ items, columns, getRowId, selectedRowId, onRowClick, sortState, defaultSortState, onSortChange, loading, error, emptyState = 'No resources found.', ariaLabel = 'Data grid', className }: AzureDataGridProps<T>) {
  const [internalSort, setInternalSort] = useState<AzfSortState | undefined>(defaultSortState);
  const activeSort = sortState ?? internalSort;
  const sortedItems = useMemo(() => defaultSort(items, columns, activeSort), [items, columns, activeSort]);
  const setSort = (columnId: string) => {
    const next: AzfSortState = activeSort?.columnId === columnId && activeSort.direction === 'ascending' ? { columnId, direction: 'descending' } : { columnId, direction: 'ascending' };
    setInternalSort(next);
    onSortChange?.(next);
  };
  const colSpan = Math.max(1, columns.length);
  return (
    <div className={mergeClasses('azf-data-grid-shell', className)}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      <Table className="azf-data-grid" aria-label={ariaLabel} aria-busy={loading || undefined}>
        <TableHeader><TableRow>{columns.map((column) => {
          const sorted = activeSort?.columnId === column.columnId;
          return <TableHeaderCell key={column.columnId} style={{ width: column.width }} aria-sort={sorted ? activeSort.direction : undefined}>{column.sortable ? <Button appearance="transparent" iconPosition="after" icon={<ChevronDownRegular className={sorted && activeSort.direction === 'ascending' ? 'azf-sort-icon--ascending' : undefined} />} onClick={() => setSort(column.columnId)}>{column.header}</Button> : column.header}</TableHeaderCell>;
        })}</TableRow></TableHeader>
        <TableBody>
          {loading && <TableRow><TableCell colSpan={colSpan}><div className="azf-row azf-gap-s azf-data-grid__state"><Spinner size="tiny" /><Text>Loading</Text></div></TableCell></TableRow>}
          {!loading && sortedItems.length === 0 && <TableRow><TableCell colSpan={colSpan}><div className="azf-data-grid__state"><Text className="azf-muted">{emptyState}</Text></div></TableCell></TableRow>}
          {!loading && sortedItems.map((item, index) => {
            const rowId = getRowId?.(item, index) ?? String(index);
            const interactive = Boolean(onRowClick);
            const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => { if (interactive && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); onRowClick?.(item); } };
            return <TableRow key={rowId} aria-selected={rowId === selectedRowId} data-selected={rowId === selectedRowId || undefined} onClick={() => onRowClick?.(item)} onKeyDown={onKeyDown} tabIndex={interactive ? 0 : undefined} className={interactive ? 'azf-data-grid__row--interactive' : undefined}>{columns.map((column) => <TableCell key={column.columnId}>{column.renderCell(item)}</TableCell>)}</TableRow>;
          })}
        </TableBody>
      </Table>
    </div>
  );
}

export interface ResourceTagEditorProps {
  rows: AzfResourceTagRow[];
  resources: AzfOption[];
  onRowChange?: (rowId: string, patch: Partial<AzfResourceTagRow>) => void;
  onAddRow?: () => void;
  onDeleteRow?: (rowId: string) => void;
  validation?: Record<string, string>;
  disabled?: boolean;
  className?: string;
}

export function ResourceTagEditor({ rows, resources, onRowChange, onAddRow, onDeleteRow, validation = {}, disabled, className }: ResourceTagEditorProps) {
  return (
    <div className={mergeClasses('azf-stack azf-gap-s', className)}>
      <Table className="azf-resource-tags" aria-label="Resource tags">
        <TableHeader><TableRow><TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Value</TableHeaderCell><TableHeaderCell>Resource</TableHeaderCell><TableHeaderCell>Actions</TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {rows.map((row, index) => <TableRow key={row.id}>
            <TableCell><Field validationMessage={validation[`${row.id}:name`]} validationState={validation[`${row.id}:name`] ? 'error' : undefined}><Input value={row.name} disabled={disabled} onChange={(_, data) => onRowChange?.(row.id, { name: data.value })} aria-label={`Tag name for row ${index + 1}`} /></Field></TableCell>
            <TableCell><Field validationMessage={validation[`${row.id}:value`]} validationState={validation[`${row.id}:value`] ? 'error' : undefined}><Input value={row.value} disabled={disabled} onChange={(_, data) => onRowChange?.(row.id, { value: data.value })} aria-label={`Tag value for row ${index + 1}`} /></Field></TableCell>
            <TableCell><Field validationMessage={validation[`${row.id}:resourceId`]} validationState={validation[`${row.id}:resourceId`] ? 'error' : undefined}><Combobox disabled={disabled} selectedOptions={row.resourceId ? [row.resourceId] : []} value={optionText(resources.find((resource) => resource.id === row.resourceId)?.label ?? '')} onOptionSelect={(_, data) => onRowChange?.(row.id, { resourceId: data.optionValue })} aria-label={`Resource for row ${index + 1}`}>{resources.map((resource) => <Option key={resource.id} value={resource.id} text={optionText(resource.label)} disabled={resource.disabled}>{resource.icon}{resource.label}</Option>)}</Combobox></Field></TableCell>
            <TableCell><IconActionButton id={`delete-${row.id}`} label={`Delete tag row ${index + 1}`} icon={<DeleteRegular />} disabled={disabled} onClick={() => onDeleteRow?.(row.id)} /></TableCell>
          </TableRow>)}
        </TableBody>
      </Table>
      {onAddRow && <Button appearance="subtle" icon={<AddRegular />} disabled={disabled} onClick={onAddRow}>Add tag</Button>}
    </div>
  );
}

export interface FormFooterProps { primaryAction: AzfAction; secondaryAction?: AzfAction; feedback?: ReactNode; className?: string; }
export function FormFooter({ primaryAction, secondaryAction, feedback, className }: FormFooterProps) {
  return <footer className={mergeClasses('azf-row azf-form-footer', className)}><div className="azf-row azf-gap-s">{renderAction({ ...primaryAction, appearance: primaryAction.appearance ?? 'primary' })}{secondaryAction && renderAction({ ...secondaryAction, appearance: secondaryAction.appearance ?? 'secondary' })}</div>{feedback && <div className="azf-form-footer__feedback">{feedback}</div>}</footer>;
}

export interface PagerProps extends AzfPagerState { pageSizeOptions?: number[]; onPageChange?: (page: number) => void; onPageSizeChange?: (pageSize: number) => void; className?: string; }
export function Pager({ page, pageSize, totalItems, pageSizeOptions = [10, 25, 50], onPageChange, onPageSizeChange, className }: PagerProps) {
  const pageCount = Math.max(1, Math.ceil(totalItems / Math.max(1, pageSize)));
  const safePage = Math.min(Math.max(1, page), pageCount);
  const start = totalItems === 0 ? 0 : (safePage - 1) * pageSize + 1;
  const end = Math.min(totalItems, safePage * pageSize);
  return <nav className={mergeClasses('azf-row azf-pager', className)} aria-label="Pagination"><Text className="azf-muted" aria-live="polite">{start}-{end} of {totalItems}</Text><Combobox aria-label="Rows per page" selectedOptions={[String(pageSize)]} value={`${pageSize} / page`} onOptionSelect={(_, data) => data.optionValue && onPageSizeChange?.(Number(data.optionValue))}>{pageSizeOptions.map((size) => <Option key={size} value={String(size)} text={`${size} / page`}>{size} / page</Option>)}</Combobox><Button icon={<ChevronLeftRegular />} disabled={safePage <= 1} onClick={() => onPageChange?.(safePage - 1)}>Previous</Button><Text>{safePage} of {pageCount}</Text><Button icon={<ChevronRightRegular />} iconPosition="after" disabled={safePage >= pageCount} onClick={() => onPageChange?.(safePage + 1)}>Next</Button></nav>;
}
export interface CopilotComposerProps {
  value: string;
  onChange: (value: string) => void;
  onSend: () => void;
  isRunning?: boolean;
  onStop?: () => void;
  disabled?: boolean;
  agentMode?: boolean;
  onAgentModeChange?: (checked: boolean) => void;
  attachments?: AzfAttachment[];
  onAddAttachment?: () => void;
  validationMessage?: ReactNode;
  placeholder?: string;
  sendLabel?: string;
  stopLabel?: string;
  className?: string;
}

export function CopilotComposer({ value, onChange, onSend, isRunning, onStop, disabled, agentMode, onAgentModeChange, attachments = [], onAddAttachment, validationMessage, placeholder = 'Ask Copilot', sendLabel = 'Send', stopLabel = 'Stop response', className }: CopilotComposerProps) {
  const canSend = Boolean(value.trim()) && !disabled && !isRunning;
  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => { if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && canSend) onSend(); };
  return (
    <div className={mergeClasses('azf-stack azf-copilot-composer', className)} aria-busy={isRunning || undefined}>
      {validationMessage && <MessageBar intent="warning"><MessageBarBody>{validationMessage}</MessageBarBody></MessageBar>}
      <Textarea value={value} disabled={disabled} onChange={(_, data) => onChange(data.value)} onKeyDown={handleKeyDown} placeholder={placeholder} aria-label={placeholder} resize="vertical" />
      {attachments.length > 0 && <div className="azf-row azf-wrap azf-gap-xs" aria-label="Attachments">{attachments.map((attachment) => <Tag key={attachment.id}>{attachment.name}{attachment.onRemove && <Button appearance="transparent" size="small" icon={<DismissRegular />} aria-label={`Remove ${attachment.name}`} onClick={attachment.onRemove} />}</Tag>)}</div>}
      <div className="azf-row azf-copilot-composer__footer"><div className="azf-row azf-gap-xs">{onAddAttachment && <Button appearance="subtle" icon={<AddRegular />} disabled={disabled || isRunning} onClick={onAddAttachment}>Add</Button>}{onAgentModeChange && <ToggleButton checked={agentMode} disabled={disabled || isRunning} onClick={() => onAgentModeChange(!agentMode)}>Agent mode</ToggleButton>}</div><Button appearance="primary" shape="circular" icon={isRunning ? <StopRegular /> : <ArrowRightRegular />} aria-label={isRunning ? stopLabel : sendLabel} onClick={isRunning ? onStop : onSend} disabled={isRunning ? !onStop : !canSend} /></div>
    </div>
  );
}

function ChoicePart({ part }: { part: Extract<AzfResponsePart, { type: 'choices' }> }) {
  const [selected, setSelected] = useState<string[]>([]);
  const toggle = (id: string, checked: boolean) => setSelected((current) => checked ? [...current, id] : current.filter((value) => value !== id));
  return <Field label={optionText(part.label)} className="azf-response-choice">{part.multiple ? part.choices.map((choice) => <Checkbox key={choice.id} label={optionText(choice.label)} disabled={choice.disabled} checked={selected.includes(choice.id)} onChange={(_, data) => toggle(choice.id, Boolean(data.checked))} />) : <RadioGroup value={selected[0]} onChange={(_, data) => setSelected([data.value])}>{part.choices.map((choice) => <Radio key={choice.id} value={choice.id} label={optionText(choice.label)} disabled={choice.disabled} />)}</RadioGroup>}<Button appearance="primary" disabled={selected.length === 0} onClick={() => part.onSubmit?.(selected)}>{part.submitLabel ?? 'Submit'}</Button></Field>;
}

export interface CopilotResponseProps { parts: AzfResponsePart[]; actions?: AzfAction[]; loading?: boolean; error?: ReactNode; className?: string; }
export function CopilotResponse({ parts, actions = [], loading, error, className }: CopilotResponseProps) {
  return <div className={mergeClasses('azf-stack azf-gap-s', className)}>{error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}{parts.map((part) => { if (part.type === 'text') return <div key={part.id} className="azf-chat-bubble" data-author="assistant"><Text>{part.content}</Text></div>; if (part.type === 'confirmation') return <MessageBar key={part.id} intent="warning"><MessageBarBody><div className="azf-stack azf-gap-s">{part.content}<div className="azf-row azf-gap-s"><Button appearance="primary" onClick={part.onConfirm}>{part.confirmLabel}</Button>{part.cancelLabel && <Button onClick={part.onCancel}>{part.cancelLabel}</Button>}</div></div></MessageBarBody></MessageBar>; return <ChoicePart key={part.id} part={part} />; })}{loading && <div className="azf-row azf-gap-s"><Spinner size="tiny" /><Text className="azf-muted">Generating response</Text></div>}<div className="azf-row azf-response-actions">{actions.length ? actions.map((action) => renderAction(action)) : <><IconActionButton id="like" label="Helpful" icon={<ThumbLikeRegular />} /><IconActionButton id="dislike" label="Not helpful" icon={<ThumbDislikeRegular />} /></>}</div></div>;
}

export interface InlineCopilotProps { open: boolean; trigger: ReactElement; value: string; onChange: (value: string) => void; onSubmit: () => void; onDismiss?: () => void; state?: 'empty' | 'loading' | 'error' | 'generated'; errorMessage?: ReactNode; suggestions?: AzfOption[]; }
export function InlineCopilot({ open, trigger, value, onChange, onSubmit, onDismiss, state = 'empty', errorMessage, suggestions = [] }: InlineCopilotProps) {
  const titleId = useId();
  return <Popover open={open} withArrow positioning="below-start"><PopoverTrigger disableButtonEnhancement>{trigger}</PopoverTrigger><PopoverSurface className="azf-stack azf-inline-copilot" aria-labelledby={titleId}><div className="azf-row azf-inline-copilot__header"><div className="azf-row azf-gap-s"><SparkleRegular /><Text id={titleId} weight="semibold">Copilot</Text></div>{onDismiss && <Button appearance="subtle" icon={<DismissRegular />} aria-label="Dismiss" onClick={onDismiss} />}</div>{errorMessage && <MessageBar intent="error"><MessageBarBody>{errorMessage}</MessageBarBody></MessageBar>}<Textarea value={value} disabled={state === 'loading'} onChange={(_, data) => onChange(data.value)} aria-label="Inline Copilot prompt" />{suggestions.length > 0 && <div className="azf-row azf-wrap azf-gap-xs" aria-label="Prompt suggestions">{suggestions.map((suggestion) => <Button key={suggestion.id} size="small" disabled={suggestion.disabled} onClick={() => onChange(optionText(suggestion.label))}>{suggestion.label}</Button>)}</div>}<Button appearance="primary" icon={state === 'loading' ? <Spinner size="tiny" /> : <SparkleRegular />} disabled={state === 'loading' || !value.trim()} onClick={onSubmit}>{state === 'loading' ? 'Generating' : 'Generate'}</Button></PopoverSurface></Popover>;
}

export interface ArtifactPillProps { artifact: AzfArtifact; }
export function ArtifactPill({ artifact }: ArtifactPillProps) { return <button type="button" className="azf-row azf-gap-s azf-artifact-pill" onClick={artifact.onOpen}>{artifact.icon ?? <DocumentRegular />}<span>{artifact.title}</span>{artifact.type && <Text className="azf-muted">{artifact.type}</Text>}</button>; }

export interface AgenticProgressProps { steps: AzfAgentStep[]; onApprove?: (stepId: string) => void; onDeny?: (stepId: string) => void; defaultOpenItems?: string[]; className?: string; }
function stepTone(step: AzfAgentStep): AzfTone { if (step.status === 'complete') return 'success'; if (step.status === 'warning' || step.needsInput) return 'warning'; if (step.status === 'error' || step.status === 'blocked') return 'danger'; return 'info'; }
export function AgenticProgress({ steps, onApprove, onDeny, defaultOpenItems, className }: AgenticProgressProps) {
  return <Accordion multiple collapsible defaultOpenItems={defaultOpenItems} className={mergeClasses('azf-stack azf-agentic-list', className)}>{steps.map((step) => <AccordionItem key={step.id} value={step.id}><AccordionHeader icon={step.status === 'running' ? <Spinner size="tiny" /> : <StatusIconText status={stepTone(step)} />}>{step.title}</AccordionHeader><AccordionPanel><div className="azf-stack azf-gap-s">{step.status === 'running' && <ProgressBar thickness="medium" />}{step.body && <Text>{step.body}</Text>}{step.needsInput && <MessageBar intent="warning"><MessageBarBody><div className="azf-stack azf-gap-s">{step.riskText}<div className="azf-row azf-gap-s"><Button appearance="primary" onClick={() => onApprove?.(step.id)}>Approve</Button><Button onClick={() => onDeny?.(step.id)}>Deny</Button></div></div></MessageBarBody></MessageBar>}{step.artifacts?.map((artifact) => <ArtifactPill key={artifact.id} artifact={artifact} />)}</div></AccordionPanel></AccordionItem>)}</Accordion>;
}

export interface AzureTab { id: string; label: ReactNode; icon?: ReactElement; status?: 'error' | 'success'; disabled?: boolean; }
export interface AzureTabListProps { tabs: AzureTab[]; selectedValue: string; onTabSelect?: (value: string) => void; orientation?: 'horizontal' | 'vertical'; className?: string; }
export function AzureTabList({ tabs, selectedValue, onTabSelect, orientation = 'horizontal', className }: AzureTabListProps) { return <TabList className={mergeClasses('azf-tabs', className)} vertical={orientation === 'vertical'} selectedValue={selectedValue} onTabSelect={(_, data) => onTabSelect?.(String(data.value))}>{tabs.map((tab) => <Tab key={tab.id} value={tab.id} disabled={tab.disabled} icon={tab.icon ?? (tab.status === 'error' ? <ErrorCircleRegular /> : tab.status === 'success' ? <CheckmarkCircleRegular /> : undefined)}>{optionText(tab.label)}</Tab>)}</TabList>; }

export interface HelpPopoverProps { trigger: ReactElement; title?: ReactNode; body: ReactNode; actions?: AzfAction[]; tone?: 'light' | 'brand' | 'dark'; }
export function HelpPopover({ trigger, title, body, actions = [], tone = 'light' }: HelpPopoverProps) { const titleId = useId(); return <Popover withArrow><PopoverTrigger disableButtonEnhancement>{trigger}</PopoverTrigger><PopoverSurface className="azf-stack azf-popover-content" data-tone={tone} aria-labelledby={title ? titleId : undefined}>{title && <Text id={titleId} weight="semibold">{title}</Text>}<Text>{body}</Text>{actions.length > 0 && <div className="azf-row azf-gap-s">{actions.map((action) => renderAction(action))}</div>}</PopoverSurface></Popover>; }
export const CalloutPopover = HelpPopover;

export interface AzureFormProps { children: ReactNode; message?: ReactNode; footer?: ReactNode; className?: string; onSubmit?: () => void; }
export function AzureForm({ children, message, footer, className, onSubmit }: AzureFormProps) { return <form className={mergeClasses('azf-stack azf-gap-m', className)} onSubmit={(event) => { event.preventDefault(); onSubmit?.(); }}>{message && <MessageBar><MessageBarBody>{message}</MessageBarBody></MessageBar>}{children}{footer}</form>; }

export { Button, Card, Field, Input, Link, MessageBar, ProgressBar, Text };
