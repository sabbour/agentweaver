import { AzureToolbar, BladeHeader } from '../copilot-fluent-system';
import type { ReactNode } from 'react';
// Shared header for every main page: a flat Azure blade header (title, optional
// "title | context" lockup, and optional subtitle) with an optional right-aligned
// actions slot and an optional breadcrumb above the title. No decorative resource
// icon by default — Agentweaver is not an Azure service, so blades don't carry a
// generic resource glyph. Pass `resourceIcon` only where a real, meaningful glyph
// applies.

export interface PageHeaderProps {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
  breadcrumb?: ReactNode;
  resourceIcon?: ReactNode;
  resourceLabel?: ReactNode;
}

export function PageHeader({
  title,
  subtitle,
  actions,
  breadcrumb,
  resourceIcon,
  resourceLabel,
}: PageHeaderProps) {
  return (
    <section className="azf-stack azf-page-header-shell" aria-label={`${title} header`}>
      {breadcrumb}
      <BladeHeader title={title} subtitle={subtitle} resourceIcon={resourceIcon} menuLabel={resourceLabel} />
      {actions && (
        <AzureToolbar actions={[]} topOfPage ariaLabel={`${title} commands`}>
          {actions}
        </AzureToolbar>
      )}
    </section>
  );
}
