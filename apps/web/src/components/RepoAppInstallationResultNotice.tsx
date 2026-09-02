import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';

const INSTALLATION_RESULTS = {
  success: {
    intent: 'success',
    message: 'The GitHub Repo App is installed and bound to this project. Unattended agent runs can now read and write this repository.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    message: 'Install the GitHub Repo App while signed in with your work account.',
  },
  project_owner_required: {
    intent: 'warning',
    message: 'Only a project owner can install the GitHub Repo App for this project.',
  },
  repository_not_connected: {
    intent: 'warning',
    message: 'Connect a repository to this project before installing the GitHub Repo App.',
  },
  installation_request_pending: {
    intent: 'warning',
    message: 'GitHub is waiting for an organization owner to approve this installation request. Once approved, reopen "Install GitHub Repo App" to finish binding it to this project.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    message: 'The GitHub Repo App installation could not be completed. Start a new installation from project settings.',
  },
  authorization_transaction_consumed: {
    intent: 'error',
    message: 'This GitHub Repo App installation attempt has already been used. Start a new installation from project settings.',
  },
  github_binding_unavailable: {
    intent: 'error',
    message: 'The GitHub Repo App installation is currently unavailable. Try again later.',
  },
  installation_conflict: {
    intent: 'error',
    message: 'This GitHub App installation is already bound to a different project. Uninstall it there first, or install a new instance of the app for this repository.',
  },
  permission_changed: {
    intent: 'warning',
    message: 'The GitHub Repo App installation was updated with different permissions. Review the installation on GitHub if agent runs report unexpected access errors.',
  },
  repository_not_found_in_installation: {
    intent: 'error',
    message: "This installation does not grant access to the project's connected repository. Reinstall the app and make sure to select that repository.",
  },
} as const;

type InstallationResultCode = keyof typeof INSTALLATION_RESULTS;

function isInstallationResultCode(value: string | null): value is InstallationResultCode {
  return value !== null && Object.hasOwn(INSTALLATION_RESULTS, value);
}

export function RepoAppInstallationResultNotice({
  code,
  onDismiss,
}: {
  code: string | null;
  onDismiss: () => void;
}) {
  const result = isInstallationResultCode(code)
    ? INSTALLATION_RESULTS[code]
    : code
      ? {
        intent: 'error' as const,
        message: 'The GitHub Repo App installation could not be completed. Start a new installation from project settings.',
      }
      : null;

  if (!result) return null;

  return (
    <MessageBar intent={result.intent}>
      <MessageBarBody>{result.message}</MessageBarBody>
      <MessageBarActions>
        <Button appearance="transparent" size="small" onClick={onDismiss}>Dismiss</Button>
      </MessageBarActions>
    </MessageBar>
  );
}
