import { getSessionToken } from '../../../demo-recording/lib/auth.mjs';
import {
  openRecordingSession,
  parseRecordingCommandOptions,
  refreshRecordingAuthentication,
  recordingAuthPaths,
} from '../../../demo-recording/lib/recording-session.mjs';

export const RECORDER_SESSION_AUTH_PROVIDER = 'recorder-session';

export function createRecorderSessionAuthProvider({
  authRoot,
  baseUrl,
  session,
  getSessionTokenFn = getSessionToken,
  recordingAuthPathsFn = recordingAuthPaths,
  openRecorderSessionFn = openRecordingSession,
  refreshRecorderAuthenticationFn = refreshRecordingAuthentication,
  parseOpenOptionsFn = parseRecordingCommandOptions,
} = {}) {
  const { sessionStoragePath } = recordingAuthPathsFn(authRoot);
  let readiness;
  let refresh;

  const ensureReady = async () => {
    if (!readiness) {
      readiness = Promise.resolve().then(async () => {
        if (typeof baseUrl !== 'string' || !baseUrl.trim()) {
          throw new Error('A target base URL is required to start managed-browser authentication.');
        }
        const argv = ['--base-url', baseUrl];
        if (authRoot) argv.push('--auth-root', authRoot);
        if (session) argv.push('--session', session);
        await openRecorderSessionFn(parseOpenOptionsFn('open', argv));
      }).catch((error) => {
        readiness = null;
        throw error;
      });
    }
    await readiness;
  };

  const refreshAndRestore = async () => {
    if (!refresh) {
      refresh = Promise.resolve().then(async () => {
        const argv = ['--base-url', baseUrl];
        if (authRoot) argv.push('--auth-root', authRoot);
        if (session) argv.push('--session', session);
        const options = parseOpenOptionsFn('open', argv);
        await refreshRecorderAuthenticationFn(options);
        readiness = null;
        await ensureReady();
      }).finally(() => {
        refresh = null;
      });
    }
    await refresh;
  };

  const readSessionToken = async () => {
    const token = await getSessionTokenFn(sessionStoragePath);
    if (typeof token !== 'string' || token.length === 0) {
      throw new Error('Protected recording authentication did not provide a session token.');
    }
    return token;
  };

  return {
    name: RECORDER_SESSION_AUTH_PROVIDER,
    async getAuthorization() {
      await ensureReady();
      try {
        return `Bearer ${await readSessionToken()}`;
      } catch {
        // A globally-open recorder session can be valid while this worktree has no
        // protected sidecar yet. Refreshing restores both the session and its local handoff.
        await refreshAndRestore();
        return `Bearer ${await readSessionToken()}`;
      }
    },
  };
}
