import { createServer } from "node:http";
import { readFile } from "node:fs/promises";

const html = new URL("./renderer.html", import.meta.url);
const script = new URL("./renderer.js", import.meta.url);

export async function startPanel(observer) {
  const [page, javascript] = await Promise.all([readFile(html), readFile(script)]);
  const server = createServer(async (req, res) => {
    const host = req.headers.host;
    const origin = `http://${host}`;
    if (!/^127\.0\.0\.1:\d+$/.test(host || "") ||
        (req.method === "POST" && req.headers.origin && req.headers.origin !== origin)) {
      res.writeHead(403); res.end(); return;
    }
    res.setHeader("Cache-Control", "no-store");
    res.setHeader("X-Content-Type-Options", "nosniff");
    res.setHeader("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'");
    try {
      if (req.method === "GET" && req.url === "/") {
        res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }); res.end(page);
      } else if (req.method === "GET" && req.url === "/renderer.js") {
        res.writeHead(200, { "Content-Type": "text/javascript; charset=utf-8" }); res.end(javascript);
      } else if (req.method === "GET" && req.url === "/state") {
        const model = await observer.state();
        res.writeHead(200, { "Content-Type": "application/json" }); res.end(JSON.stringify(model));
      } else if (req.method === "POST" && req.url === "/refresh") {
        await observer.refresh(true);
        res.writeHead(200, { "Content-Type": "application/json" }); res.end(JSON.stringify({ refreshed: true }));
      } else {
        res.writeHead(404); res.end();
      }
    } catch (error) {
      if (!res.headersSent) res.writeHead(503, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: `Observation failed: ${error.code || error.name || "unknown"}` }));
    }
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => { server.off("error", reject); resolve(); });
  });
  const url = `http://127.0.0.1:${server.address().port}/`;
  return {
    url,
    close: async () => {
      server.closeAllConnections();
      await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    },
  };
}
