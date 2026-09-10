import { AuthCallbackNotice } from './AuthCallbackNotice';

const REPO_AUTH_RESULTS = {
  success: {
    intent: 'success',
    title: 'Repo App connected',
    message: 'GitHub repository access is connected. You can return to the task that requested it.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    title: 'Work account required',
    message: 'Authorize repository access while signed in with your work account.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    title: 'Connection expired',
    message: 'Repository authorization could not be completed. Start a new authorization.',
  },
  authorization_transaction_consumed: {
    intent: 'warning',
    title: 'Connection already completed',
    message: 'This repository authorization has already been used. Start a new authorization.',
  },
  github_binding_unavailable: {
    intent: 'error',
    title: 'Repo App unavailable',
    message: 'Repository authorization is currently unavailable. Try again later.',
  },
  rate_limited: {
    intent: 'warning',
    title: 'Too many attempts',
    message: 'GitHub is receiving too many authorization requests. Wait a moment and try again.',
  },
} as const;

const USER_COPILOT_RESULTS = {
  success: {
    intent: 'success',
    title: 'GitHub Copilot connected',
    message: 'GitHub Copilot is connected for your session chat.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    title: 'Work account required',
    message: 'Connect GitHub Copilot while signed in with your work account.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    title: 'Connection expired',
    message: 'The GitHub Copilot connection could not be completed. Start a new connection from Account settings.',
  },
  authorization_transaction_consumed: {
    intent: 'warning',
    title: 'Connection already completed',
    message: 'This GitHub Copilot connection was already completed. Refresh AI access before starting again.',
  },
  github_binding_unavailable: {
    intent: 'error',
    title: 'GitHub Copilot unavailable',
    message: 'The GitHub Copilot connection is currently unavailable. Try again later.',
  },
} as const;

const PLATFORM_COPILOT_RESULTS = {
  success: {
    intent: 'success',
    title: 'Platform Copilot connected',
    message: 'The platform-default GitHub Copilot account is connected.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    title: 'Work account required',
    message: 'Authorize GitHub Copilot while signed in with your work account.',
  },
  platform_admin_required: {
    intent: 'warning',
    title: 'Platform Admin required',
    message: 'Only a Platform Admin can connect the platform-default GitHub Copilot account.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    title: 'Connection expired',
    message: 'The GitHub Copilot connection failed. Start a new connection from Platform settings.',
  },
  authorization_transaction_consumed: {
    intent: 'warning',
    title: 'Connection already completed',
    message: 'This GitHub Copilot connection was already completed. Refresh the connection status before starting again.',
  },
  github_binding_unavailable: {
    intent: 'error',
    title: 'GitHub Copilot unavailable',
    message: 'The GitHub Copilot connection is currently unavailable. Try again later.',
  },
} as const;

const repoFallback = {
  intent: 'error',
  title: 'Connection not completed',
  message: 'Repository authorization could not be completed. Start a new authorization.',
} as const;

const copilotFallback = {
  intent: 'error',
  title: 'Connection not completed',
  message: 'The GitHub Copilot connection could not be completed. Start a new connection from settings.',
} as const;

export function RepoAppAuthorizationResultNotice(props: { code: string | null; onDismiss?: () => void }) {
  return <AuthCallbackNotice {...props} results={REPO_AUTH_RESULTS} fallback={repoFallback} />;
}

export function UserCopilotAuthorizationResultNotice(props: { code: string | null; onDismiss?: () => void }) {
  return <AuthCallbackNotice {...props} results={USER_COPILOT_RESULTS} fallback={copilotFallback} />;
}

export function PlatformCopilotAuthorizationResultNotice(props: { code: string | null; onDismiss?: () => void }) {
  return <AuthCallbackNotice {...props} results={PLATFORM_COPILOT_RESULTS} fallback={copilotFallback} />;
}
