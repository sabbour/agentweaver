import type { HTMLAttributes, ReactNode } from 'react';
import { Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  page: {
    width: '100%',
    maxWidth: '1480px',
    margin: '0 auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXXL,
    minWidth: 0,
    '@media (max-width: 720px)': {
      gap: tokens.spacingVerticalXL,
    },
  },
  pageFullHeight: {
    height: '100%',
    minHeight: 0,
  },
  surface: {
    minWidth: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow2,
  },
  surfaceRaised: {
    boxShadow: tokens.shadow4,
  },
  surfaceSubtle: {
    backgroundColor: tokens.colorNeutralBackground2,
    boxShadow: 'none',
  },
  surfaceFlat: {
    boxShadow: 'none',
  },
  paddingCompact: {
    padding: tokens.spacingVerticalM,
  },
  paddingComfortable: {
    padding: tokens.spacingVerticalL,
  },
  paddingSpacious: {
    padding: tokens.spacingVerticalXL,
    '@media (max-width: 720px)': {
      padding: tokens.spacingVerticalM,
    },
  },
  commandStrip: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  sectionHeader: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  sectionTitleGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  sectionTitle: {
    display: 'block',
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    overflowWrap: 'anywhere',
  },
  sectionDescription: {
    display: 'block',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '72ch',
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    color: tokens.colorNeutralForeground2,
    textAlign: 'center',
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    boxShadow: 'none',
  },
  emptyTitle: {
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  emptyBody: {
    maxWidth: '56ch',
    lineHeight: tokens.lineHeightBase300,
  },
});

type SurfaceTone = 'default' | 'raised' | 'subtle' | 'flat';
type SurfaceDensity = 'compact' | 'comfortable' | 'spacious';

export interface AzurePageProps extends HTMLAttributes<HTMLDivElement> {
  fullHeight?: boolean;
}

export function AzurePage({ className, fullHeight, ...props }: AzurePageProps) {
  const styles = useStyles();
  return (
    <div
      {...props}
      className={mergeClasses(styles.page, fullHeight && styles.pageFullHeight, className)}
    />
  );
}

export interface AzureSurfaceProps extends HTMLAttributes<HTMLDivElement> {
  tone?: SurfaceTone;
  density?: SurfaceDensity;
}

export function AzureSurface({
  className,
  tone = 'default',
  density = 'comfortable',
  ...props
}: AzureSurfaceProps) {
  const styles = useStyles();
  return (
    <div
      {...props}
      className={mergeClasses(
        styles.surface,
        tone === 'raised' && styles.surfaceRaised,
        tone === 'subtle' && styles.surfaceSubtle,
        tone === 'flat' && styles.surfaceFlat,
        density === 'compact' && styles.paddingCompact,
        density === 'comfortable' && styles.paddingComfortable,
        density === 'spacious' && styles.paddingSpacious,
        className,
      )}
    />
  );
}

export interface AzureSectionHeaderProps extends Omit<HTMLAttributes<HTMLDivElement>, 'title'> {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
}

export function AzureSectionHeader({
  title,
  description,
  actions,
  className,
  ...props
}: AzureSectionHeaderProps) {
  const styles = useStyles();
  return (
    <div {...props} className={mergeClasses(styles.sectionHeader, className)}>
      <div className={styles.sectionTitleGroup}>
        <Text className={styles.sectionTitle}>{title}</Text>
        {description && <Text className={styles.sectionDescription}>{description}</Text>}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </div>
  );
}

export interface AzureEmptyStateProps extends Omit<AzureSurfaceProps, 'children' | 'title'> {
  title: ReactNode;
  body?: ReactNode;
  actions?: ReactNode;
  icon?: ReactNode;
}

export function AzureEmptyState({
  title,
  body,
  actions,
  icon,
  className,
  ...surfaceProps
}: AzureEmptyStateProps) {
  const styles = useStyles();
  return (
    <AzureSurface
      {...surfaceProps}
      role={surfaceProps.role ?? 'status'}
      className={mergeClasses(styles.emptyState, className)}
    >
      {icon}
      <Text className={styles.emptyTitle}>{title}</Text>
      {body && <Text className={styles.emptyBody}>{body}</Text>}
      {actions && <div className={styles.actions}>{actions}</div>}
    </AzureSurface>
  );
}

export function AzureCommandStrip({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  const styles = useStyles();
  return <AzureSurface {...props} density="spacious" tone="raised" className={mergeClasses(styles.commandStrip, className)} />;
}
