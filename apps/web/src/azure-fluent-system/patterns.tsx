import type { ReactElement, ReactNode } from 'react';
import { Button, Card, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, DialogTrigger, MessageBar, MessageBarBody, Text, mergeClasses } from '@fluentui/react-components';
import { DeleteRegular, SparkleRegular, WarningRegular } from '@fluentui/react-icons';
import './tokens.css';
import type { AzfAction, AzfColumn, AzfFilter, AzfPagerState, AzfServiceMenuGroup } from './types';
import { AzureDataGrid, BladeHeader, CopilotComposer, CopilotResponse, DataToolbar, FilterBar, FormFooter, Pager, ServiceMenu, StatusIconText, type CopilotComposerProps, type CopilotResponseProps } from './components';

export interface BrowseResourcePatternProps<T> {
  title: ReactNode;
  subtitle?: ReactNode;
  items: T[];
  columns: AzfColumn<T>[];
  filters?: AzfFilter[];
  toolbarActions?: AzfAction[];
  headerActions?: AzfAction[];
  pager?: AzfPagerState;
  loading?: boolean;
  error?: ReactNode;
  emptyState?: ReactNode;
  onPageChange?: (page: number) => void;
  onPageSizeChange?: (pageSize: number) => void;
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  className?: string;
}

export function BrowseResourcePattern<T>({ title, subtitle, items, columns, filters = [], toolbarActions = [], headerActions = [], pager, loading, error, emptyState, onPageChange, onPageSizeChange, searchValue, onSearchChange, className }: BrowseResourcePatternProps<T>) {
  return <section className={mergeClasses('azf-stack azf-pattern-shell', className)}><BladeHeader title={title} subtitle={subtitle} actions={headerActions} loading={loading} /><div><DataToolbar actions={toolbarActions} /><FilterBar filters={filters} searchValue={searchValue} onSearchChange={onSearchChange} /><AzureDataGrid items={items} columns={columns} loading={loading} error={error} emptyState={emptyState} />{pager && <Pager {...pager} onPageChange={onPageChange} onPageSizeChange={onPageSizeChange} />}</div></section>;
}

export interface FilteringPatternProps<T> extends Omit<BrowseResourcePatternProps<T>, 'title'> { title?: ReactNode; }
export function FilteringPattern<T>({ title = 'Resources', ...props }: FilteringPatternProps<T>) { return <BrowseResourcePattern {...props} title={title} />; }

export interface ManageResourcePatternProps { header: ReactNode; serviceMenu?: ReactNode; children: ReactNode; className?: string; }
export function ManageResourcePattern({ header, serviceMenu, children, className }: ManageResourcePatternProps) { return <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>{header}<div className="azf-pattern-grid">{serviceMenu}{children}</div></section>; }

export interface FormBladePatternProps { title: ReactNode; subtitle?: ReactNode; children: ReactNode; primaryAction: AzfAction; secondaryAction?: AzfAction; feedback?: ReactNode; message?: ReactNode; className?: string; }
export function FormBladePattern({ title, subtitle, children, primaryAction, secondaryAction, feedback, message, className }: FormBladePatternProps) { return <section className={mergeClasses('azf-stack azf-pattern-shell', className)}><BladeHeader title={title} subtitle={subtitle} />{message && <MessageBar><MessageBarBody>{message}</MessageBarBody></MessageBar>}<div className="azf-stack azf-gap-m">{children}</div><FormFooter primaryAction={primaryAction} secondaryAction={secondaryAction} feedback={feedback} /></section>; }

export interface StepWizardPatternProps { title: ReactNode; subtitle?: ReactNode; steps: Array<{ id: string; label: ReactNode; content: ReactNode; disabled?: boolean }>; currentStepId: string; onStepSelect?: (stepId: string) => void; primaryAction: AzfAction; secondaryAction?: AzfAction; className?: string; }
export function StepWizardPattern({ title, subtitle, steps, currentStepId, onStepSelect, primaryAction, secondaryAction, className }: StepWizardPatternProps) { const current = steps.find((step) => step.id === currentStepId) ?? steps[0]; return <FormBladePattern title={title} subtitle={subtitle} primaryAction={primaryAction} secondaryAction={secondaryAction} className={className}><div className="azf-row azf-wrap azf-gap-s" aria-label="Steps">{steps.map((step) => <Button key={step.id} disabled={step.disabled} appearance={step.id === currentStepId ? 'primary' : 'subtle'} onClick={() => onStepSelect?.(step.id)}>{step.label}</Button>)}</div>{current?.content}</FormBladePattern>; }


export interface CreateResourcePatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  steps: Array<{ id: string; label: ReactNode; content: ReactNode; disabled?: boolean }>;
  currentStepId: string;
  primaryAction: AzfAction;
  secondaryAction?: AzfAction;
  onStepSelect?: (stepId: string) => void;
  validationSummary?: ReactNode;
  reviewContent?: ReactNode;
  feedback?: ReactNode;
  className?: string;
}

