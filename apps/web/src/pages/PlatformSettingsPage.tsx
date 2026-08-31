import { Body, PageContainer, PageHeader, PageSection } from '../components/ui';

export function PlatformSettingsPage() {
  return (
    <PageContainer width="readable">
      <PageHeader
        title="Platform settings"
        description="Deployment-wide configuration for Agentweaver."
      />
      <PageSection title="Platform configuration">
        <Body tone="muted">Platform-wide settings will be available here.</Body>
      </PageSection>
    </PageContainer>
  );
}
