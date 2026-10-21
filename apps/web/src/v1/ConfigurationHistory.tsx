import { Button, Field, Input, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { ConfigurationFields } from './ConfigurationFields';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { VersionedProjectConfiguration } from './contracts';

/** Reads one exact historical revision without replacing the current editable document. */
export function ConfigurationHistory({ projectId, currentRevision }: { projectId: string; currentRevision: number }) {
  const { apiCall } = useAuth();
  const [revision, setRevision] = useState(String(currentRevision));
  const [snapshot, setSnapshot] = useState<VersionedProjectConfiguration | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Request counter. A response is applied only if no newer request started and
  // the panel did not unmount or switch project or revision in the meantime.
  const generation = useRef(0);
  // Cleanup runs on unmount and when the project or current revision changes.
  // Bumping the counter there makes any in-flight response stale.
  useEffect(() => () => { generation.current += 1; }, [projectId, currentRevision]);

  /** Loads the revision typed by the user and shows it read-only. */
  const load = async () => {
    // Check the range locally first. Revisions start at 1 and cannot be newer
    // than the current one, so other values would only produce a server error.
    const selected = Number(revision);
    if (!Number.isSafeInteger(selected) || selected < 1 || selected > currentRevision) {
      setError(`Enter a revision from 1 to ${currentRevision}.`);
      return;
    }
    const request = ++generation.current;
    setBusy(true);
    // Hide the previous snapshot so it is never shown under a new revision number.
    setSnapshot(null);
    setError(null);
    try {
      const result = await apiCall((token, tenant) => gatewayClient.getProjectConfiguration(token, projectId, tenant, selected), null);
      if (request !== generation.current) return;
      // Show only the exact project and revision that was asked for.
      if (result.projectId !== projectId || result.revision !== selected) throw new Error('The service returned a different project or revision.');
      setSnapshot(result);
    } catch (reason) {
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'This revision could not load.');
    } finally {
      // Only the latest request may clear the busy flag.
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
