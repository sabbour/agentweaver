/** Thin page compositions that combine system primitives into browse, form, Copilot, and approval flows. */
import type { ReactNode } from 'react';
import {
  Card,
  MessageBar,
  MessageBarBody,
  Text,
  mergeClasses,
} from '@fluentui/react-components';
import { SparkleRegular } from '@fluentui/react-icons';
import './tokens.css';
import type { AzfAction, AzfAgentStep, AzfColumn, AzfFilter, AzfPagerState, AzfServiceMenuGroup } from './types';
import {
  AgenticProgress,
  AzureDataGrid,
  AzureStepList,
  BladeHeader,
  ChainOfThought,
  CopilotComposer,
  CopilotResponse,
  DataToolbar,
  FilterBar,
  FormFooter,
  Pager,
  ServiceMenu,
  type ChainOfThoughtProps,
  type CopilotComposerProps,
  type CopilotResponseProps,
} from './components';
/** Props for the header, filter, grid, and optional paging composition used on resource lists. */
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

/** Composes a resource header, toolbar, filters, data grid, and optional pager. */
export function BrowseResourcePattern<T>({
  title,
  subtitle,
  items,
  columns,
  filters = [],
  toolbarActions = [],
  headerActions = [],
  pager,
  loading,
  error,
  emptyState,
  onPageChange,
  onPageSizeChange,
  searchValue,
  onSearchChange,
  className,
}: BrowseResourcePatternProps<T>) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>
      <BladeHeader title={title} subtitle={subtitle} actions={headerActions} loading={loading} />
      <div>
        <DataToolbar actions={toolbarActions} />
        <FilterBar filters={filters} searchValue={searchValue} onSearchChange={onSearchChange} />
        <AzureDataGrid items={items} columns={columns} loading={loading} error={error} emptyState={emptyState} />
        {pager && <Pager {...pager} onPageChange={onPageChange} onPageSizeChange={onPageSizeChange} />}
      </div>
    </section>
  );
}

/** Browse pattern props with an optional default title. */
export interface FilteringPatternProps<T> extends Omit<BrowseResourcePatternProps<T>, 'title'> {
  title?: ReactNode;
}

/** Convenience wrapper for a browsable list when no explicit title is provided. */
export function FilteringPattern<T>({ title = 'Resources', ...props }: FilteringPatternProps<T>) {
  return <BrowseResourcePattern {...props} title={title} />;
}

/** Header, optional service navigation, and host-owned content for a resource-management page. */
export interface ManageResourcePatternProps {
  header: ReactNode;
  serviceMenu?: ReactNode;
  children: ReactNode;
  className?: string;
}

/** Lays out a management header beside its optional service menu and page content. */
export function ManageResourcePattern({ header, serviceMenu, children, className }: ManageResourcePatternProps) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>
      {header}
      <div className="azf-pattern-grid">
        {serviceMenu}
        {children}
      </div>
    </section>
  );
}

/** One selectable step and its content in a create or form workflow. */
export interface StepWizardPatternStep {
  id: string;
  label: ReactNode;
  content: ReactNode;
  disabled?: boolean;
  description?: ReactNode;
  status?: 'default' | 'complete' | 'warning' | 'error';
}

/** Shared header, fields, message, and action footer for a form blade. */
export interface FormBladePatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  children: ReactNode;
  primaryAction: AzfAction;
  secondaryAction?: AzfAction;
  feedback?: ReactNode;
  message?: ReactNode;
  className?: string;
}

/** Renders a titled form surface with optional message and standard action footer. */
export function FormBladePattern({
  title,
  subtitle,
  children,
  primaryAction,
  secondaryAction,
  feedback,
  message,
  className,
}: FormBladePatternProps) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>
      <BladeHeader title={title} subtitle={subtitle} />
      {message && <MessageBar><MessageBarBody>{message}</MessageBarBody></MessageBar>}
      <div className="azf-stack azf-gap-m">{children}</div>
      <FormFooter primaryAction={primaryAction} secondaryAction={secondaryAction} feedback={feedback} />
    </section>
  );
}

