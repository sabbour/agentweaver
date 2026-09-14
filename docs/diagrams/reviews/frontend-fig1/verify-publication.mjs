import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { models } from './batch-models.mjs';
import { drawioExportArgs, verifyDrawioVersion, fileHash, createDiagramStamp } from '../../../../scripts/docs/diagram-sources.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../../..');
const cli = path.join(repo, 'docs/diagrams/reviews/canonical-api-host/renderer/desktop/draw.io.exe');
const profile = path.join(here, 'desktop-profile');
const version = verifyDrawioVersion({ command: cli, prefixArgs: [`--user-data-dir=${profile}`] });
if (version !== '31.4.5') throw new Error(`Unexpected Desktop version: ${version}`);
const reports = [];
for (const { name } of models) {
  const review = path.join(repo, 'docs/diagrams/reviews', name);
  const source = path.join(repo, 'docs/diagrams/src', `${name}.drawio`);
  const generated = path.join(repo, 'docs/diagrams/drawio/generated', `${name}.drawio`);
  const published = path.join(repo, 'docs/diagrams', `${name}.png`);
  const verification = path.join(review, `${name}-canonical-reexport.png`);
  execFileSync(cli, [
    `--user-data-dir=${profile}`,
    ...drawioExportArgs(source, verification, 'png'),
  ], { stdio: 'pipe', timeout: 90000 });
  const inspectedHash = await fileHash(path.join(review, `${name}-pass-04.png`));
  const publishedHash = await fileHash(published);
  const reexportHash = await fileHash(verification);
  if (inspectedHash !== publishedHash || inspectedHash !== reexportHash) {
    throw new Error(`${name}: canonical re-export does not match inspected PNG exactly`);
  }
  const expectedStamp = await createDiagramStamp({ path: source, kind: 'drawio' }, generated, published, { rendererVersion: version });
  const actualStamp = JSON.parse(fs.readFileSync(path.join(repo, 'docs/diagrams', `${name}.hash.txt`), 'utf8'));
  if (JSON.stringify(expectedStamp) !== JSON.stringify(actualStamp)) throw new Error(`${name}: stamp mismatch`);
  const result = { diagram: name, renderer_version: version, inspected_png_sha256: inspectedHash, published_png_sha256: publishedHash, canonical_reexport_sha256: reexportHash, byte_identical: true, repository_stamp_exact: true };
  fs.writeFileSync(path.join(review, 'publication-verification.json'), JSON.stringify(result, null, 2) + '\n');
  reports.push(result);
  console.log(`${name}: inspected PNG = publication = canonical Desktop re-export (byte-identical)`);
}
const output = execFileSync(process.execPath, [
  path.join(repo, 'scripts/docs/render-diagrams.mjs'),
  '--check', ...models.flatMap(m => ['--spec', m.name]),
], { cwd: repo, encoding: 'utf8' });
fs.writeFileSync(path.join(here, 'selected-drift-check.txt'), output);
fs.writeFileSync(path.join(here, 'batch-publication-verification.json'), JSON.stringify({ diagrams: reports, selected_repository_drift_check: output.trim() }, null, 2) + '\n');
console.log(output.trim());
