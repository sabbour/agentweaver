import { Badge, Button, Checkbox, Field, Input, Text } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { usePlatformPoliciesController } from './PlatformPoliciesController';
import { PlatformPolicyTextarea } from './PlatformPolicyTextarea';
import { PageHeading } from './pageParts';
import {
  comparePlatformPolicyDraft,
  escapePlatformPolicyInvisibleCharacters,
} from './platformPolicyContracts';
import { findPlatformPolicyDiagnosticDraftFile } from './platformPolicyDraftNavigation';
import {
  applyProjectSkillTrustScoreUpdate,
  findProjectSkillTrustTarget,
  MAX_PROJECT_SKILL_TRUST_SCORE,
  validateNextSkillTrustScore,
} from './projectSkillTrustPolicy';
import type {
  PlatformPolicyComparison,
  PlatformPolicyComparisonItem,
  PlatformPolicyRevisionReadResult,
  PlatformPolicyTextBuffer,
} from './platformPolicyContracts';
import type { PlatformPoliciesControllerValue } from './PlatformPoliciesController';
import type { ProjectSkillTrustTarget } from './projectSkillTrustPolicy';

function createdAtLabel(value: string): string {
  return new Date(value).toLocaleString();
}

function PlatformPolicyComparisonControls({
  detail,
  draftManifest,
  draftFiles,
}: Pick<PlatformPoliciesControllerValue, 'draftManifest' | 'draftFiles'> & {
  detail: PlatformPolicyRevisionReadResult;
}) {
  const [comparison, setComparison] = useState<{
    revision: number;
    contentSha256: string;
    result: PlatformPolicyComparison;
  } | null>(null);
  const [comparisonError, setComparisonError] = useState<string | null>(null);
  const [selectedComparisonItem, setSelectedComparisonItem] = useState('');
  const [showInvisible, setShowInvisible] = useState(false);

  const compareDraft = () => {
    try {
      const result = comparePlatformPolicyDraft(
        detail.artifact,
        draftManifest,
        draftFiles.map(({ relativePath, content }) => ({ relativePath, content })),
      );
      const firstItem = result.manifest
        ? 'item:manifest'
        : result.files[0]
          ? `file:${result.files[0].relativePath}`
          : '';
      setComparison({
        revision: detail.revision,
        contentSha256: detail.contentSha256,
        result,
      });
      setComparisonError(null);
      setSelectedComparisonItem(firstItem);
      setShowInvisible(false);
    } catch (reason) {
      setComparison(null);
      setSelectedComparisonItem('');
      setComparisonError(reason instanceof Error
        ? reason.message
        : 'The draft could not be compared.');
    }
  };

  const comparisonItems: Array<{
    key: string;
    label: string;
    savedContent: string | null;
    draftContent: string | null;
  }> = comparison
    ? [
        ...(comparison.result.manifest ? [{
          key: 'item:manifest',
          label: 'Manifest — Changed',
          savedContent: comparison.result.manifest.savedContent,
          draftContent: comparison.result.manifest.draftContent,
        }] : []),
        ...comparison.result.files.map((item: PlatformPolicyComparisonItem) => ({
          key: `file:${item.relativePath}`,
          label: `${item.relativePath} — ${item.change[0].toUpperCase()}${item.change.slice(1)}`,
          savedContent: item.savedContent,
          draftContent: item.draftContent,
        })),
      ]
    : [];
  const selectedComparison = comparisonItems.find((item) =>
    item.key === selectedComparisonItem);
  const displayComparisonText = (value: string) =>
    showInvisible ? escapePlatformPolicyInvisibleCharacters(value) : value;

  return (
    <div className="v1-policy-compare-control">
      <Button appearance="secondary" data-platform-policy-compare onClick={compareDraft}>
        Compare draft
      </Button>
      {comparisonError && <p role="alert">{comparisonError}</p>}
      {comparison && (
        <section
          className="v1-policy-comparison"
          aria-label={`Draft comparison with revision ${comparison.revision}`}
          data-platform-policy-comparison
        >
          <h3>Compare draft with revision {comparison.revision}</h3>
          <p><strong>Content hash:</strong> <code>{comparison.contentSha256}</code></p>
          {comparisonItems.length === 0 ? (
            <p>No changes from revision {comparison.revision}.</p>
          ) : (
            <>
              <Field label="Changed item">
                <select
                  className="v1-select"
                  aria-label="Changed Policy item"
                  value={selectedComparisonItem}
                  onChange={(event) => setSelectedComparisonItem(event.currentTarget.value)}
                >
                  {comparisonItems.map((item) => (
                    <option key={item.key} value={item.key}>{item.label}</option>
                  ))}
                </select>
              </Field>
              <Checkbox
                label="Show invisible characters"
                checked={showInvisible}
                onChange={(_, data) => setShowInvisible(Boolean(data.checked))}
              />
              {showInvisible && (
                <p className="v1-muted">
                  Key: {'\\x20'} space, {'\\t'} tab, {'\\r'} carriage return,
                  {' '}
                  {'\\n'} newline, {'\\uXXXX'} other Unicode.
                </p>
              )}
              {selectedComparison && (
                <div className="v1-policy-comparison-sides">
                  <section aria-label="Saved comparison source">
                    <h4>Saved</h4>
                    {selectedComparison.savedContent === null ? (
                      <p>Not in this revision</p>
                    ) : (
                      <pre className="v1-policy-source">
                        {displayComparisonText(selectedComparison.savedContent)}
                      </pre>
                    )}
                  </section>
                  <section aria-label="Draft comparison source">
                    <h4>Draft</h4>
                    {selectedComparison.draftContent === null ? (
                      <p>Not in the draft</p>
                    ) : (
                      <pre className="v1-policy-source">
                        {displayComparisonText(selectedComparison.draftContent)}
                      </pre>
                    )}
                  </section>
                </div>
              )}
            </>
          )}
        </section>
      )}
    </div>
  );
}

