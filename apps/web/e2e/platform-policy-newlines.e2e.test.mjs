/**
 * Browser check for raw Policy text round-tripping through Fluent's textarea.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { createServer as createViteServer } from 'vite';
import { openApp, startStaticServer } from './harness.mjs';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

test('policy text keeps mixed line endings after a browser textarea edit', async () => {
  const vite = await createViteServer({
    root: resolve('.'),
    server: { host: '127.0.0.1', port: 0 },
    logLevel: 'error',
  });
  await vite.listen();
  let app;
  try {
    app = await openApp({ origin: server.origin });
    const port = vite.httpServer.address().port;
    await app.page.goto(`http://127.0.0.1:${port}/e2e/platform-policy-newlines.html`);
    const editor = app.page.getByRole('textbox', { name: 'Platform Policy text' });
    await editor.waitFor();
    assert.equal(await editor.inputValue(), 'first\nsecond\nthird\n');
    await editor.press('Control+Home');
    await editor.press('ArrowDown');
    await editor.press('End');
    await editor.pressSequentially(' changed');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      'first\r\nsecond changed\rthird\n',
    );

    await app.page.getByRole('button', { name: 'Load adjacent separators' }).click();
    assert.equal(await editor.inputValue(), 'first\n\nthird\n');
    await editor.press('Control+Home');
    await editor.press('ArrowDown');
    await editor.press('Backspace');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      'first\rthird\n',
    );

    await editor.press('Control+Z');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      'first\r\n\rthird\n',
    );
    await editor.press('Control+Y');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      'first\rthird\n',
    );

    await app.page.goto(`http://127.0.0.1:${port}/e2e/platform-policy-newlines.html?many`);
    const longLines = Array.from({ length: 23 }, (_, index) => `line-${index}`);
    const lineEndings = ['\r\n', '\r', '\n'];
    const longSource = longLines.reduce((text, line, index) =>
      index === 0 ? line : `${text}${lineEndings[(index - 1) % lineEndings.length]}${line}`, '');
    assert.equal(await editor.inputValue(), longSource.replace(/\r\n|\r/g, '\n'));
    for (let index = 0; index < 21; index += 1) {
      const separatorIndex = await editor.evaluate((element) => element.value.indexOf('\n'));
      await editor.evaluate((element, position) => element.setSelectionRange(position, position), separatorIndex);
      await editor.press('Delete');
    }
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      `${longLines.slice(0, 22).join('')}${lineEndings[21 % lineEndings.length]}${longLines[22]}`,
    );
    for (let index = 0; index < 21; index += 1)
      await editor.press('Control+Z');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      longSource,
    );

    await app.page.getByRole('button', { name: 'Load adjacent separators' }).click();
    await editor.press('Control+Home');
    await editor.press('ArrowDown');
    await editor.press('Control+Backspace');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      'first\rthird\n',
    );

    await app.page.getByRole('button', { name: 'Load adjacent separators' }).click();
    await editor.press('Control+Home');
    await editor.press('End');
    await editor.press('Control+Delete');
    await app.page.getByRole('button', { name: 'Serialize' }).click();
    assert.equal(
      await app.page.locator('#serialized').evaluate((element) => element.textContent),
      'first\rthird\n',
    );
  } finally {
    await app?.close();
    await vite.close();
  }
});
