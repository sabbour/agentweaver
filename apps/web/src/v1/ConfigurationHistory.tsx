import { Button, Field, Input, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { ConfigurationFields } from './ConfigurationFields';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { VersionedProjectConfiguration } from './contracts';

/**
 * Loads and shows one earlier configuration revision, read-only. It does not
 * replace the current configuration that the settings page edits.
 */
export function ConfigurationHistory({ projectId, currentRevision }: { projectId: string; currentRevision: number }) {
  const { apiCall } = useAuth();
  const [revision, setRevision] = useState(String(currentRevision));
  const [snapshot, setSnapshot] = useState<VersionedProjectConfiguration | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Counter for the latest request. A response is used only if its number is
  // still the latest, so a slow old response cannot overwrite a newer one.
  const generation = useRef(0);
  // On unmount, or when the project or current revision changes, bump the
  // counter so any request still in flight is ignored when it returns.
  useEffect(() => () => { generation.current += 1; }, [projectId, currentRevision]);

  const load = async () => {
    // Accept only whole numbers from 1 to the current revision.
    const selected = Number(revision);
    if (!Number.isSafeInteger(selected) || selected < 1 || selected > currentRevision) {
      setError(`Enter a revision from 1 to ${currentRevision}.`);
      return;
    }
    const request = ++generation.current;
    setBusy(true);
    // Hide the previous snapshot right away so it is never shown under the new revision number.
    setSnapshot(null);
    setError(null);
    try {
      const result = await apiCall((token, tenant) => gatewayClient.getProjectConfiguration(token, projectId, tenant, selected), null);
      if (request !== generation.current) return;
      // Show the snapshot only if the service returned exactly the project and revision we asked for.
      if (result.projectId !== projectId || result.revision !== selected) throw new Error('The service returned a different project or revision.');
      setSnapshot(result);
    } catch (reason) {
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'This revision could not load.');
    } finally {
      if (request === generation.current) setBusy(false);
    }
  };
  return (
    <PageSection title="Configuration history" description={`The current revision is ${currentRevision}. Earlier revisions are read-only.`}>
      <div className="v1-inline-form">
        <Field label="Configuration revision">
          <Input type="number" min={1} max={currentRevision} value={revision} onChange={(_, data) => setRevision(data.value)} />
        </Field>
        <Button disabled={busy} onClick={() => void load()}>{busy ? 'Loading revision…' : 'View revision'}</Button>
      </div>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {/* Old revisions are shown read-only, and onChange does nothing, so they can never be saved from here. */}
      {snapshot && <PageSection title={`Configuration revision ${snapshot.revision}`}>
        <ConfigurationFields configuration={snapshot.configuration} onChange={() => {}} readOnly />
        <dl className="v1-detail-list">
          <div><dt>Agents</dt><dd>{snapshot.configuration.agentCharters.map((agent) => agent.name).join(', ') || 'None'}</dd></div>
          <div><dt>Skills</dt><dd>{snapshot.configuration.skills.map((skill) => skill.skillId).join(', ') || 'None'}</dd></div>
        </dl>
      </PageSection>}
    </PageSection>
  );
}
