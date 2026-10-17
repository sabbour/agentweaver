import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';

/** Display content and severity for one authentication callback result code. */
export type AuthCallbackResult = {
  intent: 'success' | 'warning' | 'error' | 'info';
  title: string;
  message: string;
};

/**
 * Renders the notice for an authentication callback code, or nothing when no code is present.
 * `results` maps known codes to notices; unknown codes use `fallback`. An optional dismiss
 * callback adds a dismiss action; all result data is supplied by the caller.
 */
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