/** Wizard state and actions for a multi-step form using the shared form blade. */
export interface StepWizardPatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  steps: StepWizardPatternStep[];
  currentStepId: string;
  onStepSelect?: (stepId: string) => void;
  primaryAction: AzfAction;
  secondaryAction?: AzfAction;
  message?: ReactNode;
  feedback?: ReactNode;
  className?: string;
}

/** Selects the current wizard step and places its content inside the common form shell. */
export function StepWizardPattern({
  title,
  subtitle,
  steps,
  currentStepId,
  onStepSelect,
  primaryAction,
  secondaryAction,
  message,
  feedback,
  className,
}: StepWizardPatternProps) {
  const current = steps.find((step) => step.id === currentStepId) ?? steps[0];

  return (
    <FormBladePattern
      title={title}
      subtitle={subtitle}
      primaryAction={primaryAction}
      secondaryAction={secondaryAction}
      feedback={feedback}
      className={className}
    >
      <AzureStepList
        steps={steps.map(({ id, label, description, disabled, status }) => ({ id, label, description, disabled, status }))}
        selectedValue={currentStepId}
        onStepSelect={onStepSelect}
      />
      {message && <MessageBar><MessageBarBody>{message}</MessageBarBody></MessageBar>}
      {current?.content}
    </FormBladePattern>
  );
}

/** Inputs for a staged resource-creation flow with validation and optional review content. */
export interface CreateResourcePatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  steps: StepWizardPatternStep[];
  currentStepId: string;
  primaryAction: AzfAction;
  secondaryAction?: AzfAction;
  onStepSelect?: (stepId: string) => void;
  validationSummary?: ReactNode;
  reviewContent?: ReactNode;
  feedback?: ReactNode;
  className?: string;
}

/** Composes step navigation, validation, current-step content, review, and final actions. */
export function CreateResourcePattern({
  title,
  subtitle,
  steps,
  currentStepId,
  primaryAction,
  secondaryAction,
  onStepSelect,
  validationSummary,
  reviewContent,
  feedback,
  className,
}: CreateResourcePatternProps) {
  const current = steps.find((step) => step.id === currentStepId) ?? steps[0];

  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>
      <BladeHeader title={title} subtitle={subtitle} />
      {validationSummary && <MessageBar intent="error"><MessageBarBody>{validationSummary}</MessageBarBody></MessageBar>}
      <AzureStepList
        steps={steps.map(({ id, label, description, disabled, status }) => ({ id, label, description, disabled, status }))}
        selectedValue={currentStepId}
        onStepSelect={onStepSelect}
      />
      <div className="azf-stack azf-gap-m">
        {current?.content}
        {reviewContent}
      </div>
      <FormFooter primaryAction={primaryAction} secondaryAction={secondaryAction} feedback={feedback} />
    </section>
  );
}

/** Plain message content and optional actions for an error or warning state. */
export interface ErrorPatternProps {
  title: ReactNode;
  body: ReactNode;
  severity?: 'error' | 'warning' | 'info';
  actions?: ReactNode;
}

/** Renders an inline message bar with severity, explanatory text, and optional actions. */
export function ErrorPattern({ title, body, severity = 'error', actions }: ErrorPatternProps) {
  return (
    <MessageBar intent={severity}>
      <MessageBarBody>
        <Text weight="semibold">{title}</Text>
        <div>{body}</div>
        {actions}
      </MessageBarBody>
    </MessageBar>
  );
}

/** Content for an inline informational or success notification. */
export interface NotificationPatternProps {
  title: ReactNode;
  body?: ReactNode;
  actions?: ReactNode;
  intent?: 'info' | 'success' | 'warning' | 'error';
}

/** Renders a compact message bar for a status update and its next action. */
export function NotificationPattern({ title, body, actions, intent = 'info' }: NotificationPatternProps) {
  return (
    <MessageBar intent={intent}>
      <MessageBarBody>
        <Text weight="semibold">{title}</Text>
        {body && <div>{body}</div>}
        {actions}
      </MessageBarBody>
    </MessageBar>
  );
}

/** Overview cards and header commands for a service landing surface. */
export interface ServiceOverviewPatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  overviewCards: Array<{ id: string; title: ReactNode; body?: ReactNode; actions?: ReactNode }>;
  primaryAction?: AzfAction;
  secondaryAction?: AzfAction;
  className?: string;
}

