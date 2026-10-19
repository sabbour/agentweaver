import { connect as connectHttp2 } from 'node:http2';
import { isIP } from 'node:net';
import { connect as connectTls } from 'node:tls';
import { pathToFileURL } from 'node:url';

/** Sends one HTTP/2 request with an explicit SNI and authority to the Gateway IP. */
export function probeHttp2Authority({ address, servername, authority, path, timeoutMs = 10_000 }) {
  if (!isIP(address) || !servername || !authority || !path?.startsWith('/') ||
      !Number.isInteger(timeoutMs) || timeoutMs < 1) {
    throw new Error('HTTP/2 route probe requires a Gateway IP, hostnames, absolute path, and positive timeout.');
  }

  return new Promise((resolve, reject) => {
    let settled = false;
    let request;
    const session = connectHttp2(`https://${servername}`, {
      createConnection: () => connectTls({
        host: address, port: 443, servername, ALPNProtocols: ['h2'], rejectUnauthorized: true,
      }),
    });
    const timeout = setTimeout(() => fail(new Error('probe timed out')), timeoutMs);
    const fail = error => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      session.destroy();
      reject(error);
    };

    session.on('error', fail);
    session.once('connect', () => {
      if (session.socket.alpnProtocol !== 'h2') {
        fail(new Error(`expected h2 ALPN, received ${session.socket.alpnProtocol || 'none'}`));
        return;
      }

      request = session.request({ ':method': 'GET', ':path': path, ':authority': authority });
      let status;
      request.on('response', headers => { status = Number(headers[':status']); });
      request.on('error', fail);
      request.once('end', () => {
        if (settled) return;
        settled = true;
        clearTimeout(timeout);
        session.close();
        resolve({ alpn: 'h2', status });
      });
      request.resume();
      request.end();
    });
  });
}

async function main() {
  const [address, servername, authority, path] = process.argv.slice(2);
  try {
    process.stdout.write(JSON.stringify(await probeHttp2Authority({ address, servername, authority, path })));
  } catch (error) {
    process.stderr.write(`HTTP/2 route probe failed: ${error.code ?? error.message}\n`);
    process.exitCode = 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
