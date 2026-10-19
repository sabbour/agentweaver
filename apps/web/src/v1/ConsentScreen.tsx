import { Button, MessageBar, MessageBarBody, Tooltip } from '@fluentui/react-components';
import { Info16Regular } from '@fluentui/react-icons';
import { useAuth } from './AuthContext';
import type { BrokerConsentPrompt } from './contracts';
import { describeConsentScope } from './consentScopes';

interface ConsentScreenViewProps {
  consent: BrokerConsentPrompt;
  busy: boolean;
  error: string | null;
  decideConsent: (approve: boolean) => Promise<void>;
}

/** Renders requested permissions and the signed-in user's choice. */
export function ConsentScreenView({
  consent,
  busy,
  error,
  decideConsent,
}: ConsentScreenViewProps) {
  return (
    <main className="v1-auth-page">
      <section className="v1-auth-card">
        <div className="v1-brand"><img src="/agentweaver.png" alt="" />Agentweaver</div>
        <h1>Review access</h1>
        <p>Agentweaver's sign-in service asks to use these permissions for this app.</p>
        <dl className="v1-detail-list">
          <div><dt>App</dt><dd>{consent.client_id}</dd></div>
          <div>
            <dt>Requested permissions</dt>
            <dd>
              <ul className="v1-consent-scopes">
                {consent.requested_scopes.map((scope, index) => {
                  const details = describeConsentScope(scope);
                  return (
                    <li className="v1-consent-scope" key={`${scope}-${index}`}>
                      <div className="v1-consent-scope__details">
                        <strong><code>{scope}</code></strong>
                        <p>{details.summary}</p>
                      </div>
                      <div className="v1-consent-scope__actions">
                        <Tooltip content={details.tooltip} relationship="description" withArrow>
                          <Button
                            appearance="subtle"
                            size="small"
                            icon={<Info16Regular />}
                            aria-label={`About ${scope}`}
                            type="button"
                          />
                        </Tooltip>
                        <a
                          href={details.documentationUrl}
                          target="_blank"
                          rel="noreferrer"
                          aria-label={`Documentation for ${scope}`}
                        >
                          Documentation
                        </a>
                      </div>
                    </li>
                  );
                })}
              </ul>
            </dd>
          </div>
        </dl>
        <p className="v1-muted">For each project run, Agentweaver checks your project access, run assignment, and saved settings.</p>
        {error && (
          <MessageBar intent="error" role="alert">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        <div className="v1-actions">
          <Button appearance="primary" disabled={busy} onClick={() => void decideConsent(true)}>
            {busy ? 'Processing…' : 'Approve'}
          </Button>
          <Button appearance="secondary" disabled={busy} onClick={() => void decideConsent(false)}>
            Decline
          </Button>
        </div>
      </section>
    </main>
  );
}

/** Connects sign-in consent state to the portal view. */
export function ConsentScreen() {
  const { consent, busy, error, decideConsent } = useAuth();
  if (!consent) return null;
  return (
    <ConsentScreenView
      consent={consent}
      busy={busy}
      error={error}
      decideConsent={decideConsent}
    />
  );
}