function ProjectSkillTrustScoreForm({
  target,
  fileBuffer,
  onUpdateDraft,
}: {
  target: ProjectSkillTrustTarget;
  fileBuffer: PlatformPolicyTextBuffer | undefined;
  onUpdateDraft: (buffer: PlatformPolicyTextBuffer) => void;
}) {
  const [scoreInput, setScoreInput] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const atMaximum = target.minimumScore >= MAX_PROJECT_SKILL_TRUST_SCORE;

  const handleUpdate = () => {
    if (!fileBuffer) return;
    const result = validateNextSkillTrustScore(scoreInput, target.minimumScore);
    if (!result.ok) {
      setFormError(result.message);
      return;
    }
    onUpdateDraft(applyProjectSkillTrustScoreUpdate(fileBuffer, target, result.token));
  };

  return (
    <section aria-label="Project Skill trust score" data-project-skill-trust-form>
      <h3>Project Skill trust score</h3>
      <p>
        Raise the minimum trust score for third-party Skills in{' '}
        <code>policy/skills/data.json</code>. The Platform minimum and built-in
        Skill exemption stay unchanged. Updating the draft does not save or
        apply it.
      </p>
      <p>
        <strong>Current minimum score:</strong> {target.minimumScore}
        {' · '}
        <strong>Require completed evidence:</strong>{' '}
        {target.requireCompletedEvidence ? 'true' : 'false'}
      </p>
      {atMaximum ? (
        <p role="status">
          The minimum trust score is already {MAX_PROJECT_SKILL_TRUST_SCORE}.
        </p>
      ) : (
        <>
          <Field label="New minimum trust score">
            <Input
              type="text"
              inputMode="numeric"
              value={scoreInput}
              onChange={(_, data) => {
                setScoreInput(data.value);
                setFormError(null);
              }}
              autoComplete="off"
            />
          </Field>
          {formError && <p role="alert">{formError}</p>}
          <div className="v1-actions">
            <Button appearance="secondary" onClick={handleUpdate}>
              Update draft
            </Button>
          </div>
        </>
      )}
    </section>
  );
}

