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

  return (
    <>
      <PageHeading
        title="Platform Policies"
        description="Read saved source and revisions, edit a raw draft, and validate it."
        breadcrumb={false}
      />
      <section aria-label="Platform Policy access">
        <Field label="Tenant ID">
          <Input
            value={policy.tenantId}
            onChange={(_, data) => policy.setTenantId(data.value)}
            autoComplete="off"
          />
        </Field>
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
        <p role="status" aria-live="polite">Checking current Platform Policy access…</p>
      )}
      {policy.status === 'needsTenant' && (
        <p role="status">Enter a tenant ID, then select Retry.</p>
      )}
      {policy.status === 'denied' && (
        <p role="alert">You do not have permission to access Platform Policies.</p>
      )}
      {policy.status === 'unavailable' && (
        <p role="alert">Platform Policies could not load. Select Retry.</p>
      )}
      {policy.draftError && <p role="alert">{policy.draftError}</p>}

      {policy.authorized && page && (
        <>
        <section aria-label="Platform Policy draft">
          <h2>Draft editor</h2>
          <p>Validation checks this draft. It does not save or apply changes.</p>
          {policy.detail && (
            <Button appearance="secondary" onClick={policy.loadDraftFromSavedSource}>
              Load viewed revision into draft
            </Button>
          )}
          <Field label="Manifest">
            <PlatformPolicyTextarea
              label="Platform Policy manifest"
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
            <p role="status">The Platform Policy draft is valid.</p>
          )}
          {policy.validation && !policy.validation.isValid && (
            <section aria-label="Validation diagnostics" role="alert">
              <h3>The Platform Policy draft has errors.</h3>
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
