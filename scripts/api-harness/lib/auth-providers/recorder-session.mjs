import { getSessionToken } from '../../../demo-recording/lib/auth.mjs';
import { recordingAuthPaths } from '../../../demo-recording/lib/recording-session.mjs';

export const RECORDER_SESSION_AUTH_PROVIDER = 'recorder-session';

export function createRecorderSessionAuthProvider({
  authRoot,
  getSessionTokenFn = getSessionToken,
  recordingAuthPathsFn = recordingAuthPaths,
} = {}) {
  const { sessionStoragePath } = recordingAuthPathsFn(authRoot);

  return {
    name: RECORDER_SESSION_AUTH_PROVIDER,
    async getAuthorization() {
      const token = await getSessionTokenFn(sessionStoragePath);
      if (typeof token !== 'string' || token.length === 0) {
        throw new Error('Protected recording authentication did not provide a session token.');
      }
      return `Bearer ${token}`;
    },
  };
}
