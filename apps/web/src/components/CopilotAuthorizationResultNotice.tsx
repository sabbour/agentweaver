import { AuthCallbackNotice } from './AuthCallbackNotice';

const AUTHORIZATION_RESULTS = {
  success: {
    intent: 'success',
    title: 'Copilot App connected',
    message: 'The Copilot App is connected to this project. Refresh automation readiness to confirm the remaining prerequisites.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    title: 'Work account required',
    message: 'Connect the Copilot App while signed in with your work account.',
  },
  project_owner_required: {
    intent: 'warning',
    title: 'Project owner required',
    message: 'Only a project owner can connect the Copilot App.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    title: 'Connection expired',
    message: 'The Copilot App connection could not be completed. Start a new connection from the project settings.',
  },
  authorization_transaction_consumed: {
    intent: 'warning',
    title: 'Connection already completed',
    message: 'This Copilot App connection was already completed. Refresh the connection status before starting again.',
  },
  github_binding_unavailable: {
    intent: 'error',
    title: 'Copilot App unavailable',
    message: 'The Copilot App connection is currently unavailable. Try again later.',
  },
  project_model_provider_reconnect_required: {
    intent: 'warning',
    title: 'Reconnect required',
    message: 'The saved Copilot connection needs authorization again. Start a new connection from project settings.',
  },
} as const;

export function CopilotAuthorizationResultNotice({
  code,
  onDismiss,
}: {
  code: string | null;
  onDismiss: () => void;
}) {
  return (
    <AuthCallbackNotice
      code={code}
      results={AUTHORIZATION_RESULTS}
      fallback={{
        intent: 'error',
        title: 'Connection not completed',
        message: 'The Copilot App connection could not be completed. Start a new connection from project settings.',
      }}
      onDismiss={onDismiss}
    />
  );
}
