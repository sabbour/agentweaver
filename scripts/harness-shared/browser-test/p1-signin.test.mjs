import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { access, readdir } from 'node:fs/promises';
import { chromium } from 'playwright';
import {
  authorizeP1InBrowser,
  createP1AuthorizationRequest,
  P1_SETUP_SCOPES,
  withFreshP1BrowserContext,
} from '../p1-signin.mjs';

// Loopback redirects reproduce the browser behavior without a real account or Broker.
test('Chrome blocks a redirect-hop callback before the Portal document or script loads', async t => {
  for (const scenario of ['valid state', 'invalid state', 'redirected consent']) {
    await t.test(scenario, async () => {
      const invalidState = scenario === 'invalid state';
      const needsConsent = scenario === 'redirected consent';
      let callback;
      let authorizeUrl;
      let approved = false;
      let portalRequests = 0;
      let spaExchanges = 0;
      let launcherExchanges = 0;
      const hops = [];
      const server = createServer((request, response) => {
        const url = new URL(request.url, 'http://127.0.0.1');
        if (url.pathname === '/connect/authorize') {
          authorizeUrl = new URL(request.url, issuer);
          callback = new URL(url.searchParams.get('redirect_uri'));
          callback.searchParams.set('code', 'loopback-code');
          callback.searchParams.set('state', invalidState ? 'invalid' : url.searchParams.get('state'));
          if (approved) {
            response.writeHead(302, { location: callback.toString() }).end();
          } else if (needsConsent && url.searchParams.has('nonce')) {
            response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
              consent_required: true,
              consent_handle: 'loopback-consent',
              client_id: 'agentweaver-web',
              requested_scopes: P1_SETUP_SCOPES,
              csrf_token: 'loopback-csrf',
            }));
          } else {
            response.writeHead(302, { location: '/entra' }).end();
          }
        } else if (url.pathname === '/entra') {
          hops.push('entra');
          response.writeHead(302, { location: '/broker' }).end();
        } else if (url.pathname === '/broker') {
          hops.push('broker');
          authorizeUrl.searchParams.set('nonce', 'loopback-nonce');
          response.writeHead(302, {
            location: needsConsent ? authorizeUrl.toString() : callback.toString(),
          }).end();
        } else if (url.pathname === '/connect/consent') {
          approved = true;
          response.writeHead(302, { location: callback.toString() }).end();
        } else if (url.pathname === '/auth/callback') {
          portalRequests += 1;
          response.writeHead(200, { 'content-type': 'text/html' })
            .end('<!doctype html><script>fetch("/spa-exchange", {method:"POST"})</script>');
        } else if (url.pathname === '/spa-exchange') {
          spaExchanges += 1;
          response.writeHead(200).end();
        } else {
          response.writeHead(404).end();
        }
      });
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      const issuer = `http://127.0.0.1:${server.address().port}/`;
      const environment = { issuer, audience: 'loopback', actorId: 'test-actor' };
      const token = ['header', Buffer.from(JSON.stringify({
        iss: issuer,
        aud: environment.audience,
        sub: environment.actorId,
        exp: Date.now() / 1000 + 600,
        scope: P1_SETUP_SCOPES.join(' '),
      })).toString('base64url'), 'signature'].join('.');
      let profile;
      try {
        const signing = withFreshP1BrowserContext({
          executablePath: chromium.executablePath(),
          chromium: {
            async launchPersistentContext(userDataDir) {
              profile = userDataDir;
              assert.deepEqual(await readdir(profile), []);
              return chromium.launchPersistentContext(userDataDir, { headless: true });
            },
          },
        }, async context => {
          const signing = authorizeP1InBrowser({
            context,
            environment,
            scopes: P1_SETUP_SCOPES,
            timeoutMs: 10_000,
            io: { log() {} },
            makeRequest: options => createP1AuthorizationRequest({
              ...options, redirectUri: `${issuer}auth/callback`,
            }),
            fetchImpl: async (_url, options) => {
              launcherExchanges += 1;
              assert.equal(options.body.get('code'), 'loopback-code');
              return { ok: true, async json() { return { token_type: 'Bearer', access_token: token }; } };
            },
          });
          const [result] = await Promise.all([
            signing,
            ...(needsConsent ? [context.pages()[0].getByRole('button', { name: 'Approve', exact: true }).click()] : []),
          ]);
          return result;
        });
        if (invalidState) await assert.rejects(signing, /P1 sign-in could not be completed/);
        else assert.equal(await signing, token);
        assert.deepEqual(hops, ['entra', 'broker']);
        assert.equal(portalRequests, 0, 'the callback must never reach the Portal server');
        assert.equal(spaExchanges, 0, 'the Portal must never consume the code');
        assert.equal(launcherExchanges, invalidState ? 0 : 1);
        assert.equal(approved, needsConsent);
        await assert.rejects(access(profile), { code: 'ENOENT' });
      } finally {
        server.closeAllConnections();
        await new Promise(resolve => server.close(resolve));
      }
    });
  }
});
