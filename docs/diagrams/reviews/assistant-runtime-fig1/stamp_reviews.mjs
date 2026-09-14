import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  createDiagramStamp,
  resolveDrawioCommand,
  verifyDrawioVersion,
} from '../../../../scripts/docs/diagram-sources.mjs';
import { checkSourceArtifacts } from '../../../../scripts/docs/capture-diagrams.mjs';

const reviews = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const renderer = String.raw`C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe`;
const rendererVersion = verifyDrawioVersion(resolveDrawioCommand({ explicitPath: renderer }));
const names = [
  'assistant-runtime-fig1', 'auth-security-fig1', 'auth-security-fig4',
  'canonical-aks-components', 'canonical-sandbox-boundary', 'canonical-sandbox-experience',
  'sandbox-browser-preview-fig1', 'canonical-memory-context', 'canonical-provider-admission',
];
for (const name of names) {
  const folder = path.join(reviews, name);
  const source = { name: `${name}-pass-04`, kind: 'drawio', path: path.join(folder, `${name}-pass-04.drawio`) };
  const png = path.join(folder, `${source.name}.png`);
  const stamp = await createDiagramStamp(source, source.path, png, { rendererVersion });
  await writeFile(path.join(folder, `${source.name}.hash.txt`), `${JSON.stringify(stamp, null, 2)}\n`);
  const result = await checkSourceArtifacts(source, {
    outputDirectory: folder,
    generatedDirectory: path.join(folder, 'generated-unused'),
  });
  if (!result.ok) throw new Error(result.message);
  const validationPath = path.join(folder, 'validation-results.json');
  const validation = JSON.parse(await readFile(validationPath, 'utf8'));
  validation.review_local_repository_stamp_and_drift = { ...result, rendererVersion };
  validation.canonical_drift_check = 'Not run: no canonical source or published raster was promoted.';
  await writeFile(validationPath, `${JSON.stringify(validation, null, 2)}\n`);
  console.log(result.message);
}