/** Displays a service header with primary and secondary actions above overview cards. */
export function ServiceOverviewPattern({
  title,
  subtitle,
  overviewCards,
  primaryAction,
  secondaryAction,
  className,
}: ServiceOverviewPatternProps) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>
      <BladeHeader
        title={title}
        subtitle={subtitle}
        actions={[primaryAction, secondaryAction].filter((action): action is AzfAction => Boolean(action))}
      />
      <div className="azf-overview-grid">
        {overviewCards.map((card) => (
          <Card key={card.id}>
            <Text weight="semibold">{card.title}</Text>
            {card.body && <Text className="azf-muted">{card.body}</Text>}
            {card.actions}
          </Card>
        ))}
      </div>
    </section>
  );
}

/** Inputs for a workspace that places optional service navigation beside Copilot content. */
export interface CopilotWorkspacePatternProps {
  title: ReactNode;
  serviceMenuGroups?: AzfServiceMenuGroup[];
  composer: CopilotComposerProps;
  response?: CopilotResponseProps;
  selectedMenuId?: string;
  onMenuSelect?: (id: string) => void;
  className?: string;
}

/** Composes optional service navigation, response history, and a Copilot composer. */
export function CopilotWorkspacePattern({
  title,
  serviceMenuGroups = [],
  composer,
  response,
  selectedMenuId,
  onMenuSelect,
  className,
}: CopilotWorkspacePatternProps) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell', className)}>
      <BladeHeader title={title} resourceIcon={<SparkleRegular />} />
      <div className="azf-pattern-grid">
        {serviceMenuGroups.length > 0 && (
          <ServiceMenu groups={serviceMenuGroups} selectedId={selectedMenuId} onSelect={onMenuSelect} />
        )}
        <div className="azf-stack azf-gap-m">
          {response && <CopilotResponse {...response} />}
          <CopilotComposer {...composer} />
        </div>
      </div>
    </section>
  );
}

// Composed scenario — Coordinator run workspace.
// This is a library-authored composition (NOT a single Figma node): it maps the real
// Agentweaver CoordinatorRunPage (run reasoning stream + run summary + operator steering)
// onto already-MCP-grounded Copilot primitives. Constituent fidelity is inherited from:
//   BladeHeader      node 32615:9834  (file q2TdO4dVcMhNWYp0N6Bc05)
//   ChainOfThought   node 386:75088   (file oqjy7GlpGqEQgUwMCs1wdq; sub-row 386:75111)
//   CopilotResponse  node 32382:38129 (file oqjy7GlpGqEQgUwMCs1wdq)
//   CopilotComposer  node 32382:38468 (file oqjy7GlpGqEQgUwMCs1wdq)
/** Run review surface that pairs step reasoning with response and steering controls. */
export interface CoordinatorRunPatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  runActions?: AzfAction[];
  copilotActions?: AzfAction[];
  reasoning: ChainOfThoughtProps;
  response?: CopilotResponseProps;
  composer: CopilotComposerProps;
  /** Optional app-owned header slot for stateful run surfaces that need custom orchestration chrome. */
  header?: ReactNode;
  /** Optional app-owned body slot; when omitted the default Copilot run composition is rendered. */
  children?: ReactNode;
  testId?: string;
  className?: string;
}

/** Composes an automation run header and body, with slots for host-owned chrome or content. */
export function CoordinatorRunPattern({
  title,
  subtitle,
  runActions,
  copilotActions,
  reasoning,
  response,
  composer,
  header,
  children,
  testId,
  className,
}: CoordinatorRunPatternProps) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell azf-coordinator-run', className)} data-testid={testId}>
      {header ?? (
        <BladeHeader
          title={title}
          subtitle={subtitle}
          resourceIcon={<SparkleRegular />}
          actions={runActions}
          copilotActions={copilotActions}
        />
      )}
      {children ?? (
        <div className="azf-coordinator-run__body">
          <ChainOfThought {...reasoning} />
          <aside className="azf-stack azf-gap-m azf-coordinator-run__aside">
            {response && <CopilotResponse {...response} />}
            <CopilotComposer {...composer} />
          </aside>
        </div>
      )}
    </section>
  );
}

