/**
 * Agentweaver shared UI pattern kit.
 *
 * The coherence contract for every page: import page-level patterns from here so
 * the whole app stays visually consistent and structurally can't reintroduce
 * Azure copy, uppercase eyebrows, blue, or hero-metric grids.
 *
 * Native @fluentui/react-components + @fluentui/react-icons only. Theme CSS vars
 * only. No copilot-fluent-system imports.
 */

// Typography — the one type convention.
export {
  TypeText,
  Display,
  Headline,
  TitleText,
  Body,
  Label,
  useTypographyStyles,
} from './typography';
export type { TypographyRole, TypeTone, TypeTextProps } from './typography';

// Page scaffolding.
export { PageContainer } from './PageContainer';
export type { PageContainerProps, PageWidth } from './PageContainer';
export { PageHeader } from './PageHeader';
export type { PageHeaderProps } from './PageHeader';
export { PageSection } from './PageSection';
export type { PageSectionProps } from './PageSection';

// Collections.
export { RichList, ListRow } from './RichList';
export type { RichListProps, ListRowProps } from './RichList';

// Metrics.
export { MetricRow, StatTile } from './Metric';
export type { MetricRowProps, StatTileProps, MetricItem } from './Metric';

// Status surfaces.
export { EmptyState, LoadingState, ErrorState } from './States';
export type { EmptyStateProps, LoadingStateProps, ErrorStateProps } from './States';

// Cards.
export { AppCard } from './AppCard';
export type { AppCardProps } from './AppCard';

// Dialog (existing — do not duplicate).
export { AppDialog } from './AppDialog';
export type { AppDialogProps, AppDialogPrimaryAction } from './AppDialog';
