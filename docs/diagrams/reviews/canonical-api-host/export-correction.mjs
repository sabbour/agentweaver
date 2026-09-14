import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { drawioExportArgs, verifyDrawioVersion } from '../../../../scripts/docs/diagram-sources.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const pass = Number(process.argv[2]);
if (![2, 3, 4].includes(pass)) throw new Error('Only correction passes 2-4 are supported.');
const previous = `canonical-api-host-pass-${String(pass - 1).padStart(2, '0')}`;
const next = `canonical-api-host-pass-${String(pass).padStart(2, '0')}`;
const cli = path.join(here, 'renderer/desktop/draw.io.exe');
verifyDrawioVersion({ command: cli, prefixArgs: [] });
const source = await fs.readFile(path.join(here, `${previous}.drawio`));
await fs.writeFile(path.join(here, `${next}.drawio`), source, { flag: 'wx' });
execFileSync(cli, [
  `--user-data-dir=${path.join(here, `renderer/profile-${next}`)}`,
  ...drawioExportArgs(path.join(here, `${next}.drawio`), path.join(here, `${next}.png`), 'png'),
], { stdio: 'inherit' });
console.log(`${next}: no-change correction pass; inspect its new PNG before advancing.`);
