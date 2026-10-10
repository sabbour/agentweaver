import { makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { PageHeader as KitPageHeader } from './ui';

// Shared header for every main page. Wraps the shared kit PageHeader so all
// importer pages stay coherent without any change on their side.
// Props: title, subtitle (→ description), actions, breadcrumb (→ breadcrumbs),
// resourceIcon / resourceLabel (rendered as a context row above the title).

export interface PageHeaderProps {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
  breadcrumb?: ReactNode;
  resourceIcon?: ReactNode;
  resourceLabel?: ReactNode;
}

const useStyles = makeStyles({
  resourceIdentity: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
    fontSize: '13px',
    lineHeight: '18px',
  },
});

export function PageHeader({
  title,
  subtitle,
  actions,
  breadcrumb,
  resourceIcon,
  resourceLabel,
}: PageHeaderProps) {
  const styles = useStyles();

  // When a resource identity is provided, render it above the breadcrumb trail
  // so both remain visible in the kit's breadcrumbs slot.
  const breadcrumbsNode: ReactNode =
    resourceIcon != null || resourceLabel != null ? (
      <>
        <span className={styles.resourceIdentity} aria-hidden="true">
          {resourceIcon}
          {resourceLabel}
        </span>
        {breadcrumb}
      </>
    ) : breadcrumb;

  return (
    <KitPageHeader
      title={title}
      description={subtitle}
      breadcrumbs={breadcrumbsNode}
      actions={actions}
    />
  );
}
