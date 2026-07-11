import type { KeyboardEvent, ReactElement, ReactNode } from 'react';
import { cloneElement, Fragment, useEffect, useId, useMemo, useRef, useState } from 'react';
import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Badge,
  Button,
  Card,
  Checkbox,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  InfoLabel,
  Input,
  Label,
  Link,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Option,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  ProgressBar,
  Radio,
  RadioGroup,
  SearchBox,
  Slider,
  Spinner,
  Tab,
  TabList,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Tag,
  Text,
  Textarea,
  ToggleButton,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  Tooltip,
  mergeClasses,
} from '@fluentui/react-components';
import type { ButtonProps, CheckboxProps } from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  ArrowMaximizeRegular,
  ArrowMaximizeVerticalRegular,
  ArrowMinimizeVerticalRegular,
  ArrowRightRegular,
  ArrowUploadRegular,
  CheckmarkRegular,
  CheckmarkCircleRegular,
  ChevronDownRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  CircleRegular,
  CopyRegular,
  DeleteRegular,
  DismissRegular,
  DocumentRegular,
  ErrorCircleRegular,
  FolderSearchRegular,
  InfoRegular,
  MoreHorizontalRegular,
  OpenRegular,
  PersonFeedbackRegular,
  PinFilled,
  PinRegular,
  SearchRegular,
  SparkleRegular,
  StarFilled,
  StarRegular,
  StopRegular,
  ThumbDislikeRegular,
  ThumbLikeRegular,
  WarningFilled,
  WarningRegular,
} from '@fluentui/react-icons';
import './tokens.css';
import type {
  AzfAction,
  AzfAgentStep,
  AzfArtifact,
  AzfAttachment,
  AzfAccordionItem,
  AzfColumn,
  AzfCodeSnippetLine,
  AzfCodeSnippetToken,
  AzfCopyButtonVisualState,
  AzfFileUploadState,
  AzfFilter,
  AzfOption,
  AzfPagerState,
  AzfPropertyItem,
  AzfResourceTagRow,
  AzfResponsePart,
  AzfServiceMenuGroup,
  AzfServiceMenuItem,
  AzfSortState,
  AzfStepBadge,
  AzfSummaryMetric,
  AzfTone,
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
    const result = a instanceof Date || b instanceof Date
      ? Number(a) - Number(b)
      : String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
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

  return action.icon
    ? (
        <Tooltip key={action.id} content={action.label} relationship="label">
          {button}
        </Tooltip>
      )
    : button;
}

function renderStateContent(emptyState: ReactNode) {
  if (typeof emptyState === 'string' || typeof emptyState === 'number') {
    return <AzureEmptyState compact title={String(emptyState)} />;
  }
  return emptyState;
}

function copyStatusText(label: ReactNode) {
  return typeof label === 'string' || typeof label === 'number' ? String(label) : 'Copied';
}

export interface IconActionButtonProps extends AzfAction {
  size?: 'small' | 'medium' | 'large';
}

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

export interface StatusIconTextProps {
  status?: AzfTone;
  icon?: ReactNode;
  children?: ReactNode;
  className?: string;
}

export function StatusIconText({ status = 'neutral', icon, children, className }: StatusIconTextProps) {
  const fallback = status === 'success'
    ? <CheckmarkCircleRegular />
    : status === 'danger'
      ? <ErrorCircleRegular />
      : status === 'warning'
        ? <WarningRegular />
        : <InfoRegular />;

  return (
    <span className={mergeClasses('azf-row azf-gap-xs azf-status-text', className)} data-status={status}>
      <AzureIcon className="azf-status-icon" size={12} icon={icon ?? fallback} />
      {children && <Text>{children}</Text>}
    </span>
  );
}

export interface BladeHeaderProps {
  title: ReactNode;
  subtitle?: ReactNode;
  resourceIcon?: ReactNode;
  menuLabel?: ReactNode;
  menu?: ReactNode;
  actions?: AzfAction[];
  copilotActions?: AzfAction[];
  overflowActions?: AzfAction[];
  pinned?: boolean;
  onPin?: () => void;
  starred?: boolean;
  onStar?: () => void;
  onDismiss?: () => void;
  promptRibbon?: ReactNode;
  size?: 'large' | 'compact';
  loading?: boolean;
  className?: string;
}

export function BladeHeader({
  title,
  subtitle,
  resourceIcon,
  menuLabel,
  menu,
  actions = [],
  copilotActions = [],
  overflowActions = [],
  pinned,
  onPin,
  starred,
  onStar,
  onDismiss,
  promptRibbon,
  size = 'large',
  loading,
  className,
}: BladeHeaderProps) {
  const visibleActions = actions.slice(0, 3);
  const overflow = [...actions.slice(3), ...overflowActions];
  // Figma node 32615:9834 keeps Pin / Star / More and the Copilot prompt ribbon
  // INLINE on the title row; only Close (dismiss) is pushed to the far right.
  const hasInlineCommands = Boolean(onPin || onStar || overflow.length > 0);

  return (
    <header
      className={mergeClasses('azf-stack azf-blade-header', size === 'compact' && 'azf-blade-header--compact', className)}
      aria-busy={loading || undefined}
    >
      <div className="azf-row azf-blade-header__main">
        <div className="azf-row azf-blade-header__title">
          {resourceIcon && <AzureIcon className="azf-blade-header__icon" size={28} icon={resourceIcon} decorative />}
          <div className="azf-stack azf-blade-header__lockup">
            <div className="azf-row azf-blade-header__titlerow">
              <Text as="h1" wrap={false} className="azf-blade-header__title-text" size={size === 'compact' ? 500 : 600} weight="semibold">
                {title}
              </Text>
              {menuLabel && (
                <>
                  <span className="azf-blade-header__divider" aria-hidden="true" />
                  <Text wrap={false} size={size === 'compact' ? 500 : 600} className="azf-blade-header__menu-label">{menuLabel}</Text>
                </>
              )}
              {menu && <div className="azf-blade-header__menu">{menu}</div>}
              {hasInlineCommands && (
                <div className="azf-row azf-blade-header__commands" aria-label="Blade commands">
                  {onPin && (
                    <IconActionButton
                      id="pin"
                      label={pinned ? 'Unpin' : 'Pin'}
                      icon={pinned ? <PinFilled /> : <PinRegular />}
                      onClick={onPin}
                    />
                  )}
                  {onStar && (
                    <IconActionButton
                      id="star"
                      label={starred ? 'Remove from favorites' : 'Add to favorites'}
                      icon={starred ? <StarFilled /> : <StarRegular />}
                      onClick={onStar}
                    />
                  )}
                  {overflow.length > 0 && (
                    <Popover positioning="below-end">
                      <PopoverTrigger disableButtonEnhancement>
                        <Button appearance="subtle" icon={<MoreHorizontalRegular />} aria-label="More actions" />
                      </PopoverTrigger>
                      <PopoverSurface className="azf-stack azf-action-overflow">
                        {overflow.map((action) => (
                          <Button
                            key={action.id}
                            appearance="subtle"
                            icon={action.icon}
                            disabled={action.disabled || action.loading}
                            onClick={action.onClick}
                          >
                            {action.label}
                          </Button>
                        ))}
                      </PopoverSurface>
                    </Popover>
                  )}
                </div>
              )}
              {promptRibbon && <div className="azf-row azf-blade-header__ribbon">{promptRibbon}</div>}
            </div>
            {subtitle && <Text className="azf-blade-header__subtitle">{subtitle}</Text>}
          </div>
        </div>
        <div className="azf-row azf-blade-header__actions" aria-label="Blade actions">
          {loading && <Spinner size="tiny" labelPosition="after" label="Loading" />}
          {copilotActions.map((action) => (
            <Button
              key={action.id}
              className="azf-blade-header__copilot"
              appearance="subtle"
              size="small"
              icon={action.icon}
              disabled={action.disabled || action.loading}
              onClick={action.onClick}
            >
              {action.label}
            </Button>
          ))}
          {visibleActions.map((action) => renderAction(action))}
          {onDismiss && (
            <IconActionButton id="dismiss" label="Close" icon={<DismissRegular />} onClick={onDismiss} />
          )}
        </div>
      </div>
    </header>
  );
}

/**
 * Copilot prompt ribbon — an inline Copilot entry point rendered as a spark
 * button followed by suggested-prompt pills. MCP-grounded to Figma
 * "PromptRibbonCopilot" node 30909:48908 (file q2TdO4dVcMhNWYp0N6Bc05): 32px
 * row, gap-8; spark button = colorNeutralBackground1Selected bg + 4px radius +
 * 6px padding + 20px Copilot mark; suggested-prompt pill (node 30945:10377) =
 * colorNeutralBackground1 bg, 1px colorBrandStroke2 border, 8px radius,
 * 4px/8px padding, caption-1 (12px) label.
 */
export interface CopilotPromptRibbonPromptItem {
  id: string;
  label: string;
  onClick?: () => void;
}

export interface CopilotPromptRibbonProps {
  prompts: CopilotPromptRibbonPromptItem[];
  onOpen?: () => void;
  label?: string;
  className?: string;
}

export function CopilotPromptRibbon({ prompts, onOpen, label = 'Copilot', className }: CopilotPromptRibbonProps) {
  return (
    <div className={mergeClasses('azf-row azf-copilot-prompt-ribbon', className)}>
      <button
        type="button"
        className="azf-row azf-copilot-prompt-ribbon__spark"
        aria-label={label}
        onClick={onOpen}
      >
        <SparkleRegular />
      </button>
      {prompts.map((prompt) => (
        <button
          key={prompt.id}
          type="button"
          className="azf-copilot-prompt-ribbon__pill"
          onClick={prompt.onClick}
        >
          {prompt.label}
        </button>
      ))}
    </div>
  );
}

export interface PortalTopNavPerson {
  name: ReactNode;
  secondaryText?: ReactNode;
  icon?: ReactNode;
}

export interface PortalTopNavProps {
  brand?: {
    product: ReactNode;
    area?: ReactNode;
  };
  startActions?: AzfAction[];
  startContent?: ReactNode;
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  searchPlaceholder?: string;
  searchAriaLabel?: string;
  centerContent?: ReactNode;
  copilotAction?: AzfAction;
  endActions?: AzfAction[];
  endContent?: ReactNode;
  persona?: PortalTopNavPerson;
  variant?: 'brand' | 'neutral';
  ariaLabel?: string;
  className?: string;
}

export function PortalTopNav({
  brand,
  startActions = [],
  startContent,
  searchValue,
  onSearchChange,
  searchPlaceholder = 'Search resources, services, and docs',
  searchAriaLabel = 'Search Azure resources',
  centerContent,
  copilotAction,
  endActions = [],
  endContent,
  persona,
  variant = 'brand',
  ariaLabel,
  className,
}: PortalTopNavProps) {
  const hasSearch = onSearchChange || searchValue != null;

  return (
    <header
      className={mergeClasses('azf-portal-topnav', className)}
      data-variant={variant}
      aria-label={ariaLabel}
    >
      <div className="azf-portal-topnav__start">
        {startActions.map((action) => (
          <IconActionButton key={action.id} size="small" {...action} />
        ))}
        {brand && (
          <div className="azf-portal-topnav__brand">
            <Text weight="semibold">{brand.product}</Text>
            {brand.area && <Text className="azf-portal-topnav__brand-secondary">{brand.area}</Text>}
          </div>
        )}
        {startContent}
      </div>

      <div className="azf-portal-topnav__center">
        {hasSearch ? (
          <SearchBox
            className="azf-portal-topnav__search"
            value={searchValue}
            onChange={(_, data) => onSearchChange?.(data.value)}
            placeholder={searchPlaceholder}
            aria-label={searchAriaLabel}
            contentBefore={<SearchRegular />}
          />
        ) : centerContent}
      </div>

      <div className="azf-portal-topnav__end">
        {copilotAction && (
          <Button
            appearance="subtle"
            icon={copilotAction.icon ?? <SparkleRegular />}
            onClick={copilotAction.onClick}
            disabled={copilotAction.disabled || copilotAction.loading}
            className="azf-portal-topnav__copilot"
          >
            {copilotAction.label}
          </Button>
        )}
        {endActions.map((action) => (
          <IconActionButton key={action.id} size="small" {...action} />
        ))}
        {endContent}
        {persona && (
          <div className="azf-portal-topnav__persona" aria-label="Signed in user">
            <span className="azf-portal-topnav__persona-icon">{persona.icon ?? <InfoRegular />}</span>
            <div className="azf-portal-topnav__persona-copy">
              <Text weight="semibold">{persona.name}</Text>
              {persona.secondaryText && <Text className="azf-portal-topnav__brand-secondary">{persona.secondaryText}</Text>}
            </div>
          </div>
        )}
      </div>
    </header>
  );
}