// Composed scenario — Agentic approval checkpoint.
// Library-authored composition mapping the CoordinatorRunPage human-in-the-loop approval gate
// (AutomationToggle + approval card) onto the MCP-grounded AgenticProgress primitive.
//   AgenticProgress  nodes 27950:10571 / 27880:13472 (file q2TdO4dVcMhNWYp0N6Bc05)
//   Running loader   node 386:75129 (shared Copilot loader)
//   ArtifactPill     node 27865:11293
/** Approval summary and step data for a human checkpoint in an agent workflow. */
export interface AgenticApprovalPatternProps {
  title: ReactNode;
  summary?: ReactNode;
  steps: AzfAgentStep[];
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
  defaultOpenItems?: string[];
  className?: string;
}

/** Presents approval steps and wires approve or deny actions back to the host. */
export function AgenticApprovalPattern({
  title,
  summary,
  steps,
  onApprove,
  onDeny,
  defaultOpenItems,
  className,
}: AgenticApprovalPatternProps) {
  return (
    <Card className={mergeClasses('azf-stack azf-gap-m azf-agentic-approval', className)}>
      <div className="azf-stack azf-gap-xs">
        <Text weight="semibold">{title}</Text>
        {summary && <Text className="azf-muted">{summary}</Text>}
      </div>
      <AgenticProgress steps={steps} onApprove={onApprove} onDeny={onDeny} defaultOpenItems={defaultOpenItems} />
    </Card>
  );
}

/** Response, optional composer, progress steps, and actions for a Copilot triage panel. */
export interface CopilotTriagePanelPatternProps {
  title: ReactNode;
  summary?: ReactNode;
  response: CopilotResponseProps;
  composer?: CopilotComposerProps;
  steps?: AzfAgentStep[];
  actions?: AzfAction[];
  className?: string;
}

/** Combines progress, generated response, and optional operator input in a triage card. */
export function CopilotTriagePanelPattern({
  title,
  summary,
  response,
  composer,
  steps,
  actions,
  className,
}: CopilotTriagePanelPatternProps) {
  return (
    <Card className={mergeClasses('azf-stack azf-gap-m azf-copilot-triage', className)}>
      <div className="azf-stack azf-gap-xs">
        <Text weight="semibold">{title}</Text>
        {summary && <Text className="azf-muted">{summary}</Text>}
      </div>
      {actions && actions.length > 0 && <DataToolbar actions={actions} />}
      {steps && steps.length > 0 && <AgenticProgress steps={steps} />}
      <CopilotResponse {...response} />
      {composer && <CopilotComposer {...composer} />}
    </Card>
  );
}

/** Header, status summaries, and optional commands for a resource operation surface. */
export interface ResourceOperationHeaderPatternProps {
  title: ReactNode;
  subtitle?: ReactNode;
  resourceIcon?: ReactNode;
  actions?: AzfAction[];
  commandActions?: AzfAction[];
  statusItems?: Array<{ id: string; title: ReactNode; body?: ReactNode }>;
  children?: ReactNode;
  className?: string;
}

/** Renders a resource header followed by optional commands, status cards, and host content. */
export function ResourceOperationHeaderPattern({
  title,
  subtitle,
  resourceIcon,
  actions,
  commandActions,
  statusItems = [],
  children,
  className,
}: ResourceOperationHeaderPatternProps) {
  return (
    <section className={mergeClasses('azf-stack azf-pattern-shell azf-resource-operation-header', className)}>
      <BladeHeader title={title} subtitle={subtitle} resourceIcon={resourceIcon} actions={actions} />
      {commandActions && commandActions.length > 0 && <DataToolbar actions={commandActions} />}
      {statusItems.length > 0 && (
        <div className="azf-overview-grid">
          {statusItems.map((item) => (
            <Card key={item.id}>
              <Text weight="semibold">{item.title}</Text>
              {item.body && <Text className="azf-muted">{item.body}</Text>}
            </Card>
          ))}
        </div>
      )}
      {children}
    </section>
  );
}
