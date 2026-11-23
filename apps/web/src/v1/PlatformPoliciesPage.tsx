import { Badge, Button, Field, Input, Text } from '@fluentui/react-components';
import { usePlatformPoliciesController } from './PlatformPoliciesController';
import { PlatformPolicyTextarea } from './PlatformPolicyTextarea';
import { PageHeading } from './pageParts';

function createdAtLabel(value: string): string {
  return new Date(value).toLocaleString();
}

export function PlatformPoliciesPage() {
  const policy = usePlatformPoliciesController();
  const page = policy.history;
  const empty = page?.policyId === null;
  const scopeName = policy.scope === 'platform' ? 'Platform' : 'Project';
  const selectedProjectMissing = policy.projectId &&
    !policy.projects.some((project) => project.projectId === policy.projectId);

  return (
    <>
      <PageHeading
        title={`${scopeName} Policies`}
        description={policy.scope === 'platform'
          ? 'Read saved source and revisions, edit a raw draft, and validate it.'
          : 'Import and edit a local Project Policy validation request.'}
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
          {policy.scope === 'project' && (
            <Field label="Validation request">
              <input
                className="v1-select"
                type="file"
                accept=".json,application/json"
                aria-label="Project Policy validation request"
                onChange={async (event) => {
                  const input = event.currentTarget;
                  const file = input.files?.[0];
                  if (file) await policy.importDraft(file);
                  input.value = '';
                }}
              />
            </Field>
          )}
          {policy.scope === 'platform' && policy.detail && (
            <Button appearance="secondary" onClick={policy.loadDraftFromSavedSource}>
              Load viewed revision into draft
            </Button>
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
              appearance="primary"
              disabled={policy.status !== 'ready' || policy.validationLoading}
              onClick={policy.validateDraft}
            >
              {policy.validationLoading ? 'Validating…' : 'Validate draft'}
            </Button>
          </div>
          {policy.validation?.isValid && (
            <p role="status">The {scopeName} Policy draft is valid.</p>
          )}
          {policy.validation && !policy.validation.isValid && (
            <section aria-label="Validation diagnostics" role="alert">
              <h3>The {scopeName} Policy draft has errors.</h3>
              {policy.validation.diagnostics.length === 0 ? (
                <p>The validator returned no diagnostic details.</p>
              ) : (
                <ul>
                  {policy.validation.diagnostics.map((diagnostic, index) => (
                    <li key={`${diagnostic.code}-${index}`}>
                      {diagnostic.relativePath && <code>{diagnostic.relativePath}</code>}
                      {diagnostic.line !== null && ` line ${diagnostic.line}`}
                      {diagnostic.column !== null && `, column ${diagnostic.column}`}
                      {(diagnostic.relativePath || diagnostic.line !== null ||
                        diagnostic.column !== null) && ': '}
                      {diagnostic.message}
                    </li>
                  ))}
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
