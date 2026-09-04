import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage } from '../api/errors';
import type { ByokProviderRequest, ByokProviderType, UserAiAccessStatus } from '../api/types';
import { AppCard, Body, Label, TitleText } from './ui';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  cards: { display: 'grid', gap: tokens.spacingVerticalM },
  card: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  row: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap', alignItems: 'center' },
  form: { display: 'grid', gap: tokens.spacingVerticalM },
});

const blankProvider: ByokProviderRequest = {
  name: '',
  type: 'openai',
  base_url: '',
  model: '',
  api_key: '',
  wire_api: null,
};

export function UserAiAccessSettings() {
  const styles = useStyles();
  const [searchParams, setSearchParams] = useSearchParams();
  const [status, setStatus] = useState<UserAiAccessStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [editingByok, setEditingByok] = useState(false);
  const [form, setForm] = useState<ByokProviderRequest>(blankProvider);
  const [authorizationResult] = useState(() => searchParams.get('user_copilot_auth'));

  const load = useCallback(async () => {
    try {
      setStatus(await apiClient.getUserAiAccess());
      setError(null);
    } catch (err) {
      setError(formatApiErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    const timeoutId = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timeoutId);
  }, [load]);

  useEffect(() => {
    if (!authorizationResult) return;
    const next = new URLSearchParams(searchParams);
    next.delete('user_copilot_auth');
    setSearchParams(next, { replace: true });
  }, [authorizationResult, searchParams, setSearchParams]);

  const connectCopilot = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await apiClient.beginUserCopilotAuthorization();
      window.location.assign(result.authorization_url);
    } catch (err) {
      setError(formatApiErrorMessage(err));
      setBusy(false);
    }
  };

  const chooseCopilot = async () => {
    setBusy(true);
    try {
      await apiClient.setUserAiPreference('github-copilot');
      await load();
    } catch (err) {
      setError(formatApiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const chooseByok = async () => {
    setBusy(true);
    try {
      await apiClient.setUserAiPreference('byok');
      await load();
    } catch (err) {
      setError(formatApiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const saveByok = async () => {
    setBusy(true);
    try {
      await apiClient.setUserByokProvider(form);
      setEditingByok(false);
      await load();
    } catch (err) {
      setError(formatApiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const editByok = () => {
    const provider = status?.personal_byok;
    setForm(provider ? {
      name: provider.name,
      type: provider.type,
      base_url: provider.base_url,
      model: provider.model,
      api_key: '',
      wire_api: provider.wire_api,
      headers: provider.headers,
      azure_api_version: provider.azure_api_version,
    } : blankProvider);
    setEditingByok(true);
  };

  const removeByok = async () => {
    setBusy(true);
    try {
      await apiClient.removeUserByokProvider();
      setEditingByok(false);
      await load();
    } catch (err) {
      setError(formatApiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const disconnectCopilot = async () => {
    setBusy(true);
    try {
      await apiClient.disconnectUserCopilot();
      await load();
    } catch (err) {
      setError(formatApiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  if (!status && !error) return <Spinner label="Loading AI access" />;
  const authorizationError = authorizationResult && authorizationResult !== 'success'
    ? 'The GitHub Copilot connection could not be completed. Start a new connection from Account settings.'
    : null;

  return (
    <div className={styles.root}>
      <Body tone="muted">
        These settings control your personal session chat. Project background work uses the
        Copilot connection selected in that project.
      </Body>
      {(error || authorizationError) && (
        <MessageBar intent="error"><MessageBarBody>{error ?? authorizationError}</MessageBarBody></MessageBar>
      )}
      {authorizationResult === 'success' && (
        <MessageBar intent="success">
          <MessageBarBody>GitHub Copilot is connected for your session chat.</MessageBarBody>
        </MessageBar>
      )}
      {status?.platform_byok && (
        <MessageBar intent="success">
          <MessageBarBody>
            Your session chat uses {status.platform_byok.name} ({status.platform_byok.type}),
            supplied by the platform. You do not need to add a personal key.
          </MessageBarBody>
        </MessageBar>
      )}
      {status && !status.platform_byok && status.effective_source === 'none' && (
        <MessageBar intent="warning">
          <MessageBarBody>Configure a model provider to continue using session chat.</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.cards}>
        <AppCard className={styles.card}>
          <TitleText>GitHub Copilot</TitleText>
          <Body tone="muted">Use your own GitHub Copilot subscription for personal session chat.</Body>
          {status?.copilot.connected && (
            <Label>Connected as @{status.copilot.github_login ?? 'unknown'}</Label>
          )}
          {status?.copilot.reconnect_required && (
            <MessageBar intent="warning">
              <MessageBarBody>Your saved connection needs authorization again.</MessageBarBody>
            </MessageBar>
          )}
          <div className={styles.row}>
            <Button appearance={status?.copilot.connected ? 'secondary' : 'primary'} disabled={busy} onClick={() => void connectCopilot()}>
              {status?.copilot.connected ? 'Switch GitHub Copilot account' : 'Authorize GitHub Copilot'}
            </Button>
            {status?.copilot.connected && status.preference !== 'github_copilot' && (
              <Button disabled={busy} onClick={() => void chooseCopilot()}>Use for session chat</Button>
            )}
            {status?.copilot.connected && (
              <Button appearance="secondary" disabled={busy} onClick={() => void disconnectCopilot()}>Disconnect</Button>
            )}
          </div>
        </AppCard>

        <AppCard className={styles.card}>
          <TitleText>Personal provider</TitleText>
          <Body tone="muted">Add one OpenAI, Azure OpenAI, or Anthropic provider for your session chat.</Body>
          {status?.personal_byok && !editingByok && (
            <Label>{status.personal_byok.name} · {status.personal_byok.model}</Label>
          )}
          {!editingByok ? (
            <div className={styles.row}>
              <Button appearance="secondary" disabled={busy} onClick={editByok}>
                {status?.personal_byok ? 'Edit provider' : 'Add provider'}
              </Button>
              {status?.personal_byok && status.preference !== 'byok' && (
                <Button disabled={busy} onClick={() => void chooseByok()}>
                  Use for session chat
                </Button>
              )}
              {status?.personal_byok && (
                <Button appearance="secondary" disabled={busy} onClick={() => void removeByok()}>Remove</Button>
              )}
            </div>
          ) : (
            <div className={styles.form}>
              <Field label="Provider type">
                <Select
                  value={form.type}
                  onChange={(_, data) => setForm((current) => ({ ...current, type: data.value as ByokProviderType }))}
                >
                  <option value="openai">OpenAI-compatible</option>
                  <option value="azure">Azure OpenAI</option>
                  <option value="anthropic">Anthropic</option>
                </Select>
              </Field>
              <Field label="Display name" required>
                <Input value={form.name} onChange={(_, data) => setForm((current) => ({ ...current, name: data.value }))} />
              </Field>
              <Field label="Base URL" required>
                <Input value={form.base_url} onChange={(_, data) => setForm((current) => ({ ...current, base_url: data.value }))} />
              </Field>
              <Field label="Model" required>
                <Input value={form.model} onChange={(_, data) => setForm((current) => ({ ...current, model: data.value }))} />
              </Field>
              <Field label="API key" hint={status?.personal_byok ? 'Leave blank to keep the saved key.' : undefined}>
                <Input type="password" value={form.api_key ?? ''} onChange={(_, data) => setForm((current) => ({ ...current, api_key: data.value }))} />
              </Field>
              <div className={styles.row}>
                <Button appearance="primary" disabled={busy} onClick={() => void saveByok()}>Save and use</Button>
                <Button appearance="secondary" disabled={busy} onClick={() => setEditingByok(false)}>Cancel</Button>
              </div>
            </div>
          )}
        </AppCard>
      </div>
    </div>
  );
}
