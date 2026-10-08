import { useEffect, useState } from 'react';
import { apiClient } from '../api/apiClient';
import type { OutputRevision, OutputRevisionComparison, OutputRevisionFile } from '../api/types';

export function OutputRevisionHistory({
  runId,
  currentReviewRevisionId,
}: {
  runId: string;
  currentReviewRevisionId?: string;
}) {
  const [history, setHistory] = useState<OutputRevision[]>([]);
  const [selected, setSelected] = useState<OutputRevision | null>(null);
  const [comparison, setComparison] = useState<OutputRevisionComparison | null>(null);
  const [file, setFile] = useState<OutputRevisionFile | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiClient.getOutputRevisionHistory(runId).then(
      (revisions) => { if (!cancelled) setHistory(revisions); },
      (reason: unknown) => { if (!cancelled) setError(String(reason)); },
    );
    return () => { cancelled = true; };
  }, [runId]);

  async function inspect(revisionId: string) {
    setError(null);
    setComparison(null);
    setFile(null);
    try {
      const revision = await apiClient.getOutputRevision(runId, revisionId);
      setSelected(revision);
      if (revision.predecessor_revision_id)
        setComparison(await apiClient.compareOutputRevisions(
          runId, revision.predecessor_revision_id, revision.revision_id));
    } catch (reason) {
      setSelected(null);
      setError(String(reason));
    }
  }

  async function inspectFile(revisionId: string, path: string) {
    setError(null);
    try {
      setFile(await apiClient.getOutputRevisionFile(runId, revisionId, path));
    } catch (reason) {
      setFile(null);
      setError(String(reason));
    }
  }

  return (
    <section aria-label="Output version history">
      <h3>Output versions</h3>
      <p>Saved versions of this run's output. Open one to inspect its files and exact changes.</p>
      {error && <p role="alert">Exact output version unavailable: {error}</p>}
      {history.length === 0 && !error && <p>No output versions yet.</p>}
      <ul>
        {history.map((revision, index) => {
          const versionNumber = history.length - index;
          return (
            <li key={revision.revision_id}>
              <button type="button" onClick={() => void inspect(revision.revision_id)}>
                Output version {versionNumber}
                {revision.revision_id === currentReviewRevisionId ? ' · Under review' : ''}
              </button>
              <details>
                <summary>Version details</summary>
                <p>Revision ID: <code>{revision.revision_id}</code></p>
                {revision.accepted_no_change && <p>Accepted with no changes</p>}
              </details>
            </li>
          );
        })}
      </ul>
      {selected && (
        <div>
          <h4>
            Output version {history.length - history.findIndex(
              (revision) => revision.revision_id === selected.revision_id,
            )}
          </h4>
          <p>Tree: {selected.tree_hash} · workflow: {selected.workflow_digest ?? 'unavailable'}</p>
          <p>Diff SHA-256: {selected.diff_sha256}</p>
          {selected.diff !== undefined && <pre>{selected.diff}</pre>}
          <h4>Exact retained files</h4>
          <ul>
            {selected.files?.map((entry) => (
              <li key={entry.path}>
                <button type="button" onClick={() => void inspectFile(selected.revision_id, entry.path)}>
                  {entry.path} ({entry.size} bytes)
                </button>
              </li>
            ))}
          </ul>
          {file && <div><p>{file.path} · SHA-256 {file.sha256}</p><pre>{file.content_base64}</pre></div>}
          {comparison && (
            <div>
              <h4>Changed since {comparison.before_revision_id}</h4>
              <ul>{comparison.changes.map((change) => (
                <li key={change.path}>{change.path}: {change.before_sha256 ?? 'absent'} → {change.after_sha256 ?? 'absent'}</li>
              ))}</ul>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
