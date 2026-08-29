import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';

const AUTHORIZATION_RESULTS = {
  success: {
    intent: 'success',
    message: 'The Copilot App is connected to this project. Refresh automation readiness to confirm the remaining prerequisites.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    message: 'Connect the Copilot App while signed in with your work account.',
  },
  project_owner_required: {
    intent: 'warning',
    message: 'Only a project owner can connect the Copilot App.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    message: 'The Copilot App connection could not be completed. Start a new connection from the project settings.',
  },
  authorization_transaction_consumed: {
    intent: 'error',
    message: 'This Copilot App connection has already been used. Start a new connection from the project settings.',
  },
  github_binding_unavailable: {
    intent: 'error',
    message: 'The Copilot App connection is currently unavailable. Try again later.',
  },
} as const;

type AuthorizationResultCode = keyof typeof AUTHORIZATION_RESULTS;

function isAuthorizationResultCode(value: string | null): value is AuthorizationResultCode {
  return value !== null && Object.hasOwn(AUTHORIZATION_RESULTS, value);
}

export function CopilotAuthorizationResultNotice({
  code,
  onDismiss,
}: {
  code: string | null;
  onDismiss: () => void;
}) {
  const result = isAuthorizationResultCode(code)
    ? AUTHORIZATION_RESULTS[code]
    : code
      ? {
        intent: 'error' as const,
        message: 'The Copilot App connection could not be completed. Start a new connection from the project settings.',
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