export function CreateResourcePattern({ title, subtitle, steps, currentStepId, primaryAction, secondaryAction, onStepSelect, validationSummary, reviewContent, feedback, className }: CreateResourcePatternProps) {
  const current = steps.find((step) => step.id === currentStepId) ?? steps[0];
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)} data-provenance="derived-from-related-patterns">
      <BladeHeader title={title} subtitle={subtitle} />
      {validationSummary && <MessageBar intent="error"><MessageBarBody>{validationSummary}</MessageBarBody></MessageBar>}
      <div className="azf-row azf-wrap azf-gap-s" aria-label="Create resource steps">
        {steps.map((step) => <Button key={step.id} disabled={step.disabled} appearance={step.id === currentStepId ? 'primary' : 'subtle'} onClick={() => onStepSelect?.(step.id)}>{step.label}</Button>)}
      </div>
      <div className="azf-stack azf-gap-m">{current?.content}{reviewContent}</div>
      <FormFooter primaryAction={primaryAction} secondaryAction={secondaryAction} feedback={feedback} />
    </section>
  );
}
export interface DeleteResourceDialogProps { resourceName: string; trigger: ReactElement; softDelete?: boolean; confirmationText?: ReactNode; confirmLabel?: string; cancelLabel?: string; confirming?: boolean; onConfirm?: () => void; onCancel?: () => void; }
export function DeleteResourceDialog({ resourceName, trigger, softDelete, confirmationText, confirmLabel = 'Delete', cancelLabel = 'Cancel', confirming, onConfirm, onCancel }: DeleteResourceDialogProps) { return <Dialog><DialogTrigger disableButtonEnhancement>{trigger}</DialogTrigger><DialogSurface><DialogBody><DialogTitle>Delete {resourceName}</DialogTitle><DialogContent className="azf-stack azf-gap-s"><StatusIconText status="danger" icon={<WarningRegular />}>{softDelete ? 'This resource can be recovered for a limited time.' : 'This action may permanently remove the resource.'}</StatusIconText><Text>{confirmationText ?? 'Review dependencies and saved work before continuing.'}</Text></DialogContent><DialogActions><Button appearance="primary" icon={<DeleteRegular />} disabled={confirming} onClick={onConfirm}>{confirmLabel}</Button><DialogTrigger disableButtonEnhancement><Button disabled={confirming} onClick={onCancel}>{cancelLabel}</Button></DialogTrigger></DialogActions></DialogBody></DialogSurface></Dialog>; }

export interface ErrorPatternProps { title: ReactNode; body: ReactNode; severity?: 'error' | 'warning' | 'info'; actions?: ReactNode; }
export function ErrorPattern({ title, body, severity = 'error', actions }: ErrorPatternProps) { return <MessageBar intent={severity}><MessageBarBody><Text weight="semibold">{title}</Text><div>{body}</div>{actions}</MessageBarBody></MessageBar>; }
export interface NotificationPatternProps { title: ReactNode; body?: ReactNode; actions?: ReactNode; intent?: 'info' | 'success' | 'warning' | 'error'; }
export function NotificationPattern({ title, body, actions, intent = 'info' }: NotificationPatternProps) { return <MessageBar intent={intent}><MessageBarBody><Text weight="semibold">{title}</Text>{body && <div>{body}</div>}{actions}</MessageBarBody></MessageBar>; }

export interface ServiceOverviewPatternProps { title: ReactNode; subtitle?: ReactNode; overviewCards: Array<{ id: string; title: ReactNode; body?: ReactNode; actions?: ReactNode }>; primaryAction?: AzfAction; secondaryAction?: AzfAction; className?: string; }
export function ServiceOverviewPattern({ title, subtitle, overviewCards, primaryAction, secondaryAction, className }: ServiceOverviewPatternProps) { return <section className={mergeClasses('azf-stack azf-pattern-shell', className)}><BladeHeader title={title} subtitle={subtitle} actions={[primaryAction, secondaryAction].filter((action): action is AzfAction => Boolean(action))} /><div className="azf-overview-grid">{overviewCards.map((card) => <Card key={card.id}><Text weight="semibold">{card.title}</Text>{card.body && <Text className="azf-muted">{card.body}</Text>}{card.actions}</Card>)}</div></section>; }

export interface CopilotWorkspacePatternProps { title: ReactNode; serviceMenuGroups?: AzfServiceMenuGroup[]; composer: CopilotComposerProps; response?: CopilotResponseProps; selectedMenuId?: string; onMenuSelect?: (id: string) => void; className?: string; }
export function CopilotWorkspacePattern({ title, serviceMenuGroups = [], composer, response, selectedMenuId, onMenuSelect, className }: CopilotWorkspacePatternProps) { return <section className={mergeClasses('azf-stack azf-pattern-shell', className)}><BladeHeader title={title} resourceIcon={<SparkleRegular />} /> <div className="azf-pattern-grid">{serviceMenuGroups.length > 0 && <ServiceMenu groups={serviceMenuGroups} selectedId={selectedMenuId} onSelect={onMenuSelect} />}<div className="azf-stack azf-gap-m">{response && <CopilotResponse {...response} />}<CopilotComposer {...composer} /></div></div></section>; }
