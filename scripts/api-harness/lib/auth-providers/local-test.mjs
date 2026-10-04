export const LOCAL_TEST_AUTH_PROVIDER = 'local-test';
const DEFAULT_ENV_NAME = 'AGENTWEAVER_LOCAL_TEST_BEARER';

export function createLocalTestAuthProvider({
  env = process.env,
  envName = DEFAULT_ENV_NAME,
} = {}) {
  let authorization;
  return {
    name: LOCAL_TEST_AUTH_PROVIDER,
    async getAuthorization() {
      if (authorization) return authorization;
      const bearer = env[envName];
      if (typeof bearer !== 'string' || !bearer.trim()) {
        throw new Error(`${envName} is required for the local-test API harness auth provider.`);
      }
      const value = bearer.trim();
      authorization = value.startsWith('Bearer ') ? value : `Bearer ${value}`;
      return authorization;
    },
  };
}
