import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage } from '../api/errors';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  Link as FluentLink,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import type { RepositoryOwner } from '../api/types';

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

/** Derives the same kebab-case default repo name the backend falls back to, e.g. "My Project" -> "my-project". */
function slugify(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/(^-+)|(-+$)/g, '') || 'project';
}

function formatError(err: unknown): string {
  return formatApiErrorMessage(err, 'Could not connect the GitHub repository.');
}


interface ConnectGitHubRepositoryDialogProps {
  projectId: string;
  projectName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Called with the connected repo's "owner/repo" once the connection succeeds. */
  onConnected: (sourceRepository: string, htmlUrl: string) => void;
}

/**
 * Shared connect/create-repository flow for a currently-unconnected (Blank-origin) project — reused
 * by both Project Settings and the more prominent dashboard banner (issue: allow creating a GitHub
 * repository for a project that has none connected). Lists the caller's own login plus orgs they
 * belong to via GET /api/projects/{id}/github/repository-owners so the owner is picked by the user,
 * never auto-selected, then creates the repository and pushes the project's existing local history to
 * it via POST /api/projects/{id}/github/repository.
 */
export function ConnectGitHubRepositoryDialog({
  projectId,
  projectName,
  open,
  onOpenChange,
  onConnected,
}: ConnectGitHubRepositoryDialogProps) {
  const styles = useStyles();
  const [owners, setOwners] = useState<RepositoryOwner[]>([]);
  const [ownersLoading, setOwnersLoading] = useState(false);
  const [ownersError, setOwnersError] = useState<string | null>(null);
  const [owner, setOwner] = useState('');
  const [name, setName] = useState('');
  const [isPrivate, setIsPrivate] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<{ sourceRepository: string; htmlUrl: string } | null>(null);

  const defaultName = slugify(projectName);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setOwnersLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setOwnersError(null);
    setResult(null);
    setError(null);
    void apiClient.listProjectRepositoryOwners(projectId)
      .then((list) => {
        if (cancelled) return;
        setOwners(list);
        if (list.length > 0) setOwner((prev) => prev || list[0].login);
      })
      .catch((err) => {
        if (cancelled) return;
        setOwnersError(formatError(err));
      })
      .finally(() => {
        if (!cancelled) setOwnersLoading(false);
      });
    return () => { cancelled = true; };
  }, [open, projectId]);

  const handleCreate = async () => {
    if (!owner) return;
    setSaving(true);
    setError(null);
    try {
      const connected = await apiClient.createProjectRepository(projectId, {
        owner,
        name: name.trim() || undefined,
        private: isPrivate,
      });
      setResult({ sourceRepository: connected.source_repository, htmlUrl: connected.html_url });
      onConnected(connected.source_repository, connected.html_url);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSaving(false);
    }
  };

  const handleClose = () => {
    onOpenChange(false);
    setName('');
    setResult(null);
    setError(null);
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => onOpenChange(data.open)}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle
              action={
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              }
            >Connect a GitHub repository</DialogTitle>
          <DialogContent className={styles.stack}>
            {result ? (
              <MessageBar intent="success">
                <MessageBarBody>
                  Connected to{' '}
                  <FluentLink href={result.htmlUrl} target="_blank" rel="noreferrer">
                    {result.sourceRepository}
                  </FluentLink>
                  . The project's existing history has been pushed to it.
                </MessageBarBody>
              </MessageBar>
            ) : (
              <>
                {ownersLoading && <Spinner label="Loading GitHub accounts" />}
                {ownersError && (
                  <MessageBar
                    intent={ownersConnectionRequired ? 'warning' : 'error'}
                    data-testid="connect-github-repository-owners-error"
                    data-intent={ownersConnectionRequired ? 'warning' : 'error'}
                  >
                    <MessageBarBody>{ownersError}</MessageBarBody>
                  </MessageBar>
                )}
                {!ownersLoading && !ownersError && (
                  <>
                    <Field label="Owner">
                      <Select value={owner} onChange={(_, data) => setOwner(data.value)}>
                        {owners.map((o) => (
                          <option key={o.login} value={o.login}>
                            {o.login} {o.type === 'org' ? '(organization)' : '(you)'}
                          </option>
                        ))}
                      </Select>
                    </Field>
                    <Field label="Repository name">
                      <Input
                        value={name}
                        placeholder={defaultName}
                        onChange={(_, data) => setName(data.value)}
                      />
                    </Field>
                    <Switch
                      checked={isPrivate}
                      onChange={(_, data) => setIsPrivate(data.checked)}
                      label="Private repository"
                    />
                  </>
                )}
                {error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{error}</MessageBarBody>
                  </MessageBar>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={handleClose}>
              {result ? 'Close' : 'Cancel'}
            </Button>
            {!result && (
              <Button
                appearance="primary"
                disabled={!owner || saving || ownersLoading}
                onClick={() => void handleCreate()}
                style={{ whiteSpace: 'nowrap' }}
              >
                {saving ? <Spinner size="tiny" /> : 'Create repository'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
