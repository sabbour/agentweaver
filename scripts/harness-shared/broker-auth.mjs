import { validateNetworkTarget } from './target-guard.mjs';

export function createBrokerAuthProvider({
  target, token, issuer, audience, actorId, now = Date.now,
}) {
  const origin = validateNetworkTarget(target).origin;
  if (!issuer || !audience || !actorId || typeof token !== 'string') {
    throw new Error('An explicit target, broker token, issuer, audience, and actor are required.');
  }
  let claims;
  try {
    const parts = token.split('.');
    if (parts.length !== 3) throw new Error('Invalid JWT shape.');
    claims = JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'));
  } catch {
    throw new Error('The supplied broker token has an invalid JWT payload.');
  }
  const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (claims.iss !== issuer || claims.sub !== actorId || !audiences.includes(audience)
    || !Number.isFinite(claims.exp)) {
    throw new Error('The supplied broker token does not match the selected issuer, audience, and actor.');
  }
  return {
    name: 'supplied-broker-token',
    origin,
    async getAuthorization(destination) {
      if (validateNetworkTarget(destination).origin !== origin) {
        throw new Error('Refusing broker authentication outside its selected target origin.');
      }
      if (claims.exp * 1000 <= now()) {
        throw new Error('AUTH_EXPIRED: the supplied broker token has expired; no refresh was attempted.');
      }
      return ['Bearer', token].join(' ');
    },
  };
}
