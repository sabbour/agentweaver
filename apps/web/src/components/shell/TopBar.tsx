import { Badge, Button, PortalTopNav, Tooltip } from '../../copilot-fluent-system';
import { useAppVersion } from '../../hooks/useAppVersion';
import { GitHubSignIn } from '../GitHubSignIn';
import { StartOrchestrationFab } from '../StartOrchestrationFab';
import { useConsolePanel } from './ConsolePanelContext';
import { ProjectSwitcher } from './ProjectSwitcher';
import { StatusDot } from './StatusDot';
import { Chat20Regular } from '../../copilot-fluent-system';
// Spec 011 — top bar (FR-011..FR-015). Carries the switch-only project switcher,
// the API-reachability status dot, and the existing GitHub sign-in. The brand
// mark lives in the left nav rail header (top-left). Docs / Inbox / Consult are
// intentionally absent (FR-015).

export interface TopBarProps {
  projectId: string | undefined;
  pathname: string;
  // True when projectId is a persisted fallback (route carries no :projectId).
  isFallbackProject?: boolean;
  // Called when the persisted fallback project no longer exists in the project list.
  onFallbackProjectMissing?: () => void;
}

export function TopBar({
  projectId,
  pathname,
  isFallbackProject,
  onFallbackProjectMissing,
}: TopBarProps) {
  const version = useAppVersion();
  const { open: consoleOpen, openConsole } = useConsolePanel();
  const pageHasStartTaskAction = /^\/projects\/[^/]+(?:\/board)?\/?$/.test(pathname);
  return (
    <PortalTopNav
      variant="brand"
      ariaLabel="Application toolbar"
      brand={{ product: 'Agentweaver', area: 'Copilot work orchestration' }}
      startContent={
        <>
          <Badge appearance="outline" color="warning" title="Agentweaver is alpha software under active development.">
            Alpha{version ? ` v${version}` : ''}
          </Badge>
          <ProjectSwitcher
            projectId={projectId}
            pathname={pathname}
            isFallbackProject={isFallbackProject}
            onFallbackProjectMissing={onFallbackProjectMissing}
          />
        </>
      }
      endContent={
        <>
          {!pageHasStartTaskAction && <StartOrchestrationFab currentProjectId={projectId} />}
          <Tooltip content="Open Agentweaver operator dock" relationship="label">
            <Button
              appearance="subtle"
              icon={<Chat20Regular />}
              aria-label="Open Agentweaver operator dock"
              aria-expanded={consoleOpen}
              aria-controls="app-console-panel"
              data-testid="open-console-panel"
              onClick={openConsole}
            >
              Operator dock
            </Button>
          </Tooltip>
          <StatusDot />
          <GitHubSignIn />
        </>
      }
    />
  );
}
