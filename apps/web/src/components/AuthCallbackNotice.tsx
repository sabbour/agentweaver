import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';

export type AuthCallbackResult = {
  intent: 'success' | 'warning' | 'error' | 'info';
  title: string;
  message: string;
};

export function AuthCallbackNotice({
  code,
  results,
  fallback,
  onDismiss,
}: {
  code: string | null;
  results: Readonly<Record<string, AuthCallbackResult>>;
  fallback: AuthCallbackResult;
  onDismiss?: () => void;
}) {
  if (!code) return null;
  const result = Object.hasOwn(results, code) ? results[code] : fallback;

  return (
    <MessageBar intent={result.intent} role={result.intent === 'error' ? 'alert' : 'status'}>
      <MessageBarBody>
        <MessageBarTitle>{result.title}</MessageBarTitle>
        {result.message}
      </MessageBarBody>
      {onDismiss && (
        <MessageBarActions>
          <Button appearance="transparent" size="small" onClick={onDismiss}>Dismiss</Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}
