import { copyFile, readFile, unlink, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  createDiagramStamp, validateUncompressedDrawio, verifyDrawioVersion,
} from '../../../../scripts/docs/diagram-sources.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../../..');
const plan = JSON.parse(await readFile(path.join(root, '.github/skills/docs-diagram-audit/reports/plan-guide.json'), 'utf8'));
const allowed = new Set(plan.exclusive_asset_paths.filter(p => !p.endsWith('/')));
const names = ['guide-architecture-aks-fig1', 'guide-architecture-aks-fig5',
  'canonical-aks-network', 'guide-example-scenarios-fig3'];
const renderer = path.join(here, 'renderer-temp/desktop/draw.io.exe');
const version = verifyDrawioVersion({ command: renderer });
if (version !== '31.4.5') throw new Error(`Unexpected renderer ${version}`);
function owned(relative) {
  if (!allowed.has(relative)) throw new Error(`Not exclusively owned: ${relative}`);
  return path.join(root, relative);
}
for (const name of names) {
  const folder = path.resolve(here, '..', name);
  const manifest = JSON.parse(await readFile(path.join(folder, 'iteration-manifest.json'), 'utf8'));
  const final = manifest.passes.at(-1);
  if (manifest.final_pass !== 4 || !final.all_arrows_traced
      || final.orientation_defects || final.overlap_defects || final.arrow_defects) {
    throw new Error(`Final review gates are not satisfied: ${name}`);
  }
  const drawio = owned(`docs/diagrams/src/${name}.drawio`);
  const png = owned(`docs/diagrams/${name}.png`);
  validateUncompressedDrawio(await readFile(path.join(folder, final.drawio), 'utf8'));
  await copyFile(path.join(folder, final.drawio), drawio);
  await copyFile(path.join(folder, final.png), png);
  for (const obsolete of [`docs/diagrams/src/${name}.json`,
    `docs/diagrams/drawio/generated/${name}.drawio`]) {
    const target = owned(obsolete);
    if (existsSync(target)) await unlink(target);
  }
  const stamp = await createDiagramStamp({ name, kind: 'drawio', path: drawio },
    drawio, png, { rendererVersion: version });
  await writeFile(owned(`docs/diagrams/${name}.hash.txt`), `${JSON.stringify(stamp, null, 2)}\n`);
  console.log(`Promoted ${name}: editable A5 source, final inspected PNG, verified ${version} stamp`);
}