type PortalRailLinkElement = ReactElement<{
  className?: string;
  children?: ReactNode;
  onClick?: () => void;
  tabIndex?: number;
  'aria-label'?: string;
  'aria-current'?: 'page';
  'aria-disabled'?: boolean;
  'data-selected'?: boolean;
  'data-disabled'?: boolean;
}>;

export interface PortalRailItem {
  id: string;
  label: string;
  icon: ReactElement;
  selected?: boolean;
  disabled?: boolean;
  onClick?: () => void;
  link?: PortalRailLinkElement;
}

export interface PortalRailSection {
  id: string;
  label?: ReactNode;
  ariaLabel?: string;
  items: PortalRailItem[];
  anchorBottom?: boolean;
}

export interface PortalRailBrand {
  label: string;
  icon?: ReactElement;
  href?: string;
  link?: PortalRailLinkElement;
  ariaLabel?: string;
}

export interface PortalRailProps {
  items?: PortalRailItem[];
  sections?: PortalRailSection[];
  ariaLabel?: string;
  brand?: PortalRailBrand;
  /** Rendered directly under the brand chrome, above the scrollable nav (e.g. a
   *  scope/project switcher or a primary "new" action). Hidden when collapsed. */
  headerContent?: ReactNode;
  /** Pinned to the bottom of the rail (e.g. the signed-in persona menu). */
  footerContent?: ReactNode;
  collapsed?: boolean;
  collapsible?: boolean;
  onToggleCollapsed?: () => void;
  collapseLabel?: string;
  expandLabel?: string;
  collapseIcon?: ReactElement;
  expandIcon?: ReactElement;
  variant?: 'brand' | 'neutral';
  dataTestId?: string;
  scrollTestId?: string;
  className?: string;
}

function withPortalRailLink(
  link: PortalRailLinkElement,
  className: string,
  props: Omit<PortalRailLinkElement['props'], 'className' | 'children'>,
  children: ReactNode,
) {
  return cloneElement(link, {
    ...props,
    className: mergeClasses(link.props.className, className),
    children,
  });
}

export function PortalRail({
  items,
  sections,
  ariaLabel = 'Portal rail navigation',
  brand,
  headerContent,
  footerContent,
  collapsed,
  collapsible = false,
  onToggleCollapsed,
  collapseLabel = 'Collapse navigation',
  expandLabel = 'Expand navigation',
  collapseIcon,
  expandIcon,
  variant = 'brand',
  dataTestId,
  scrollTestId,
  className,
}: PortalRailProps) {
  const isCollapsed = collapsed ?? true;
  const resolvedSections = sections ?? (items ? [{ id: 'items', items }] : []);

  const renderBrand = () => {
    if (!brand) return null;

    const content = (
      <>
        {brand.icon && <span className="azf-portal-rail__brand-icon" aria-hidden="true">{brand.icon}</span>}
        {!isCollapsed && <span className="azf-portal-rail__brand-label">{brand.label}</span>}
      </>
    );
    const brandClassName = mergeClasses('azf-portal-rail__brand-link', isCollapsed && 'azf-portal-rail__brand-link--collapsed');
    const brandProps = {
      'aria-label': brand.ariaLabel ?? brand.label,
    };

    if (brand.link) {
      return withPortalRailLink(brand.link, brandClassName, brandProps, content);
    }
    if (brand.href) {
      return <a className={brandClassName} href={brand.href} {...brandProps}>{content}</a>;
    }
    return <div className={brandClassName} {...brandProps}>{content}</div>;
  };

  const renderItem = (item: PortalRailItem) => {
    const content = (
      <>
        <span className="azf-portal-rail__icon" aria-hidden="true">{item.icon}</span>
        {!isCollapsed && <span className="azf-portal-rail__label">{item.label}</span>}
      </>
    );
    const itemClassName = mergeClasses(
      'azf-portal-rail__item',
      'azf-portal-rail__button',
      isCollapsed && 'azf-portal-rail__item--collapsed',
      item.selected && 'azf-portal-rail__item--selected',
    );
    const itemProps = {
      'aria-label': item.label,
      'aria-current': item.selected ? 'page' as const : undefined,
      'aria-disabled': item.disabled || undefined,
      'data-selected': item.selected || undefined,
      'data-disabled': item.disabled || undefined,
      onClick: item.disabled ? undefined : item.onClick,
      tabIndex: item.disabled ? -1 : undefined,
    };
    const node = item.link ? (
      withPortalRailLink(item.link, itemClassName, itemProps, content)
    ) : (
      <Button
        appearance="subtle"
        disabled={item.disabled}
        className={itemClassName}
        icon={isCollapsed ? (item.icon as ButtonProps['icon']) : undefined}
        {...itemProps}
      >
        {!isCollapsed && content}
      </Button>
    );

    if (!isCollapsed) return <Fragment key={item.id}>{node}</Fragment>;
    return (
      <Tooltip content={item.label} relationship="label" positioning="after" key={item.id}>
        {node}
      </Tooltip>
    );
  };

  const renderSection = (section: PortalRailSection) => (
    <div
      key={section.id}
      role="group"
      aria-label={section.ariaLabel ?? (typeof section.label === 'string' ? section.label : undefined)}
      className={mergeClasses(
        'azf-portal-rail__section',
        !isCollapsed && Boolean(section.label) && 'azf-portal-rail__section--framed',
        section.anchorBottom && 'azf-portal-rail__section--bottom',
      )}
      style={{ gap: '0px' }}
    >
      {section.label && (
        isCollapsed ? (
          <div className="azf-portal-rail__section-divider" aria-hidden="true" />
        ) : (
          <div className="azf-portal-rail__section-heading">{section.label}</div>
        )
      )}
      {section.items.map(renderItem)}
    </div>
  );

  return (
    <nav
      className={mergeClasses('azf-portal-rail', className)}
      aria-label={ariaLabel}
      data-variant={variant}
      data-collapsed={isCollapsed ? 'true' : 'false'}
      data-testid={dataTestId}
    >
      {(brand || collapsible) && (
        <div className="azf-portal-rail__chrome">
          {renderBrand()}
          {collapsible && (
            <Button
              appearance="subtle"
              icon={isCollapsed ? expandIcon : collapseIcon}
              aria-label={isCollapsed ? expandLabel : collapseLabel}
              aria-expanded={!isCollapsed}
              onClick={onToggleCollapsed}
              className="azf-portal-rail__collapse"
            />
          )}
        </div>
      )}
      {headerContent && !isCollapsed && (
        <div className="azf-portal-rail__header">{headerContent}</div>
      )}
      <div
        className="azf-portal-rail__scroll"
        data-testid={scrollTestId}
        data-scrollbar-mode={isCollapsed ? 'hidden' : 'hover'}
        tabIndex={0}
      >
        {resolvedSections.map(renderSection)}
      </div>
      {footerContent && (
        <div className="azf-portal-rail__footer">{footerContent}</div>
      )}
    </nav>
  );
}

export interface PortalLayoutProps {
  topNav?: ReactNode;
  rail?: ReactNode;
  breadcrumb?: ReactNode;
  header?: ReactNode;
  commandBar?: ReactNode;
  filters?: ReactNode;
  footer?: ReactNode;
  children: ReactNode;
  variant?: 'default' | 'appShell';
  bodyAs?: 'div' | 'main';
  bodyKey?: string;
  bodyAriaLabel?: string;
  bodyTestId?: string;
  className?: string;
  contentClassName?: string;
}

export function PortalLayout({
  topNav,
  rail,
  breadcrumb,
  header,
  commandBar,
  filters,
  footer,
  children,
  variant = 'default',
  bodyAs = 'div',
  bodyKey,
  bodyAriaLabel,
  bodyTestId,
  className,
  contentClassName,
}: PortalLayoutProps) {
  const Body = bodyAs;

  return (
    <section
      className={mergeClasses(
        'azf-portal-layout',
        topNav ? 'azf-portal-layout--with-topnav' : undefined,
        variant === 'appShell' && 'azf-portal-layout--app-shell',
        className,
      )}
    >
      {topNav}
      <div className={mergeClasses('azf-portal-layout__frame', !rail && 'azf-portal-layout__frame--single-column')}>
        {rail && <aside className="azf-portal-layout__rail">{rail}</aside>}
        <div className="azf-portal-layout__content">
          {breadcrumb && <div className="azf-portal-layout__breadcrumb">{breadcrumb}</div>}
          {header}
          {commandBar}
          {filters}
          <Body
            key={bodyKey}
            className={mergeClasses('azf-portal-layout__body', contentClassName)}
            aria-label={bodyAriaLabel}
            data-testid={bodyTestId}
          >
            {children}
          </Body>
          {footer}
        </div>
      </div>
    </section>
  );
}

export interface CommandBarProps {
  title?: ReactNode;
  description?: ReactNode;
  primaryActions?: AzfAction[];
  secondaryActions?: AzfAction[];
  children?: ReactNode;
  className?: string;
  ariaLabel?: string;
}