export function PlatformPoliciesPage() {
  const policy = usePlatformPoliciesController();
  const page = policy.history;
  const empty = page?.policyId === null;
  const scopeName = policy.scope === 'platform' ? 'Platform' : 'Project';
  const selectedProjectMissing = policy.projectId &&
    !policy.projects.some((project) => project.projectId === policy.projectId);
  const validateDraftButtonRef = useRef<HTMLButtonElement>(null);
  const focusValidateDraftRef = useRef(false);

  useEffect(() => {
    if (!focusValidateDraftRef.current) return;
    if (policy.authorized && policy.status === 'ready' && policy.validation === null) {
      focusValidateDraftRef.current = false;
      validateDraftButtonRef.current?.focus();
    } else if (!policy.authorized) {
      focusValidateDraftRef.current = false;
    }
  }, [policy.authorized, policy.status, policy.validation]);

  const skillTrustTarget =
    policy.scope === 'project' &&
    policy.authorized &&
    policy.status === 'ready' &&
    !policy.validationLoading &&
    policy.validation?.isValid === true
      ? findProjectSkillTrustTarget(policy.draftFiles)
      : null;

  return (
    <>
      <PageHeading
        title={`${scopeName} Policies`}
        description={policy.scope === 'platform'
          ? 'Read saved source and revisions, then import, edit, validate, and download a local Platform Policy draft.'
          : 'Import, edit, validate, and download a local Project Policy draft.'}
        breadcrumb={false}
      />
      <section aria-label={`${scopeName} Policy access`}>
        <Field label="Policy scope">
          <select
            className="v1-select"
            aria-label="Policy scope"
            value={policy.scope}
            onChange={(event) => policy.setScope(event.currentTarget.value as 'platform' | 'project')}
          >
            <option value="platform">Platform</option>
            <option value="project">Project</option>
          </select>
        </Field>
        <Field label="Tenant ID">
          <Input
            value={policy.tenantId}
            onChange={(_, data) => policy.setTenantId(data.value)}
            autoComplete="off"
          />
        </Field>
        {policy.scope === 'project' && (
          <Field label="Project">
            <select
              className="v1-select"
              aria-label="Project"
              value={policy.projectId}
              onChange={(event) => policy.setProjectId(event.currentTarget.value)}
            >
              <option value="">Select a Project</option>
              {selectedProjectMissing && (
                <option value={policy.projectId}>{policy.projectId}</option>
              )}
              {policy.projects.map((project) => (
                <option key={project.projectId} value={project.projectId}>
                  {project.name} ({project.projectId})
                </option>
              ))}
            </select>
          </Field>
        )}
        <div className="v1-actions">
          <Button
            appearance="primary"
            data-platform-policies-retry
            disabled={policy.status === 'checking'}
            onClick={policy.retry}
          >
            {policy.status === 'checking' ? 'Checking…' : 'Retry'}
          </Button>
        </div>
      </section>

      {policy.status === 'checking' && (
        <p role="status" aria-live="polite">Checking current {scopeName} Policy access…</p>
      )}
      {policy.status === 'needsTenant' && (
        <p role="status">Enter a tenant ID, then select Retry.</p>
      )}
      {policy.status === 'denied' && (
        <p role="alert">{policy.scope === 'platform'
          ? 'You do not have permission to access Platform Policies.'
          : 'You do not have permission to access this Project Policy.'}</p>
      )}
      {policy.status === 'unavailable' && (
        <p role="alert">{policy.scope === 'platform'
          ? 'Platform Policies could not load. Select Retry.'
          : 'Project Policy access could not load. Select Retry.'}</p>
      )}
      {policy.draftError && <p role="alert">{policy.draftError}</p>}

      {policy.scope === 'project' &&
        policy.status === 'ready' &&
        !policy.projectId &&
        policy.projects.length === 0 && (
          <p role="status">No writable Projects are available in this tenant.</p>
        )}

      {policy.authorized && (policy.scope === 'project' || page) && (
        <>
        <section aria-label={`${scopeName} Policy draft`}>
          <h2>Draft editor</h2>
          <p>Validation checks this draft. It does not save or apply changes.</p>
          <Field label="Validation request">
            <input
              className="v1-select"
              type="file"
              accept=".json,application/json"
              aria-label={`${scopeName} Policy validation request`}
              disabled={policy.status !== 'ready'}
              onChange={(event) => {
                const input = event.currentTarget;
                const file = input.files?.[0];
                input.value = '';
                if (file) void policy.importDraft(file);
              }}
            />
          </Field>
          {policy.scope === 'platform' && policy.detail && (
            <Button appearance="secondary" onClick={policy.loadDraftFromSavedSource}>
              Load viewed revision into draft
            </Button>
          )}
          {policy.scope === 'platform' && (
            policy.detail ? (
              <PlatformPolicyComparisonControls
                key={`${policy.detail.policyId}:${policy.detail.revision}:${policy.draftGeneration}`}
                detail={policy.detail}
                draftManifest={policy.draftManifest}
                draftFiles={policy.draftFiles}
              />
            ) : (
              <p>Select View on a saved revision to compare.</p>
            )
          )}
          <Field label="Manifest">
            <PlatformPolicyTextarea
              label={`${scopeName} Policy manifest`}
              buffer={policy.draftManifest}
              onChange={policy.setDraftManifest}
              rows={16}
            />
          </Field>
          {policy.draftFiles.map((file) => (
            <section key={file.id} aria-label={`Policy file ${file.relativePath}`}>
              <div className="v1-actions">
                <h3>Policy file</h3>
                <Button
                  appearance="subtle"
                  aria-label={`Remove ${file.relativePath || 'policy file'}`}
                  onClick={() => policy.removeDraftFile(file.id)}
                >
                  Remove file
                </Button>
              </div>
              <Field label="Relative path">
                <Input
                  value={file.relativePath}
                  onChange={(_, data) => policy.setDraftFilePath(file.id, data.value)}
                  autoComplete="off"
                />
              </Field>
              <Field label="File content">
                <PlatformPolicyTextarea
                  draftFileId={file.id}
                  label={`Content for ${file.relativePath || 'policy file'}`}
                  buffer={file.content}
                  onChange={(content) => policy.setDraftFileContent(file.id, content)}
                  rows={12}
                />
              </Field>
            </section>
          ))}
          <div className="v1-actions">
            <Button appearance="secondary" onClick={policy.addDraftFile}>Add file</Button>
            <Button
              ref={validateDraftButtonRef}
              appearance="primary"
              disabled={policy.status !== 'ready' || policy.validationLoading}
              onClick={policy.validateDraft}
            >
              {policy.validationLoading ? 'Validating…' : 'Validate draft'}
            </Button>
            <Button
              appearance="secondary"
              disabled={policy.status !== 'ready'}
              onClick={policy.downloadDraft}
            >
              Download draft
            </Button>
          </div>
          {policy.validation?.isValid && (
            <p role="status">The {scopeName} Policy draft is valid.</p>
          )}
          {skillTrustTarget && (
            <ProjectSkillTrustScoreForm
              key={`${policy.tenantId}:${policy.projectId}:${policy.draftGeneration}`}
              target={skillTrustTarget}
              fileBuffer={policy.draftFiles.find((file) => file.id === skillTrustTarget.fileId)?.content}
              onUpdateDraft={(nextBuffer) => {
                focusValidateDraftRef.current = true;
                policy.setDraftFileContent(skillTrustTarget.fileId, nextBuffer);
              }}
            />
          )}
          {policy.validation && !policy.validation.isValid && (
            <section aria-label="Validation diagnostics" role="alert">
              <h3>The {scopeName} Policy draft has errors.</h3>
              {policy.validation.diagnostics.length === 0 ? (
                <p>The validator returned no diagnostic details.</p>
              ) : (
                <ul>
                  {policy.validation.diagnostics.map((diagnostic, index) => {
                    const matchingFile = findPlatformPolicyDiagnosticDraftFile(
                      diagnostic.relativePath,
                      policy.draftFiles,
                    );
                    return (
                      <li key={`${diagnostic.code}-${index}`}>
                        {diagnostic.relativePath && <code>{diagnostic.relativePath}</code>}
                        {diagnostic.line !== null && ` line ${diagnostic.line}`}
                        {diagnostic.column !== null && `, column ${diagnostic.column}`}
                        {(diagnostic.relativePath || diagnostic.line !== null ||
                          diagnostic.column !== null) && ': '}
                        {diagnostic.message}
                        {matchingFile && (
                          <Button
                            appearance="subtle"
                            size="small"
                            aria-label={`Go to draft file: ${matchingFile.relativePath}`}
                            data-platform-policy-diagnostic-navigation
                            onClick={() => document.querySelector<HTMLTextAreaElement>(
                              `[data-platform-policy-draft-file-id="${matchingFile.id}"]`,
                            )?.focus()}
                          >
                            Go to draft file
                          </Button>
                        )}
                      </li>
                    );
                  })}
                </ul>
              )}
            </section>
          )}
        </section>

        {policy.scope === 'platform' && page && (
        <section aria-label="Saved Platform Policy" aria-live="polite">
          <div className="v1-actions">
            <Badge appearance="tint" color="informative">Saved source</Badge>
            <Text className="v1-muted">This view does not show runtime enforcement.</Text>
          </div>

          {empty ? (
            <p>No Platform Policy has been saved.</p>
          ) : (
            <>
              <div className="v1-table-wrap">
                <table className="v1-table">
                  <caption>Saved Platform Policy revisions</caption>
                  <thead>
                    <tr>
                      <th scope="col">Revision</th>
                      <th scope="col">Created</th>
                      <th scope="col">Content hash</th>
                      <th scope="col">Source</th>
                    </tr>
                  </thead>
                  <tbody>
                    {page.items.map((revision) => (
                      <tr key={revision.revision}>
                        <th scope="row">
                          {revision.revision}
                          {revision.revision === page.snapshotRevision && (
                            <span className="v1-muted"> · snapshot head</span>
                          )}
                        </th>
                        <td>{createdAtLabel(revision.createdAt)}</td>
                        <td><code>{revision.contentSha256}</code></td>
                        <td>
                          <Button
                            appearance="subtle"
                            disabled={policy.detailLoading || policy.historyLoading}
                            onClick={() => policy.viewRevision(revision)}
                          >
                            View
                          </Button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {policy.historyError && <p role="alert">{policy.historyError}</p>}
              {page.nextCursor && (
                <Button
                  disabled={policy.historyLoading || policy.detailLoading}
                  onClick={policy.loadMore}
                >
                  {policy.historyLoading ? 'Loading…' : 'Load more'}
                </Button>
              )}
            </>
          )}
        </section>
        )}
        </>
      )}

      {policy.detailLoading && <p role="status" aria-live="polite">Loading saved source…</p>}
      {policy.detailError && <p role="alert">{policy.detailError}</p>}
      {policy.detail && (
        <section aria-label={`Saved Platform Policy revision ${policy.detail.revision}`}>
          <h2>Revision {policy.detail.revision}</h2>
          <p><strong>Policy ID:</strong> <code>{policy.detail.policyId}</code></p>
          <p><strong>Created:</strong> {createdAtLabel(policy.detail.createdAt)}</p>
          <p><strong>Content hash:</strong> <code>{policy.detail.contentSha256}</code></p>
          <h3>Manifest</h3>
          <pre className="v1-policy-source">{policy.detail.artifact.manifest}</pre>
          {policy.detail.artifact.files.map((file) => (
            <div key={file.relativePath}>
              <h3>{file.relativePath}</h3>
              <pre className="v1-policy-source">{file.content}</pre>
            </div>
          ))}
        </section>
      )}
    </>
  );
}
