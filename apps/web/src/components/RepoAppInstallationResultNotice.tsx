import { AuthCallbackNotice } from './AuthCallbackNotice';

const INSTALLATION_RESULTS = {
  success: {
    intent: 'success',
    title: 'Repo App installed',
    message: 'The GitHub Repo App is installed and bound to this project. Unattended agent runs can now read and write this repository.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    title: 'Work account required',
    message: 'Install the GitHub Repo App while signed in with your work account.',
  },
  project_owner_required: {
    intent: 'warning',
    title: 'Project owner required',
    message: 'Only a project owner can install the GitHub Repo App for this project.',
  },
  repository_not_connected: {
    intent: 'warning',
    title: 'Connect a repository first',
    message: 'Connect a repository to this project before installing the GitHub Repo App.',
  },
  installation_request_pending: {
    intent: 'warning',
    title: 'Organization approval pending',
    message: 'GitHub is waiting for an organization owner to approve this installation request. Once approved, reopen "Install GitHub Repo App" to finish binding it to this project.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    title: 'Installation expired',
    message: 'The GitHub Repo App installation could not be completed. Start a new installation from project settings.',
  },
  authorization_transaction_consumed: {
    intent: 'warning',
    title: 'Installation already completed',
    message: 'This GitHub Repo App installation was already completed. Refresh readiness before starting again.',
  },
  github_binding_unavailable: {
    intent: 'error',
    title: 'Repo App unavailable',
    message: 'The GitHub Repo App installation is currently unavailable. Try again later.',
  },
  installation_conflict: {
    intent: 'error',
    title: 'Installation already in use',
    message: 'This GitHub App installation is already bound to a different project. Uninstall it there first, or install a new instance of the app for this repository.',
  },
  permission_changed: {
    intent: 'warning',
    title: 'Permissions changed',
    message: 'The GitHub Repo App installation was updated with different permissions. Review the installation on GitHub if agent runs report unexpected access errors.',
  },
  repository_not_found_in_installation: {
    intent: 'error',
    title: 'Repository access missing',
    message: "This installation does not grant access to the project's connected repository. Reinstall the app and make sure to select that repository.",
  },
} as const;

export function RepoAppInstallationResultNotice({
  code,
  onDismiss,
}: {
  code: string | null;
  onDismiss: () => void;
}) {
  return (
    <AuthCallbackNotice
      code={code}
      results={INSTALLATION_RESULTS}
      fallback={{
        intent: 'error',
        title: 'Installation not completed',
        message: 'The GitHub Repo App installation could not be completed. Start a new installation from project settings.',
      }}
      onDismiss={onDismiss}
    />
  );
}
