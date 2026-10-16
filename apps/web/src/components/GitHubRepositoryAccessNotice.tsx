import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { GitHubRepositoryInstallation } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  installationList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  installation: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
});

interface GitHubRepositoryAccessNoticeProps {
  installations: GitHubRepositoryInstallation[] | null;
  repositoryCount: number;
  repoAppInstallUrl?: string | null;
}

export function GitHubRepositoryAccessNotice({
  installations,
  repositoryCount,
  repoAppInstallUrl,
}: GitHubRepositoryAccessNoticeProps) {
  const styles = useStyles();

  return (
    <div className={styles.root} data-testid="github-repository-access-notice">
      <Text>
        Repositories shown here are limited to repositories available to both your GitHub account
        and the Agentweaver GitHub App installation.
      </Text>

      {installations?.length === 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            The Agentweaver GitHub App is not installed for an account you can access.
          </MessageBarBody>
          {repoAppInstallUrl && (
            <MessageBarActions>
              <Button
                as="a"
                href={repoAppInstallUrl}
                target="_blank"
                rel="noopener noreferrer"
                size="small"
              >
                Install Agentweaver GitHub App
              </Button>
            </MessageBarActions>
          )}
        </MessageBar>
      )}

      {installations && installations.length > 0 && (
        <>
          <Text weight="semibold">Manage repository access in GitHub</Text>
          <div className={styles.installationList} role="list" aria-label="GitHub App installations">
            {installations.map((installation) => (
              <div
                className={styles.installation}
                role="listitem"
                key={`${installation.account_type}:${installation.account_login}`}
              >
                <Text>
                  {installation.repository_selection === 'selected'
                    ? `Agentweaver has access only to selected repositories for ${installation.account_login}.`
                    : `Agentweaver has access to all repositories for ${installation.account_login}.`}
                </Text>
                <Button
                  as="a"
                  href={installation.management_url}
                  target="_blank"
                  rel="noopener noreferrer"
                  size="small"
                  appearance="subtle"
                  aria-label={`Open GitHub installation settings for ${installation.account_login}`}
                >
                  Open GitHub installation settings
                </Button>
              </div>
            ))}
          </div>
        </>
      )}

      {installations && installations.length > 0 && repositoryCount === 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            No repositories are available through the current GitHub App installation access.
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