export function CommandBar({
  title,
  description,
  primaryActions = [],
  secondaryActions = [],
  children,
  className,
  ariaLabel = 'Command bar',
}: CommandBarProps) {
  return (
    <div className={mergeClasses('azf-row azf-wrap azf-command-bar', className)}>
      <div className="azf-row azf-wrap azf-gap-s azf-command-bar__group">
        {(title || description) && (
          <div className="azf-stack azf-gap-xs azf-command-bar__meta">
            {title && <Text weight="semibold">{title}</Text>}
            {description && <Text className="azf-muted">{description}</Text>}
          </div>
        )}
        {children}
        {primaryActions.length > 0 && (
          <Toolbar aria-label={ariaLabel} className="azf-command-bar__toolbar">
            {primaryActions.map((action) => (
              <ToolbarButton
                key={action.id}
                icon={action.loading ? <Spinner size="tiny" /> : action.icon}
                disabled={action.disabled || action.loading}
                onClick={action.onClick}
              >
                {action.label}
              </ToolbarButton>
            ))}
          </Toolbar>
        )}
      </div>
      {secondaryActions.length > 0 && (
        <div className="azf-row azf-wrap azf-gap-xs azf-command-bar__secondary" aria-label="Secondary commands">
          {secondaryActions.map((action) => renderAction({ ...action, appearance: action.appearance ?? 'subtle' }))}
        </div>
      )}
    </div>
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

export function ServiceMenu({
  groups,
  selectedId,
  onSelect,
  searchable = true,
  collapsed = false,
  searchValue,
  onSearchChange,
  onToggleFavorite,
  ariaLabel = 'Service navigation',
  className,
}: ServiceMenuProps) {
  const [internalSearch, setInternalSearch] = useState('');
  const query = (searchValue ?? internalSearch).trim().toLowerCase();
  const setQuery = (value: string) => (onSearchChange ?? setInternalSearch)(value);

  return (
    <nav className={mergeClasses('azf-stack azf-service-menu', collapsed && 'azf-service-menu--collapsed', className)} aria-label={ariaLabel}>
      {searchable && !collapsed && (
        <SearchBox
          contentBefore={<SearchRegular />}
          aria-label="Filter navigation"
          placeholder="Search"
          value={searchValue ?? internalSearch}
          onChange={(_, data) => setQuery(data.value)}
        />
      )}
      {groups.map((group) => {
        const items = group.items
          .flatMap((item) => flattenMenuItem(item))
          .filter((item) => !query || item.label.toLowerCase().includes(query));
        if (items.length === 0) return null;

        return (
          <div className="azf-stack azf-service-menu__group" key={group.id}>
            {!collapsed && (
              <Text className="azf-service-menu__group-title azf-muted" weight="semibold">
                {group.label}
              </Text>
            )}
            {items.map((item) => {
              const selected = item.id === selectedId;
              const content = (
                <Button
                  appearance="subtle"
                  className="azf-service-menu__item"
                  data-selected={selected || undefined}
                  data-child={item.child || item.depth > 0 || undefined}
                  style={{ paddingInlineStart: collapsed ? undefined : 8 + item.depth * 16 }}
                  icon={<span className="azf-service-menu__icon">{item.icon}</span>}
                  disabled={item.disabled}
                  aria-current={selected ? 'page' : undefined}
                  aria-label={collapsed ? item.label : undefined}
                  onClick={() => onSelect?.(item.id)}
                >
                  {collapsed ? undefined : <span className="azf-service-menu__label">{item.label}</span>}
                  {!collapsed && item.badge && <Badge size="small">{item.badge}</Badge>}
                </Button>
              );

              return (
                <div className="azf-row azf-service-menu__item-row" key={item.id}>
                  {collapsed ? <Tooltip content={item.label} relationship="label">{content}</Tooltip> : content}
                  {!collapsed && onToggleFavorite && (
                    <IconActionButton
                      id={`favorite-${item.id}`}
                      label={item.favorite ? `Unfavorite ${item.label}` : `Favorite ${item.label}`}
                      icon={item.favorite ? <StarRegular /> : <PinRegular />}
                      size="small"
                      appearance="subtle"
                      onClick={() => onToggleFavorite(item.id)}
                    />
                  )}
                </div>
              );
            })}
          </div>
        );
      })}
    </nav>
  );
}

export interface DataToolbarProps {
  title?: ReactNode;
  actions?: AzfAction[];
  children?: ReactNode;
  className?: string;
  ariaLabel?: string;
}

export function DataToolbar({ title, actions = [], children, className, ariaLabel = 'Data actions' }: DataToolbarProps) {
  return (
    <CommandBar title={title} primaryActions={actions} className={mergeClasses('azf-toolbar', className)} ariaLabel={ariaLabel}>
      {children}
    </CommandBar>
  );
}

export const PortalCommandBar = CommandBar;

export interface FilterBarProps {
  filters: AzfFilter[];
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  searchPlaceholder?: string;
  children?: ReactNode;
  className?: string;
}

export function FilterBar({
  filters,
  searchValue,
  onSearchChange,
  searchPlaceholder = 'Search',
  children,
  className,
}: FilterBarProps) {
  return (
    <div className={mergeClasses('azf-row azf-wrap azf-filter-bar', className)}>
      {onSearchChange && (
        <SearchBox value={searchValue} placeholder={searchPlaceholder} onChange={(_, data) => onSearchChange(data.value)} />
      )}
      <div className="azf-row azf-wrap azf-gap-xs" aria-label="Selected filters">
        {filters.map((filter) => (
          <span key={filter.id} className="azf-row azf-filter-pill">
            <Tag appearance={filter.selected ? 'brand' : 'filled'} disabled={filter.disabled}>
              {filter.label}
              {filter.value ? <>: {filter.value}</> : null}
            </Tag>
            {filter.removable && (
              <Button
                size="small"
                appearance="subtle"
                icon={<DismissRegular />}
                aria-label={`Remove ${filter.label}`}
                disabled={filter.disabled}
                onClick={filter.onRemove}
              />
            )}
          </span>
        ))}
      </div>
      {children}
    </div>
  );
}

export interface AzureEmptyStateProps {
  title: ReactNode;
  body?: ReactNode;
  icon?: ReactNode;
  action?: ReactNode;
  compact?: boolean;
  className?: string;
}

export function AzureEmptyState({
  title,
  body,
  icon = <InfoRegular />,
  action,
  compact = false,
  className,
}: AzureEmptyStateProps) {
  return (
    <div className={mergeClasses('azf-stack azf-empty-state', compact && 'azf-empty-state--compact', className)}>
      <span className="azf-empty-state__icon" aria-hidden="true">{icon}</span>
      <div className="azf-stack azf-gap-xs azf-empty-state__copy">
        <Text weight="semibold">{title}</Text>
        {body && <Text className="azf-muted">{body}</Text>}
      </div>
      {action && <div className="azf-empty-state__action">{action}</div>}
    </div>
  );
}

export interface AzureSummaryCardProps {
  title: ReactNode;
  icon?: ReactNode;
  metrics: AzfSummaryMetric[];
  /** Optional React Router link element to make the whole card navigable. */
  link?: ReactElement<{ className?: string; children?: ReactNode }>;
  onClick?: () => void;
  ariaLabel?: string;
  className?: string;
}

/**
 * Resource summary card — leading icon + title header with right-aligned metric
 * rows and optional status dots. Flat, bordered, no shadow. Compose several in a
 * row (e.g. inside `.azf-overview-grid`) to build an Azure-style overview blade,
 * mirroring the Azure Container Apps sandbox overview summary tiles.
 */
export function AzureSummaryCard({
  title,
  icon,
  metrics,
  link,
  onClick,
  ariaLabel,
  className,
}: AzureSummaryCardProps) {
  const body = (
    <>
      <div className="azf-summary-card__head">
        {icon && <span className="azf-summary-card__icon" aria-hidden="true">{icon}</span>}
        <Text as="h3" className="azf-summary-card__title">{title}</Text>
      </div>
      <div className="azf-summary-card__metrics">
        {metrics.map((metric) => (
          <div className="azf-summary-card__metric" key={metric.id}>
            <span className="azf-summary-card__metric-label">
              {metric.tone && <span className="azf-summary-card__dot" data-tone={metric.tone} aria-hidden="true" />}
              {metric.label}
            </span>
            <span className="azf-summary-card__metric-value">{metric.value}</span>
          </div>
        ))}
      </div>
    </>
  );

  if (link) {
    return cloneElement(link, {
      className: mergeClasses('azf-summary-card', link.props.className, className),
      children: body,
    });
  }

  if (onClick) {
    return (
      <button type="button" className={mergeClasses('azf-summary-card', className)} onClick={onClick} aria-label={ariaLabel}>
        {body}
      </button>
    );
  }

  return (
    <section className={mergeClasses('azf-summary-card', className)} aria-label={ariaLabel}>
      {body}
    </section>
  );
}

export interface AzurePropertyListProps {
  title?: ReactNode;
  items: AzfPropertyItem[];
  className?: string;
}

/**
 * Property list card — titled card with an uppercase muted label column and value
 * rows. The out-of-portal equivalent of an Azure "Essentials" panel, modeled on
 * the Azure Container Apps sandbox "Basics" / "Networking" property cards.
 */
export function AzurePropertyList({ title, items, className }: AzurePropertyListProps) {
  return (
    <section className={mergeClasses('azf-property-card', className)}>
      {title && <Text as="h3" className="azf-property-card__title">{title}</Text>}
      <dl className="azf-property-card__grid">
        {items.map((item) => (
          <Fragment key={item.id}>
            <dt className="azf-property-card__label">{item.label}</dt>
            <dd className="azf-property-card__value">{item.value}</dd>
          </Fragment>
        ))}
      </dl>
    </section>
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
  caption?: ReactNode;
  density?: 'compact' | 'cozy';
  className?: string;
}

export function AzureDataGrid<T>({
  items,
  columns,
  getRowId,
  selectedRowId,
  onRowClick,
  sortState,
  defaultSortState,
  onSortChange,
  loading,
  error,
  emptyState = <AzureEmptyState compact title="No resources found." body="Try adjusting the current search or filters." />,
  ariaLabel = 'Data grid',
  caption,
  density = 'compact',
  className,
}: AzureDataGridProps<T>) {
  const [internalSort, setInternalSort] = useState<AzfSortState | undefined>(defaultSortState);
  const activeSort = sortState ?? internalSort;
  const sortedItems = useMemo(() => defaultSort(items, columns, activeSort), [items, columns, activeSort]);

  const setSort = (columnId: string) => {
    const next: AzfSortState = activeSort?.columnId === columnId && activeSort.direction === 'ascending'
      ? { columnId, direction: 'descending' }
      : { columnId, direction: 'ascending' };
    setInternalSort(next);
    onSortChange?.(next);
  };

  const colSpan = Math.max(1, columns.length);

  return (
    <div className={mergeClasses('azf-data-grid-shell', className)} data-density={density}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {caption && <div className="azf-data-grid__caption">{caption}</div>}
      <Table className="azf-data-grid" aria-label={ariaLabel} aria-busy={loading || undefined}>
        <TableHeader>
          <TableRow>
            {columns.map((column) => {
              const sorted = activeSort?.columnId === column.columnId;
              return (
                <TableHeaderCell key={column.columnId} style={{ width: column.width }} aria-sort={sorted ? activeSort.direction : undefined}>
                  {column.sortable ? (
                    <Button
                      appearance="transparent"
                      iconPosition="after"
                      icon={<ChevronDownRegular className={sorted && activeSort.direction === 'ascending' ? 'azf-sort-icon--ascending' : undefined} />}
                      onClick={() => setSort(column.columnId)}
                    >
                      {column.header}
                    </Button>
                  ) : column.header}
                </TableHeaderCell>
              );
            })}
          </TableRow>
        </TableHeader>
        <TableBody>
          {loading && (
            <TableRow>
              <TableCell colSpan={colSpan}>
                <div className="azf-row azf-gap-s azf-data-grid__state">
                  <Spinner size="tiny" />
                  <Text>Loading</Text>
                </div>
              </TableCell>
            </TableRow>
          )}
          {!loading && sortedItems.length === 0 && (
            <TableRow>
              <TableCell colSpan={colSpan}>
                <div className="azf-data-grid__state">{renderStateContent(emptyState)}</div>
              </TableCell>
            </TableRow>
          )}
          {!loading && sortedItems.map((item, index) => {
            const rowId = getRowId?.(item, index) ?? String(index);
            const interactive = Boolean(onRowClick);
            const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
              if (interactive && (event.key === 'Enter' || event.key === ' ')) {
                event.preventDefault();
                onRowClick?.(item);
              }
            };

            return (
              <TableRow
                key={rowId}
                aria-selected={rowId === selectedRowId}
                data-selected={rowId === selectedRowId || undefined}
                onClick={() => onRowClick?.(item)}
                onKeyDown={onKeyDown}
                tabIndex={interactive ? 0 : undefined}
                className={interactive ? 'azf-data-grid__row--interactive' : undefined}
              >
                {columns.map((column) => <TableCell key={column.columnId}>{column.renderCell(item)}</TableCell>)}
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

export interface FormFieldRowProps {
  label: ReactNode;
  htmlFor?: string;
  info?: ReactNode;
  hint?: ReactNode;
  validationMessage?: ReactNode;
  validationState?: 'error' | 'warning' | 'success';
  status?: ReactNode;
  required?: boolean;
  children: ReactNode;
  className?: string;
}

export function FormFieldRow({
  label,
  htmlFor,
  info,
  hint,
  validationMessage,
  validationState = validationMessage ? 'error' : undefined,
  status,
  required,
  children,
  className,
}: FormFieldRowProps) {
  const labelText = optionText(label);

  return (
    <div className={mergeClasses('azf-form-row', className)} data-validation-state={validationState || undefined}>
      <div className="azf-form-row__header">
        <div className="azf-form-row__labelline">
          {info ? (
            <InfoLabel htmlFor={htmlFor} info={info} infoButton={{ 'aria-label': `About ${labelText}` }}>
              {label}
            </InfoLabel>
          ) : (
            <label className="azf-form-row__label" htmlFor={htmlFor}>
              {label}
            </label>
          )}
          {required && <span className="azf-form-row__required" aria-hidden="true">*</span>}
        </div>
        {hint && <Text className="azf-form-row__hint">{hint}</Text>}
      </div>
      <div className="azf-stack azf-gap-xs azf-form-row__control">
        {children}
        {validationMessage && <Text className="azf-form-row__message">{validationMessage}</Text>}
        {!validationMessage && status && <div className="azf-form-row__status">{status}</div>}
      </div>
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

export function ResourceTagEditor({
  rows,
  resources,
  onRowChange,
  onAddRow,
  onDeleteRow,
  validation = {},
  disabled,
  className,
}: ResourceTagEditorProps) {
  return (
    <div className={mergeClasses('azf-stack azf-gap-s', className)}>
      <Table className="azf-resource-tags" aria-label="Resource tags">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Name</TableHeaderCell>
            <TableHeaderCell>Value</TableHeaderCell>
            <TableHeaderCell>Resource</TableHeaderCell>
            <TableHeaderCell>Actions</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row, index) => (
            <TableRow key={row.id}>
              <TableCell>
                <Field validationMessage={validation[`${row.id}:name`]} validationState={validation[`${row.id}:name`] ? 'error' : undefined}>
                  <Input
                    value={row.name}
                    disabled={disabled}
                    onChange={(_, data) => onRowChange?.(row.id, { name: data.value })}
                    aria-label={`Tag name for row ${index + 1}`}
                  />
                </Field>
              </TableCell>
              <TableCell>
                <Field validationMessage={validation[`${row.id}:value`]} validationState={validation[`${row.id}:value`] ? 'error' : undefined}>
                  <Input
                    value={row.value}
                    disabled={disabled}
                    onChange={(_, data) => onRowChange?.(row.id, { value: data.value })}
                    aria-label={`Tag value for row ${index + 1}`}
                  />
                </Field>
              </TableCell>
              <TableCell>
                <Field validationMessage={validation[`${row.id}:resourceId`]} validationState={validation[`${row.id}:resourceId`] ? 'error' : undefined}>
                  <Combobox
                    disabled={disabled}
                    selectedOptions={row.resourceId ? [row.resourceId] : []}
                    value={optionText(resources.find((resource) => resource.id === row.resourceId)?.label ?? '')}
                    onOptionSelect={(_, data) => onRowChange?.(row.id, { resourceId: data.optionValue })}
                    aria-label={`Resource for row ${index + 1}`}
                  >
                    {resources.map((resource) => (
                      <Option key={resource.id} value={resource.id} text={optionText(resource.label)} disabled={resource.disabled}>
                        {resource.icon}
                        {resource.label}
                      </Option>
                    ))}
                  </Combobox>
                </Field>
              </TableCell>
              <TableCell>
                <IconActionButton
                  id={`delete-${row.id}`}
                  label={`Delete tag row ${index + 1}`}
                  icon={<DeleteRegular />}
                  disabled={disabled}
                  onClick={() => onDeleteRow?.(row.id)}
                />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {onAddRow && (
        <Button appearance="subtle" icon={<AddRegular />} disabled={disabled} onClick={onAddRow}>
          Add tag
        </Button>
      )}
    </div>
  );
}

export interface FeedbackFooterProps {
  title?: ReactNode;
  body?: ReactNode;
  action?: AzfAction;
  className?: string;
  icon?: ReactNode;
}

export function FeedbackFooter({
  title = 'Tell us how this surface worked for you.',
  body,
  action,
  className,
  icon = <PersonFeedbackRegular />,
}: FeedbackFooterProps) {
  return (
    <footer className={mergeClasses('azf-row azf-feedback-footer', className)}>
      <div className="azf-row azf-gap-s azf-feedback-footer__copy">
        <span className="azf-feedback-footer__icon" aria-hidden="true">{icon}</span>
        <div className="azf-stack azf-gap-xs">
          <Text weight="semibold">{title}</Text>
          {body && <Text className="azf-muted">{body}</Text>}
        </div>
      </div>
      {action && <div className="azf-feedback-footer__action">{renderAction({ ...action, appearance: action.appearance ?? 'subtle' })}</div>}
    </footer>
  );
}

export interface FormFooterProps {
  primaryAction: AzfAction;
  secondaryAction?: AzfAction;
  feedback?: ReactNode;
  className?: string;
}

export function FormFooter({ primaryAction, secondaryAction, feedback, className }: FormFooterProps) {
  return (
    <footer className={mergeClasses('azf-row azf-form-footer', className)}>
      <div className="azf-row azf-gap-s azf-form-footer__actions">
        {renderAction({ ...primaryAction, appearance: primaryAction.appearance ?? 'primary' })}
        {secondaryAction && renderAction({ ...secondaryAction, appearance: secondaryAction.appearance ?? 'secondary' })}
      </div>
      {feedback && <div className="azf-form-footer__feedback">{feedback}</div>}
    </footer>
  );
}

export interface PagerProps extends AzfPagerState {
  pageSizeOptions?: number[];
  onPageChange?: (page: number) => void;
  onPageSizeChange?: (pageSize: number) => void;
  className?: string;
}

export function Pager({
  page,
  pageSize,
  totalItems,
  pageSizeOptions = [10, 25, 50],
  onPageChange,
  onPageSizeChange,
  className,
}: PagerProps) {
  const pageCount = Math.max(1, Math.ceil(totalItems / Math.max(1, pageSize)));
  const safePage = Math.min(Math.max(1, page), pageCount);
  const start = totalItems === 0 ? 0 : (safePage - 1) * pageSize + 1;
  const end = Math.min(totalItems, safePage * pageSize);

  return (
    <nav className={mergeClasses('azf-row azf-pager', className)} aria-label="Pagination">
      <Text className="azf-muted" aria-live="polite">{start}-{end} of {totalItems}</Text>
      <Combobox
        aria-label="Rows per page"
        selectedOptions={[String(pageSize)]}
        value={`${pageSize} / page`}
        onOptionSelect={(_, data) => data.optionValue && onPageSizeChange?.(Number(data.optionValue))}
      >
        {pageSizeOptions.map((size) => (
          <Option key={size} value={String(size)} text={`${size} / page`}>
            {size} / page
          </Option>
        ))}
      </Combobox>
      <Button icon={<ChevronLeftRegular />} disabled={safePage <= 1} onClick={() => onPageChange?.(safePage - 1)}>
        Previous
      </Button>
      <Text>{safePage} of {pageCount}</Text>
      <Button icon={<ChevronRightRegular />} iconPosition="after" disabled={safePage >= pageCount} onClick={() => onPageChange?.(safePage + 1)}>
        Next
      </Button>
    </nav>
  );
}

export interface AzureAccordionProps {
  items: readonly AzfAccordionItem[];
  ariaLabel?: string;
  bordered?: boolean;
  collapsible?: boolean;
  defaultOpenItems?: string[];
  multiple?: boolean;
  className?: string;
}

export function AzureAccordion({
  items,
  ariaLabel = 'Accordion',
  bordered = true,
  collapsible = true,
  defaultOpenItems,
  multiple,
  className,
}: AzureAccordionProps) {
  return (
    <Accordion
      collapsible={collapsible}
      multiple={multiple}
      defaultOpenItems={defaultOpenItems}
      className={mergeClasses('azf-stack azf-accordion', !bordered && 'azf-accordion--borderless', className)}
      aria-label={ariaLabel}
    >
      {items.map((item) => (
        <AccordionItem key={item.id} value={item.id} disabled={item.disabled} className="azf-accordion__item">
          <AccordionHeader className="azf-accordion__header">
            <span className="azf-accordion__title">
              {item.icon && <span className="azf-accordion__icon">{item.icon}</span>}
              <span>{item.title}</span>
            </span>
          </AccordionHeader>
          {item.content && <AccordionPanel className="azf-accordion__panel">{item.content}</AccordionPanel>}
        </AccordionItem>
      ))}
    </Accordion>
  );
}

function lineText(line: AzfCodeSnippetLine) {
  if (line.text != null) return line.text;
  return (line.tokens ?? []).map((token) => token.text).join('');
}

function tokenTone(token: AzfCodeSnippetToken) {
  return token.tone ?? 'plain';
}

export interface CopyButtonProps {
  value: string;
  label?: ReactNode;
  ariaLabel?: string;
  copiedLabel?: ReactNode;
  copiedDuration?: number;
  disabled?: boolean;
  onCopy?: (value: string) => void | Promise<void>;
  visualState?: AzfCopyButtonVisualState;
  className?: string;
}

export function CopyButton({
  value,
  label,
  ariaLabel,
  copiedLabel = 'Copied',
  copiedDuration = 2000,
  disabled,
  onCopy,
  visualState,
  className,
}: CopyButtonProps) {
  const [copied, setCopied] = useState(false);
  const resolvedState = visualState ?? (copied ? 'copied' : 'rest');
  const isIconOnly = label == null;

  useEffect(() => {
    if (visualState || !copied) return undefined;
    const timeoutId = window.setTimeout(() => setCopied(false), copiedDuration);
    return () => window.clearTimeout(timeoutId);
  }, [copied, copiedDuration, visualState]);

  const handleCopy = async () => {
    if (disabled) return;
    try {
      if (onCopy) {
        await onCopy(value);
      } else if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(value);
      } else {
        return;
      }
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  return (
    <>
      <Button
        appearance="transparent"
        className={mergeClasses('azf-copy-button', className)}
        data-has-label={!isIconOnly || undefined}
        data-visual-state={resolvedState}
        icon={<span className="azf-copy-button__icon">{resolvedState === 'copied' ? <CheckmarkRegular /> : <CopyRegular />}</span>}
        aria-label={resolvedState === 'copied' ? 'Copied' : ariaLabel ?? (isIconOnly ? 'Copy value' : undefined)}
        onClick={() => { void handleCopy(); }}
        disabled={disabled}
      >
        {!isIconOnly && <span className="azf-copy-button__label">{resolvedState === 'copied' ? copiedLabel : label}</span>}
      </Button>
      {!visualState && (
        <span className="azf-visually-hidden" role="status" aria-live="polite">
          {copied ? copyStatusText(copiedLabel) : ''}
        </span>
      )}
    </>
  );
}

export interface CodeSnippetProps {
  lines: readonly AzfCodeSnippetLine[];
  title?: ReactNode;
  maxHeight?: number | string;
  showCopyButton?: boolean;
  copyValue?: string;
  onCopy?: (value: string) => void | Promise<void>;
  className?: string;
}

export function CodeSnippet({
  lines,
  title,
  maxHeight = 320,
  showCopyButton = true,
  copyValue,
  onCopy,
  className,
}: CodeSnippetProps) {
  const viewportStyle = maxHeight == null
    ? undefined
    : { maxHeight: typeof maxHeight === 'number' ? `${maxHeight}px` : maxHeight };
  const resolvedCopyValue = copyValue ?? lines.map((line) => lineText(line)).join('\n');

  return (
    <section className={mergeClasses('azf-stack azf-code-snippet', className)} aria-label={title ? `${optionText(title)} code snippet` : 'Code snippet'}>
      {(title || showCopyButton) && (
        <div className="azf-row azf-code-snippet__header">
          <div className="azf-row azf-gap-xs">
            {title && <Text className="azf-code-snippet__title">{title}</Text>}
          </div>
          {showCopyButton && <CopyButton value={resolvedCopyValue} label="Copy" onCopy={onCopy} />}
        </div>
      )}
      <div className="azf-code-snippet__viewport" style={viewportStyle}>
        <div className="azf-code-snippet__body">
          {lines.map((line, index) => (
            <div key={line.id ?? `${index}-${line.lineNumber ?? index + 1}`} className="azf-code-snippet__line">
              <div className="azf-row azf-code-snippet__gutter">
                <span className="azf-code-snippet__line-number">{line.lineNumber ?? index + 1}</span>
                {line.foldState && (
                  <span className="azf-code-snippet__fold-marker" data-fold-state={line.foldState} aria-hidden="true">
                    {line.foldState === 'expanded' ? '−' : '+'}
                  </span>
                )}
              </div>
              <div
                className="azf-code-snippet__content"
                style={line.indentLevel ? { paddingInlineStart: `${line.indentLevel * 14}px` } : undefined}
              >
                {line.tokens?.length
                  ? line.tokens.map((token, tokenIndex) => (
                      <span key={`${line.id ?? index}-${tokenIndex}`} className="azf-code-snippet__token" data-tone={tokenTone(token)}>
                        {token.text}
                      </span>
                    ))
                  : <span className="azf-code-snippet__token">{lineText(line)}</span>}
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

export interface NotificationPaneItem {
  id: string;
  title: ReactNode;
  body?: ReactNode;
  tone?: AzfTone;
  timestamp?: ReactNode;
  unread?: boolean;
  actions?: AzfAction[];
}

export interface NotificationPaneProps {
  title?: ReactNode;
  items: NotificationPaneItem[];
  emptyState?: ReactNode;
  footer?: ReactNode;
  surface?: 'inline' | 'flyout';
  className?: string;
}

export function NotificationPane({
  title = 'Notifications',
  items,
  emptyState = <AzureEmptyState compact title="No notifications" body="You're all caught up." />,
  footer,
  surface = 'inline',
  className,
}: NotificationPaneProps) {
  return (
    <aside className={mergeClasses('azf-stack azf-notification-pane', className)} data-surface={surface} aria-label={optionText(title)}>
      <div className="azf-row azf-notification-pane__header">
        <Text as="h2" weight="semibold">{title}</Text>
        {items.length > 0 && <Badge appearance="tint">{items.length}</Badge>}
      </div>
      <div className="azf-stack azf-notification-pane__body">
        {items.length === 0 && renderStateContent(emptyState)}
        {items.map((item) => (
          <section key={item.id} className="azf-stack azf-gap-xs azf-notification-pane__item" data-unread={item.unread || undefined} data-tone={item.tone ?? 'info'}>
            <div className="azf-row azf-notification-pane__item-header">
              <StatusIconText status={item.tone ?? 'info'}>{item.title}</StatusIconText>
              {item.timestamp && <Text className="azf-muted">{item.timestamp}</Text>}
            </div>
            {item.body && <Text>{item.body}</Text>}
            {item.actions && item.actions.length > 0 && (
              <div className="azf-row azf-wrap azf-gap-xs">
                {item.actions.map((action) => renderAction({ ...action, appearance: action.appearance ?? 'subtle' }))}
              </div>
            )}
          </section>
        ))}
      </div>
      {footer}
    </aside>
  );
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

export function CopilotComposer({
  value,
  onChange,
  onSend,
  isRunning,
  onStop,
  disabled,
  agentMode,
  onAgentModeChange,
  attachments = [],
  onAddAttachment,
  validationMessage,
  placeholder = 'Message Copilot',
  sendLabel = 'Send',
  stopLabel = 'Stop response',
  className,
}: CopilotComposerProps) {
  const canSend = Boolean(value.trim()) && !disabled && !isRunning;
  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && canSend) onSend();
  };

  return (
    <div className={mergeClasses('azf-copilot-composer', className)} aria-busy={isRunning || undefined}>
      <div className="azf-stack azf-copilot-composer__field">
        {validationMessage && <MessageBar intent="warning"><MessageBarBody>{validationMessage}</MessageBarBody></MessageBar>}
        <Textarea
          size="large"
          appearance="filled-lighter"
          className="azf-copilot-composer__textarea"
          value={value}
          disabled={disabled}
          onChange={(_, data) => onChange(data.value)}
          onKeyDown={handleKeyDown}
          placeholder={placeholder}
          aria-label={placeholder}
          resize="vertical"
        />
        {attachments.length > 0 && (
          <div className="azf-row azf-wrap azf-gap-xs azf-copilot-composer__attachments" aria-label="Attachments">
            {attachments.map((attachment) => (
              <Tag key={attachment.id} className="azf-copilot-composer__attachment">
                {attachment.name}
                {attachment.onRemove && (
                  <Button
                    appearance="transparent"
                    size="small"
                    icon={<DismissRegular />}
                    aria-label={`Remove ${attachment.name}`}
                    onClick={attachment.onRemove}
                  />
                )}
              </Tag>
            ))}
          </div>
        )}
        <div className="azf-row azf-copilot-composer__footer">
          <div className="azf-row azf-gap-xs">
            {onAddAttachment && (
              <Button appearance="subtle" icon={<AddRegular />} disabled={disabled || isRunning} onClick={onAddAttachment}>
                Add
              </Button>
            )}
            {onAgentModeChange && (
              <ToggleButton
                checked={agentMode}
                icon={<SparkleRegular />}
                disabled={disabled || isRunning}
                className="azf-copilot-composer__agent-toggle"
                onClick={() => onAgentModeChange(!agentMode)}
              >
                {agentMode ? 'Agents on' : 'Agents off'}
              </ToggleButton>
            )}
          </div>
          <Button
            appearance="primary"
            shape="circular"
            className="azf-copilot-composer__send"
            icon={isRunning ? <StopRegular /> : <ArrowRightRegular />}
            aria-label={isRunning ? stopLabel : sendLabel}
            onClick={isRunning ? onStop : onSend}
            disabled={isRunning ? !onStop : !canSend}
          />
        </div>
      </div>
    </div>
  );
}

function ChoicePart({ part }: { part: Extract<AzfResponsePart, { type: 'choices' }> }) {
  const [selected, setSelected] = useState<string[]>([]);
  const toggle = (id: string, checked: boolean) => setSelected((current) => (checked ? [...current, id] : current.filter((value) => value !== id)));

  return (
    <Field label={optionText(part.label)} className="azf-response-choice">
      {part.multiple
        ? part.choices.map((choice) => (
            <Checkbox
              key={choice.id}
              label={optionText(choice.label)}
              disabled={choice.disabled}
              checked={selected.includes(choice.id)}
              onChange={(_, data) => toggle(choice.id, Boolean(data.checked))}
            />
          ))
        : (
            <RadioGroup value={selected[0]} onChange={(_, data) => setSelected([data.value])}>
              {part.choices.map((choice) => (
                <Radio key={choice.id} value={choice.id} label={optionText(choice.label)} disabled={choice.disabled} />
              ))}
            </RadioGroup>
          )}
      <Button appearance="primary" disabled={selected.length === 0} onClick={() => part.onSubmit?.(selected)}>
        {part.submitLabel ?? 'Submit'}
      </Button>
    </Field>
  );
}

export interface CopilotResponseProps {
  parts: AzfResponsePart[];
  actions?: AzfAction[];
  loading?: boolean;
  error?: ReactNode;
  className?: string;
}

function renderResponseContent(content: ReactNode) {
  if (typeof content === 'string' || typeof content === 'number') return <Text>{content}</Text>;
  return content;
}

function CopilotResponseHeader({
  title = 'Copilot',
  badge = 'AI-generated content may be incorrect',
}: {
  title?: ReactNode;
  badge?: ReactNode;
}) {
  return (
    <div className="azf-row azf-copilot-response__header">
      <div className="azf-row azf-gap-s azf-copilot-response__persona">
        <span className="azf-copilot-response__persona-icon" aria-hidden="true">
          <SparkleRegular />
        </span>
        <Text weight="semibold">{title}</Text>
      </div>
      {badge && (
        <span className="azf-copilot-response__badge">
          <Text size={300}>{badge}</Text>
        </span>
      )}
    </div>
  );
}

export function CopilotResponse({ parts, actions = [], loading, error, className }: CopilotResponseProps) {
  return (
    <div className={mergeClasses('azf-stack azf-gap-s', className)}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {parts.map((part) => {
        if (part.type === 'text') {
          if (part.author === 'user') {
            return (
              <div key={part.id} className="azf-chat-bubble" data-author="user">
                {renderResponseContent(part.content)}
              </div>
            );
          }
          return (
            <section key={part.id} className="azf-stack azf-gap-s azf-copilot-response__surface">
              <CopilotResponseHeader title={part.title} badge={part.badge} />
              <div className="azf-copilot-response__body">
                {renderResponseContent(part.content)}
              </div>
              {part.supportingText && <Text className="azf-muted">{part.supportingText}</Text>}
              {part.footerActions?.length ? <div className="azf-row azf-response-actions">{part.footerActions.map((action) => renderAction(action))}</div> : null}
            </section>
          );
        }
        if (part.type === 'confirmation') {
          return (
            <MessageBar key={part.id} intent="warning" className="azf-copilot-response__confirm">
              <MessageBarBody>{part.content}</MessageBarBody>
              <MessageBarActions>
                <Button appearance="primary" onClick={part.onConfirm}>{part.confirmLabel}</Button>
                {part.cancelLabel && <Button onClick={part.onCancel}>{part.cancelLabel}</Button>}
              </MessageBarActions>
            </MessageBar>
          );
        }
        return <ChoicePart key={part.id} part={part} />;
      })}
      {loading && (
        <section className="azf-stack azf-gap-s azf-copilot-response__surface">
          <CopilotResponseHeader />
          <div className="azf-stack azf-gap-xs azf-copilot-response__latency">
            <div className="azf-row azf-gap-s" role="status">
              <span className="azf-copilot-loader" aria-hidden="true" />
              <Text size={300}>Generating response</Text>
            </div>
            <span className="azf-copilot-loader-bar" aria-hidden="true" />
          </div>
        </section>
      )}
      <div className="azf-row azf-response-actions">
        {actions.length
          ? actions.map((action) => renderAction(action))
          : (
              <>
                <IconActionButton id="like" label="Helpful" icon={<ThumbLikeRegular />} />
                <IconActionButton id="dislike" label="Not helpful" icon={<ThumbDislikeRegular />} />
              </>
            )}
      </div>
    </div>
  );
}

export interface InlineCopilotProps {
  open: boolean;
  trigger: ReactElement;
  title?: ReactNode;
  titleIcon?: ReactNode;
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  onDismiss?: () => void;
  state?: 'empty' | 'loading' | 'error' | 'generated';
  errorMessage?: ReactNode;
  suggestions?: AzfOption[];
  placeholder?: string;
  submitLabel?: string;
  loadingLabel?: string;
  className?: string;
}

export function InlineCopilot({
  open,
  trigger,
  title = 'Copilot',
  titleIcon = <SparkleRegular />,
  value,
  onChange,
  onSubmit,
  onDismiss,
  state = 'empty',
  errorMessage,
  suggestions = [],
  placeholder = 'Ask Copilot',
  submitLabel = 'Generate',
  loadingLabel = 'Generating',
  className,
}: InlineCopilotProps) {
  const titleId = useId();

  return (
    <Popover open={open} withArrow positioning="below-start">
      <PopoverTrigger disableButtonEnhancement>{trigger}</PopoverTrigger>
      <PopoverSurface className={mergeClasses('azf-stack azf-inline-copilot', className)} aria-labelledby={titleId}>
        <div className="azf-inline-copilot__flair" aria-hidden="true" />
        <div className="azf-stack azf-gap-s azf-inline-copilot__content">
          <div className="azf-row azf-inline-copilot__header">
            <div className="azf-row azf-gap-s azf-inline-copilot__title">
              <span className="azf-inline-copilot__title-icon" aria-hidden="true">
                {titleIcon}
              </span>
              <Text id={titleId}>{title}</Text>
            </div>
            {onDismiss && <Button appearance="subtle" icon={<DismissRegular />} aria-label="Dismiss" onClick={onDismiss} />}
          </div>
          {errorMessage && <MessageBar intent="error"><MessageBarBody>{errorMessage}</MessageBarBody></MessageBar>}
          <Textarea
            value={value}
            disabled={state === 'loading'}
            onChange={(_, data) => onChange(data.value)}
            aria-label={placeholder}
            placeholder={placeholder}
          />
          {suggestions.length > 0 && (
            <div className="azf-row azf-wrap azf-gap-xs" aria-label="Prompt suggestions">
              {suggestions.map((suggestion) => (
                <Button
                  key={suggestion.id}
                  size="small"
                  shape="circular"
                  appearance="secondary"
                  className="azf-inline-copilot__suggestion"
                  disabled={suggestion.disabled}
                  onClick={() => onChange(optionText(suggestion.label))}
                >
                  {suggestion.label}
                </Button>
              ))}
            </div>
          )}
          {/* Footer + AI disclaimer per Figma Inline Copilot node 29192:8232 (open/guided start,
              file q2TdO4dVcMhNWYp0N6Bc05). Loading uses the "Copilot Loader - Long - Endless"
              gradient bar (node 29192:8246) + label, NOT a blue Fluent Spinner. The
              "AI-generated content may be incorrect" caption-2 disclaimer is node 29192:8267. */}
          <div className="azf-row azf-inline-copilot__footer">
            {state === 'loading' ? (
              <div className="azf-row azf-gap-s azf-inline-copilot__loading" role="status">
                <span className="azf-copilot-loader-bar" aria-hidden="true" />
                <Text>{loadingLabel}</Text>
              </div>
            ) : (
              <Button
                appearance="primary"
                icon={<SparkleRegular />}
                disabled={!value.trim()}
                onClick={onSubmit}
              >
                {submitLabel}
              </Button>
            )}
            <Text className="azf-inline-copilot__disclaimer">AI-generated content may be incorrect</Text>
          </div>
        </div>
      </PopoverSurface>
    </Popover>
  );
}

export interface ArtifactPillProps {
  artifact: AzfArtifact;
}

export function ArtifactPill({ artifact }: ArtifactPillProps) {
  // Anatomy per Figma ".Artifact pill (CoT)" node 27865:11293 (file q2TdO4dVcMhNWYp0N6Bc05):
  // 34px white pill, colorNeutralStroke1 border, 8px (XLarge) radius; 20px leading icon; title
  // (Body1 14/20) and type (Caption1 12/16 colorNeutralForeground3) INLINE side-by-side; trailing
  // 20px arrow-maximize (expand) icon — not a stacked layout with an arrow-right.
  return (
    <button type="button" className="azf-row azf-gap-s azf-artifact-pill" onClick={artifact.onOpen}>
      <span className="azf-artifact-pill__icon" aria-hidden="true">{artifact.icon ?? <DocumentRegular />}</span>
      <span className="azf-row azf-artifact-pill__content">
        <span className="azf-artifact-pill__title">{artifact.title}</span>
        {artifact.type && <span className="azf-artifact-pill__type">{artifact.type}</span>}
      </span>
      <span className="azf-artifact-pill__open" aria-hidden="true">
        <ArrowMaximizeRegular />
      </span>
    </button>
  );
}

export interface AgenticProgressProps {
  steps: AzfAgentStep[];
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
  defaultOpenItems?: string[];
  openItems?: string[];
  onToggleItems?: (openItems: string[]) => void;
  className?: string;
}

function stepTone(step: AzfAgentStep): AzfTone {
  if (step.status === 'complete') return 'success';
  if (step.status === 'warning' || step.needsInput) return 'warning';
  if (step.status === 'error' || step.status === 'blocked') return 'danger';
  return 'info';
}

export function AgenticProgress({ steps, onApprove, onDeny, defaultOpenItems, openItems, onToggleItems, className }: AgenticProgressProps) {
  return (
    <Accordion
      multiple
      collapsible
      openItems={openItems}
      defaultOpenItems={openItems === undefined ? defaultOpenItems : undefined}
      onToggle={(_event, data) => onToggleItems?.(data.openItems as string[])}
      className={mergeClasses('azf-stack azf-agentic-list', className)}
    >
      {steps.map((step) => (
        <AccordionItem key={step.id} value={step.id}>
          <AccordionHeader icon={step.status === 'running' ? <span className="azf-copilot-loader" aria-hidden="true" /> : <StatusIconText status={stepTone(step)} />}>
            <div className="azf-row azf-agentic-step__header">
              <span>{step.title}</span>
              <Badge appearance="tint" color={stepTone(step) === 'danger' ? 'danger' : stepTone(step) === 'warning' ? 'warning' : stepTone(step) === 'success' ? 'success' : 'informative'}>
                {step.needsInput ? 'Needs input' : step.status ?? 'pending'}
              </Badge>
            </div>
          </AccordionHeader>
          <AccordionPanel>
            <div className="azf-stack azf-gap-s azf-agentic-step" data-status={step.status ?? 'pending'}>
              {/* Running indicator uses the shared Copilot loader bar (Figma CoT running row
                  node 386:75129 / Inline "endless" bar node 29192:8246), not a generic blue bar. */}
              {step.status === 'running' && <span className="azf-copilot-loader-bar" aria-hidden="true" />}
              {step.body && <Text>{step.body}</Text>}
              {step.needsInput && (
                <MessageBar intent="warning" className="azf-agentic-step__risk">
                  <MessageBarBody>{step.riskText}</MessageBarBody>
                  <MessageBarActions>
                    <Button appearance="primary" onClick={() => onApprove?.(step.id)}>Approve</Button>
                    <Button onClick={() => onDeny?.(step.id)}>Deny</Button>
                  </MessageBarActions>
                </MessageBar>
              )}
              {step.artifacts?.map((artifact) => <ArtifactPill key={artifact.id} artifact={artifact} />)}
            </div>
          </AccordionPanel>
        </AccordionItem>
      ))}
    </Accordion>
  );
}

export interface ChainOfThoughtProps {
  title?: ReactNode;
  subtitle?: ReactNode;
  steps: AzfAgentStep[];
  artifacts?: AzfArtifact[];
  defaultExpanded?: boolean;
  defaultTab?: 'activity' | 'artifacts';
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
  className?: string;
}

const COT_BADGE_TONE: Record<NonNullable<AzfStepBadge['tone']>, string> = {
  success: 'azf-cot__badge--success',
  warning: 'azf-cot__badge--warning',
  danger: 'azf-cot__badge--danger',
  info: 'azf-cot__badge--info',
};

// Status glyph on the LEFT of each step row, mapped from the Figma "Status v2" / "Action Status"
// symbols: neutral circle-check when complete, the Copilot loader dot while running (Figma
// "Circle_Loader"), a filled warning triangle when the step needs input, an error circle on
// failure, and a hollow circle when pending.
function cotStatusIcon(step: AzfAgentStep): ReactNode {
  if (step.needsInput || step.status === 'warning') {
    return <WarningFilled className="azf-cot__status azf-cot__status--warning" aria-hidden="true" />;
  }
  if (step.status === 'error' || step.status === 'blocked') {
    return <ErrorCircleRegular className="azf-cot__status azf-cot__status--danger" aria-hidden="true" />;
  }
  if (step.status === 'running') {
    return <span className="azf-cot__status azf-cot__status--running" aria-hidden="true"><span className="azf-copilot-loader" /></span>;
  }
  if (step.status === 'complete') {
    return <CheckmarkCircleRegular className="azf-cot__status azf-cot__status--complete" aria-hidden="true" />;
  }
  return <CircleRegular className="azf-cot__status azf-cot__status--pending" aria-hidden="true" />;
}

interface CotStepRowProps {
  step: AzfAgentStep;
  open: boolean;
  onToggle: () => void;
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
}

function CotStepRow({ step, open, onToggle, onApprove, onDeny }: CotStepRowProps) {
  const hasSub = Boolean(step.body || step.needsInput);
  const titleId = `azf-cot-step-${step.id}`;

  return (
    <li className="azf-cot__step" data-status={step.status ?? 'pending'}>
      <div className="azf-cot__step-row">
        <span className="azf-cot__step-icon">{cotStatusIcon(step)}</span>
        <span className="azf-cot__step-main">
          <Text id={titleId} size={300} className="azf-cot__step-title">{step.title}</Text>
          {step.badge && (
            <span className={mergeClasses('azf-cot__badge', COT_BADGE_TONE[step.badge.tone ?? 'info'])}>
              {step.badge.label}
            </span>
          )}
        </span>
        {hasSub && (
          <Button
            appearance="subtle"
            size="small"
            shape="circular"
            className="azf-cot__step-toggle"
            icon={open ? <ChevronDownRegular /> : <ChevronRightRegular />}
            aria-expanded={open}
            aria-describedby={titleId}
            aria-label={open ? 'Collapse step' : 'Expand step'}
            onClick={onToggle}
          />
        )}
      </div>

      {hasSub && open && (
        <div className="azf-cot__step-sub">
          {step.needsInput ? (
            <div className="azf-stack azf-cot__approval">
              {step.body && <Text size={300} className="azf-cot__approval-body">{step.body}</Text>}
              {step.disclaimer && (
                <Text size={200} className="azf-cot__approval-note">{step.disclaimer}</Text>
              )}
              <div className="azf-row azf-cot__approval-actions">
                <Button appearance="primary" onClick={() => onApprove?.(step.id)}>
                  {step.approveLabel ?? 'Approve'}
                </Button>
                <Button appearance="secondary" onClick={() => onDeny?.(step.id)}>
                  {step.denyLabel ?? 'Deny'}
                </Button>
              </div>
            </div>
          ) : (
            step.body && <Text size={300} className="azf-cot__step-body">{step.body}</Text>
          )}
        </div>
      )}
    </li>
  );
}

function CotArtifactRow({ artifact }: { artifact: AzfArtifact }) {
  const metaParts = [artifact.type, artifact.size].filter(Boolean);
  return (
    <li className="azf-cot__artifact">
      <span className="azf-cot__artifact-icon" aria-hidden="true">{artifact.icon ?? <DocumentRegular />}</span>
      <span className="azf-cot__artifact-main">
        <Text size={300} weight="semibold" className="azf-cot__artifact-name">{artifact.title}</Text>
        {metaParts.length > 0 && (
          <Text size={200} className="azf-cot__artifact-meta">
            {metaParts.map((part, index) => (
              <Fragment key={index}>
                {index > 0 && <span aria-hidden="true"> · </span>}
                {part}
              </Fragment>
            ))}
          </Text>
        )}
      </span>
      {artifact.onDownload && (
        <Tooltip content="Download" relationship="label">
          <Button appearance="subtle" size="small" icon={<ArrowDownloadRegular />} onClick={artifact.onDownload} aria-label="Download" />
        </Tooltip>
      )}
      <Tooltip content="Open" relationship="label">
        <Button appearance="subtle" size="small" icon={<OpenRegular />} onClick={artifact.onOpen} aria-label="Open" />
      </Tooltip>
    </li>
  );
}

// First-class rebuild of the Azure UI Kit "Chain of thought" spec (Figma file oqjy7GlpGqEQgUwMCs1wdq,
// nodes 17:42005 / 386:75088 / 17:42903 / 17:55334). Owns its own step-row and artifact-row markup
// instead of routing through AgenticProgress: status icon on the LEFT, disclosure chevron on the
// RIGHT, semantic badges only where the spec shows them, and an inline (not MessageBar) approval
// block with body + disclaimer + primary Approve / secondary Deny. Every value maps to a Fluent v9
// token harvested from the design's get_design_context / get_variable_defs output.
export function ChainOfThought({
  title = 'Reasoning',
  subtitle,
  steps,
  artifacts,
  defaultExpanded = true,
  defaultTab = 'activity',
  onApprove,
  onDeny,
  className,
}: ChainOfThoughtProps) {
  const [expanded, setExpanded] = useState(defaultExpanded);
  const [tab, setTab] = useState<'activity' | 'artifacts'>(defaultTab);
  const [openSteps, setOpenSteps] = useState<string[]>(() =>
    steps.filter((step) => step.defaultOpen || step.needsInput || step.status === 'running').map((step) => step.id),
  );

  const completed = steps.filter((step) => step.status === 'complete').length;
  const allOpen = steps.length > 0 && openSteps.length === steps.length;
  const hasArtifacts = artifacts !== undefined;
  const showActivity = !hasArtifacts || tab === 'activity';

  const toggleStep = (id: string) =>
    setOpenSteps((current) => (current.includes(id) ? current.filter((value) => value !== id) : [...current, id]));

  return (
    <section
      className={mergeClasses('azf-stack azf-cot', className)}
      aria-label={typeof title === 'string' ? title : 'Chain of thought'}
    >
      <header className="azf-row azf-cot__header">
        <div className="azf-stack azf-cot__heading">
          <Text size={600} weight="semibold" className="azf-cot__title">{title}</Text>
          {subtitle && <Text size={300} className="azf-cot__subtitle">{subtitle}</Text>}
        </div>
        <Button
          appearance="secondary"
          className="azf-cot__toggle"
          icon={expanded ? <ArrowMinimizeVerticalRegular /> : <ArrowMaximizeVerticalRegular />}
          aria-expanded={expanded}
          onClick={() => setExpanded((value) => !value)}
        >
          {expanded ? 'Hide activity' : 'Show activity'}
        </Button>
      </header>

      {expanded && (
        <>
          {hasArtifacts && (
            <TabList
              className="azf-cot__tabs"
              selectedValue={tab}
              onTabSelect={(_event, data) => setTab(data.value as 'activity' | 'artifacts')}
              aria-label="Chain of thought views"
            >
              <Tab value="activity">Activity</Tab>
              <Tab value="artifacts">Artifacts</Tab>
            </TabList>
          )}

          {showActivity && (
            <>
              <div className="azf-row azf-cot__summary">
                <Text size={200} className="azf-cot__summary-text">
                  {completed} actions completed
                </Text>
                {steps.length > 0 && (
                  <>
                    <span className="azf-cot__summary-sep" aria-hidden="true">•</span>
                    <Button
                      appearance="subtle"
                      size="small"
                      className="azf-cot__summary-toggle"
                      icon={allOpen ? <ChevronDownRegular /> : <ChevronRightRegular />}
                      iconPosition="after"
                      onClick={() => setOpenSteps(allOpen ? [] : steps.map((step) => step.id))}
                    >
                      {allOpen ? 'Collapse' : 'Show all'}
                    </Button>
                  </>
                )}
              </div>
              <ol className="azf-cot__steps">
                {steps.map((step) => (
                  <CotStepRow
                    key={step.id}
                    step={step}
                    open={openSteps.includes(step.id)}
                    onToggle={() => toggleStep(step.id)}
                    onApprove={onApprove}
                    onDeny={onDeny}
                  />
                ))}
              </ol>
            </>
          )}

          {hasArtifacts && tab === 'artifacts' && (
            artifacts!.length > 0 ? (
              <ul className="azf-cot__artifact-list">
                {artifacts!.map((artifact) => (
                  <CotArtifactRow key={artifact.id} artifact={artifact} />
                ))}
              </ul>
            ) : (
              <Text size={300} className="azf-cot__subtitle">No artifacts created yet.</Text>
            )
          )}
        </>
      )}
    </section>
  );
}

export interface AzureTab {
  id: string;
  label: ReactNode;
  description?: ReactNode;
  icon?: ReactElement;
  ariaLabel?: string;
  testId?: string;
  status?: 'error' | 'success' | 'warning';
  disabled?: boolean;
}

export interface AzureTabListProps {
  tabs: AzureTab[];
  selectedValue: string;
  onTabSelect?: (value: string) => void;
  orientation?: 'horizontal' | 'vertical';
  ariaLabel?: string;
  className?: string;
}

export function AzureTabList({ tabs, selectedValue, onTabSelect, orientation = 'horizontal', ariaLabel, className }: AzureTabListProps) {
  return (
    <TabList
      className={mergeClasses('azf-tabs', className)}
      vertical={orientation === 'vertical'}
      selectedValue={selectedValue}
      onTabSelect={(_, data) => onTabSelect?.(String(data.value))}
      aria-label={ariaLabel}
    >
      {tabs.map((tab) => {
        const statusIcon = tab.status === 'error'
          ? <ErrorCircleRegular />
          : tab.status === 'success'
            ? <CheckmarkCircleRegular />
            : tab.status === 'warning'
              ? <WarningRegular />
              : undefined;

        return (
          <Tab
            key={tab.id}
            value={tab.id}
            disabled={tab.disabled}
            icon={tab.icon ?? statusIcon}
            aria-label={tab.ariaLabel ?? optionText(tab.label)}
            data-testid={tab.testId}
          >
            <span className={mergeClasses('azf-tab__content', Boolean(tab.description) && 'azf-tab__content--with-description')}>
              <span className="azf-tab__label">{tab.label}</span>
              {tab.description && <span className="azf-tab__description">{tab.description}</span>}
            </span>
          </Tab>
        );
      })}
    </TabList>
  );
}

export interface AzureStep {
  id: string;
  label: ReactNode;
  description?: ReactNode;
  status?: 'default' | 'complete' | 'warning' | 'error';
  disabled?: boolean;
}

export interface AzureStepListProps {
  steps: AzureStep[];
  selectedValue: string;
  onStepSelect?: (value: string) => void;
  orientation?: 'horizontal' | 'vertical';
  className?: string;
  ariaLabel?: string;
}

export function AzureStepList({
  steps,
  selectedValue,
  onStepSelect,
  orientation = 'horizontal',
  className,
  ariaLabel = 'Steps',
}: AzureStepListProps) {
  return (
    <AzureTabList
      className={mergeClasses('azf-step-list', className)}
      selectedValue={selectedValue}
      onTabSelect={onStepSelect}
      orientation={orientation}
      ariaLabel={ariaLabel}
      tabs={steps.map((step, index) => ({
        id: step.id,
        label: (
          <span className="azf-step-list__label">
            <span className="azf-step-list__index" aria-hidden="true">{index + 1}</span>
            <span>{step.label}</span>
          </span>
        ),
        description: step.description,
        disabled: step.disabled,
        ariaLabel: typeof step.label === 'string' ? `Step ${index + 1}: ${step.label}` : `Step ${index + 1}`,
        status: step.status === 'complete' ? 'success' : step.status === 'warning' ? 'warning' : step.status === 'error' ? 'error' : undefined,
      }))}
    />
  );
}

export interface HelpPopoverProps {
  trigger: ReactElement;
  title?: ReactNode;
  body: ReactNode;
  actions?: AzfAction[];
  tone?: 'light' | 'brand' | 'dark';
}

export function HelpPopover({ trigger, title, body, actions = [], tone = 'light' }: HelpPopoverProps) {
  const titleId = useId();
  return (
    <Popover withArrow>
      <PopoverTrigger disableButtonEnhancement>{trigger}</PopoverTrigger>
      <PopoverSurface className="azf-stack azf-popover-content" data-tone={tone} aria-labelledby={title ? titleId : undefined}>
        {title && <Text id={titleId} weight="semibold">{title}</Text>}
        <Text>{body}</Text>
        {actions.length > 0 && <div className="azf-row azf-gap-s">{actions.map((action) => renderAction(action))}</div>}
      </PopoverSurface>
    </Popover>
  );
}

export const CalloutPopover = HelpPopover;

export interface AzureFormProps {
  children: ReactNode;
  message?: ReactNode;
  footer?: ReactNode;
  className?: string;
  onSubmit?: () => void;
}

export function AzureForm({ children, message, footer, className, onSubmit }: AzureFormProps) {
  return (
    <form
      className={mergeClasses('azf-stack azf-gap-m', className)}
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit?.();
      }}
    >
      {message && <MessageBar><MessageBarBody>{message}</MessageBarBody></MessageBar>}
      {children}
      {footer}
    </form>
  );
}

export interface DeleteResourceDialogProps {
  resourceName: string;
  trigger: ReactElement;
  softDelete?: boolean;
  confirmationText?: ReactNode;
  consequences?: ReactNode[];
  acknowledgement?: {
    label: CheckboxProps['label'];
    checked: boolean;
    onChange?: (checked: boolean) => void;
  };
  confirmLabel?: string;
  cancelLabel?: string;
  confirming?: boolean;
  onConfirm?: () => void;
  onCancel?: () => void;
}

export function DeleteResourceDialog({
  resourceName,
  trigger,
  softDelete,
  confirmationText,
  consequences = [],
  acknowledgement,
  confirmLabel = 'Delete',
  cancelLabel = 'Cancel',
  confirming,
  onConfirm,
  onCancel,
}: DeleteResourceDialogProps) {
  const confirmDisabled = confirming || (acknowledgement ? !acknowledgement.checked : false);

  return (
    <Dialog>
      <DialogTrigger disableButtonEnhancement>{trigger}</DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Delete {resourceName}</DialogTitle>
          <DialogContent className="azf-stack azf-gap-s azf-delete-dialog__content">
            <StatusIconText status="danger" icon={<WarningRegular />}>
              {softDelete ? 'This resource can be recovered for a limited time.' : 'This action may permanently remove the resource.'}
            </StatusIconText>
            <Text>{confirmationText ?? 'Review dependencies and saved work before continuing.'}</Text>
            {consequences.length > 0 && (
              <ul className="azf-stack azf-gap-xs azf-delete-dialog__consequences">
                {consequences.map((consequence, index) => <li key={index}>{consequence}</li>)}
              </ul>
            )}
            {acknowledgement && (
              <Checkbox
                checked={acknowledgement.checked}
                onChange={(_, data) => acknowledgement.onChange?.(Boolean(data.checked))}
                label={acknowledgement.label}
              />
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" icon={<DeleteRegular />} disabled={confirmDisabled} onClick={onConfirm}>
              {confirmLabel}
            </Button>
            <DialogTrigger disableButtonEnhancement>
              <Button disabled={confirming} onClick={onCancel}>{cancelLabel}</Button>
            </DialogTrigger>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export const DeleteConfirmationDialog = DeleteResourceDialog;

// --- Azure Slider (Figma 28472:10338) ---------------------------------------
export interface AzureSliderProps {
  value?: number;
  defaultValue?: number;
  min?: number;
  max?: number;
  step?: number;
  onChange?: (value: number) => void;
  label?: ReactNode;
  info?: ReactNode;
  showValue?: boolean;
  formatValue?: (value: number) => string;
  disabled?: boolean;
  vertical?: boolean;
  id?: string;
  className?: string;
  ariaLabel?: string;
}

export function AzureSlider({
  value,
  defaultValue,
  min = 0,
  max = 100,
  step,
  onChange,
  label,
  info,
  showValue = false,
  formatValue,
  disabled,
  vertical,
  id,
  className,
  ariaLabel,
}: AzureSliderProps) {
  const generatedId = useId();
  const sliderId = id ?? generatedId;
  const [internal, setInternal] = useState(defaultValue ?? min);
  const current = value ?? internal;
  const displayValue = formatValue ? formatValue(current) : String(current);
  const resolvedAriaLabel = ariaLabel ?? (typeof label === 'string' ? label : undefined);

  const handleChange = (next: number) => {
    if (value === undefined) setInternal(next);
    onChange?.(next);
  };

  return (
    <div className={mergeClasses('azf-stack azf-gap-xs azf-slider', vertical && 'azf-slider--vertical', className)}>
      {label && (
        <div className="azf-row azf-gap-s azf-slider__labelline">
          {info ? (
            <InfoLabel htmlFor={sliderId} info={info}>{label}</InfoLabel>
          ) : (
            <Label htmlFor={sliderId}>{label}</Label>
          )}
          {showValue && <Text className="azf-slider__value azf-muted">{displayValue}</Text>}
        </div>
      )}
      <Slider
        id={sliderId}
        className="azf-slider__control"
        min={min}
        max={max}
        step={step}
        value={current}
        vertical={vertical}
        disabled={disabled}
        aria-label={resolvedAriaLabel}
        aria-valuetext={showValue ? displayValue : undefined}
        onChange={(_, data) => handleChange(data.value)}
      />
      {!label && showValue && <Text className="azf-slider__value azf-muted">{displayValue}</Text>}
    </div>
  );
}

// --- Progress bar with labels (Figma 28174:7417 / animated 28209:4560) -------
export interface ProgressBarWithLabelProps {
  value?: number;
  max?: number;
  indeterminate?: boolean;
  label?: ReactNode;
  info?: ReactNode;
  description?: ReactNode;
  required?: boolean;
  thickness?: 'medium' | 'large';
  id?: string;
  className?: string;
}

export function ProgressBarWithLabel({
  value,
  max = 1,
  indeterminate,
  label,
  info,
  description,
  required,
  thickness = 'medium',
  id,
  className,
}: ProgressBarWithLabelProps) {
  const generatedId = useId();
  const barId = id ?? generatedId;
  const isIndeterminate = indeterminate || value === undefined;
  const descId = description ? `${barId}-desc` : undefined;

  return (
    <div className={mergeClasses('azf-stack azf-progress-field', className)}>
      {label && (
        <div className="azf-row azf-gap-xs azf-progress-field__labelline">
          {info ? (
            <InfoLabel htmlFor={barId} info={info}>{label}</InfoLabel>
          ) : (
            <Label htmlFor={barId}>{label}</Label>
          )}
          {required && <span className="azf-progress-field__required" aria-hidden="true">*</span>}
        </div>
      )}
      <ProgressBar
        id={barId}
        className="azf-progress-field__bar"
        thickness={thickness}
        shape="rounded"
        max={isIndeterminate ? undefined : max}
        value={isIndeterminate ? undefined : value}
        aria-label={typeof label === 'string' ? label : undefined}
        aria-describedby={descId}
      />
      {description && (
        <Text id={descId} className="azf-progress-field__description">{description}</Text>
      )}
    </div>
  );
}

// --- File upload (Figma 25412:31783) ----------------------------------------
export interface FileUploadProps {
  label?: ReactNode;
  state?: AzfFileUploadState;
  fileName?: string;
  placeholder?: string;
  progress?: number;
  buttonLabel?: string;
  multiple?: boolean;
  disabled?: boolean;
  onSelectFiles?: (files: FileList | null) => void;
  onUpload?: () => void;
  onRemove?: () => void;
  id?: string;
  className?: string;
}

export function FileUpload({
  label = 'Upload file',
  state = 'default',
  fileName,
  placeholder = 'Select File',
  progress = 0,
  buttonLabel = 'Upload File',
  multiple,
  disabled,
  onSelectFiles,
  onUpload,
  onRemove,
  id,
  className,
}: FileUploadProps) {
  const generatedId = useId();
  const inputId = id ?? generatedId;
  const fileInputRef = useRef<HTMLInputElement>(null);
  const openPicker = () => fileInputRef.current?.click();

  const showProgress = state === 'progress';
  const showSuccess = state === 'success';
  const isDragDrop = state === 'dragdrop';
  const labelText = typeof label === 'string' ? label : 'File';

  return (
    <div className={mergeClasses('azf-file-upload', className)} data-state={state}>
      <Label htmlFor={inputId} className="azf-file-upload__label">{label}</Label>
      <div className="azf-stack azf-gap-xs azf-file-upload__control">
        <input
          ref={fileInputRef}
          id={inputId}
          type="file"
          multiple={multiple}
          className="azf-file-upload__native"
          disabled={disabled}
          onChange={(event) => onSelectFiles?.(event.currentTarget.files)}
        />
        {isDragDrop ? (
          <div
            className="azf-file-upload__dropzone"
            role="button"
            tabIndex={0}
            onClick={openPicker}
            onKeyDown={(event) => {
              if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                openPicker();
              }
            }}
            onDragOver={(event) => event.preventDefault()}
            onDrop={(event) => {
              event.preventDefault();
              onSelectFiles?.(event.dataTransfer.files);
            }}
          >
            <ArrowUploadRegular className="azf-file-upload__dropicon" aria-hidden="true" />
            <Text>Drag files here or <span className="azf-linkish">browse</span></Text>
          </div>
        ) : (
          <Input
            className="azf-file-upload__input"
            readOnly
            value={fileName ?? ''}
            placeholder={placeholder}
            disabled={disabled}
            aria-label={labelText}
            contentAfter={
              showSuccess ? (
                <CheckmarkCircleRegular className="azf-file-upload__success-icon" aria-label="Upload complete" />
              ) : (
                <Button
                  appearance="transparent"
                  size="small"
                  icon={<FolderSearchRegular />}
                  aria-label="Browse for file"
                  disabled={disabled}
                  onClick={openPicker}
                />
              )
            }
          />
        )}
        {showProgress ? (
          <div className="azf-row azf-gap-s azf-file-upload__progressrow">
            <ProgressBar
              className="azf-file-upload__progress"
              thickness="medium"
              shape="rounded"
              value={progress}
              max={1}
              aria-label="Upload progress"
            />
            <Button appearance="subtle" size="small" icon={<DismissRegular />} aria-label="Cancel upload" onClick={onRemove} />
          </div>
        ) : (
          !isDragDrop && (
            <Button
              className="azf-file-upload__button"
              appearance="primary"
              icon={<ArrowUploadRegular />}
              disabled={disabled}
              onClick={onUpload}
            >
              {buttonLabel}
            </Button>
          )
        )}
      </div>
    </div>
  );
}

// --- Filterable combo box (Figma 25248:8173) --------------------------------
export interface FilterableComboBoxProps {
  options: AzfOption[];
  value?: string;
  selectedOptions?: string[];
  onSelect?: (id: string | undefined) => void;
  onSelectionChange?: (ids: string[]) => void;
  multiselect?: boolean;
  size?: 'small' | 'medium' | 'large';
  placeholder?: string;
  label?: ReactNode;
  info?: ReactNode;
  disabled?: boolean;
  freeform?: boolean;
  id?: string;
  className?: string;
  ariaLabel?: string;
}

export function FilterableComboBox({
  options,
  value,
  selectedOptions,
  onSelect,
  onSelectionChange,
  multiselect = false,
  size = 'medium',
  placeholder = 'Select an option',
  label,
  info,
  disabled,
  freeform = true,
  id,
  className,
  ariaLabel,
}: FilterableComboBoxProps) {
  const generatedId = useId();
  const comboId = id ?? generatedId;
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);

  const selectedIds = multiselect ? (selectedOptions ?? []) : value ? [value] : [];
  const selectedText = value ? optionText(options.find((option) => option.id === value)?.label ?? '') : '';

  const filtered = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    if (!normalized || !freeform) return options;
    return options.filter((option) => optionText(option.label).toLowerCase().includes(normalized));
  }, [options, query, freeform]);

  const displayValue = multiselect ? undefined : open ? query : selectedText;
  const resolvedAriaLabel = ariaLabel ?? (typeof label === 'string' ? label : undefined);

  const combo = (
    <Combobox
      id={comboId}
      className={mergeClasses('azf-combobox', className)}
      size={size}
      placeholder={placeholder}
      disabled={disabled}
      multiselect={multiselect}
      freeform={freeform}
      selectedOptions={selectedIds}
      value={displayValue}
      aria-label={resolvedAriaLabel}
      onOpenChange={(_, data) => {
        setOpen(data.open);
        if (!data.open) setQuery('');
      }}
      onChange={(event) => setQuery(event.target.value)}
      onOptionSelect={(_, data) => {
        if (multiselect) {
          onSelectionChange?.(data.selectedOptions);
        } else {
          onSelect?.(data.optionValue);
          setQuery('');
        }
      }}
    >
      {filtered.length === 0 ? (
        <Option key="__empty" text="" disabled>No matches found</Option>
      ) : (
        filtered.map((option) => (
          <Option key={option.id} value={option.id} text={optionText(option.label)} disabled={option.disabled}>
            {option.icon}
            {option.label}
          </Option>
        ))
      )}
    </Combobox>
  );

  if (!label) return combo;

  return (
    <div className="azf-stack azf-gap-xs">
      {info ? (
        <InfoLabel htmlFor={comboId} info={info}>{label}</InfoLabel>
      ) : (
        <Label htmlFor={comboId}>{label}</Label>
      )}
      {combo}
    </div>
  );
}

// --- Azure toolbar (Figma 29553:7576) ---------------------------------------
export interface AzureToolbarProps {
  actions: AzfAction[];
  topOfPage?: boolean;
  size?: 'small' | 'medium' | 'large';
  ariaLabel?: string;
  className?: string;
  children?: ReactNode;
}

export function AzureToolbar({
  actions,
  topOfPage = false,
  size = 'medium',
  ariaLabel = 'Toolbar',
  className,
  children,
}: AzureToolbarProps) {
  return (
    <Toolbar
      aria-label={ariaLabel}
      size={size}
      className={mergeClasses('azf-toolbar', topOfPage && 'azf-toolbar--top-of-page', className)}
    >
      {children}
      {actions.map((action) =>
        action.id === 'divider' || action.label === '|' ? (
          <ToolbarDivider key={action.id} />
        ) : (
          <ToolbarButton
            key={action.id}
            icon={action.loading ? <Spinner size="tiny" /> : action.icon}
            appearance={action.appearance === 'primary' ? 'primary' : 'subtle'}
            disabled={action.disabled || action.loading}
            onClick={action.onClick}
          >
            {action.label}
          </ToolbarButton>
        ),
      )}
    </Toolbar>
  );
}

export interface AzfFilterPill {
  id: string;
  label: string;
}

export interface FilterPillsProps {
  pills: AzfFilterPill[];
  selectedIds: string[];
  onToggle?: (id: string) => void;
  overflowPills?: AzfFilterPill[];
  overflowLabel?: string;
  ariaLabel?: string;
  className?: string;
}

export function FilterPills({
  pills,
  selectedIds,
  onToggle,
  overflowPills = [],
  overflowLabel = 'Filters',
  ariaLabel = 'Filter results',
  className,
}: FilterPillsProps) {
  const selected = new Set(selectedIds);
  const overflowSelectedCount = overflowPills.filter((pill) => selected.has(pill.id)).length;

  return (
    <div className={mergeClasses('azf-filter-pills', className)} role="group" aria-label={ariaLabel}>
      {pills.map((pill) => (
        <ToggleButton
          key={pill.id}
          size="small"
          className="azf-filter-pill"
          checked={selected.has(pill.id)}
          onClick={() => onToggle?.(pill.id)}
        >
          {pill.label}
        </ToggleButton>
      ))}
      {overflowPills.length > 0 && (
        <Popover positioning="below-start" withArrow>
          <PopoverTrigger disableButtonEnhancement>
            <ToggleButton
              size="small"
              className="azf-filter-pill azf-filter-pill--overflow"
              icon={<AddRegular />}
              checked={overflowSelectedCount > 0}
            >
              {overflowSelectedCount > 0 ? `${overflowLabel} (${overflowSelectedCount})` : overflowLabel}
            </ToggleButton>
          </PopoverTrigger>
          <PopoverSurface>
            <div className="azf-stack azf-gap-s azf-filter-pills__overflow">
              {overflowPills.map((pill) => (
                <Checkbox
                  key={pill.id}
                  label={pill.label}
                  checked={selected.has(pill.id)}
                  onChange={() => onToggle?.(pill.id)}
                />
              ))}
            </div>
          </PopoverSurface>
        </Popover>
      )}
    </div>
  );
}

export interface AzfEssentialProperty {
  id: string;
  label: string;
  value: ReactNode;
  href?: string;
  tags?: string[];
}

export interface EssentialsGridProps {
  properties: AzfEssentialProperty[];
  title?: string;
  defaultOpen?: boolean;
  columns?: 1 | 2;
  className?: string;
}

export function EssentialsGrid({
  properties,
  title = 'Essentials',
  defaultOpen = true,
  columns = 2,
  className,
}: EssentialsGridProps) {
  return (
    <Accordion
      collapsible
      defaultOpenItems={defaultOpen ? ['essentials'] : []}
      className={mergeClasses('azf-essentials', className)}
    >
      <AccordionItem value="essentials">
        <AccordionHeader expandIconPosition="end">
          <Text weight="semibold">{title}</Text>
        </AccordionHeader>
        <AccordionPanel>
          <dl className={mergeClasses('azf-essentials-grid', columns === 1 && 'azf-essentials-grid--single')}>
            {properties.map((property) => (
              <div className="azf-essentials-grid__cell" key={property.id}>
                <dt className="azf-essentials-grid__label azf-muted">{property.label}</dt>
                <dd className="azf-essentials-grid__value">
                  {property.href ? (
                    <Link href={property.href}>{property.value}</Link>
                  ) : (
                    property.value
                  )}
                  {property.tags && property.tags.length > 0 && (
                    <span className="azf-essentials-grid__tags">
                      {property.tags.map((tag) => (
                        <Tag key={tag} size="small" appearance="brand">
                          {tag}
                        </Tag>
                      ))}
                    </span>
                  )}
                </dd>
              </div>
            ))}
          </dl>
        </AccordionPanel>
      </AccordionItem>
    </Accordion>
  );
}

export { Button, Card, Field, Input, Label, Link, MessageBar, ProgressBar, Slider, Text };
