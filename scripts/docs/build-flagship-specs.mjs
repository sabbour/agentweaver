import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { jsonFileToDrawio } from './drawio-generator.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const sourceDir = path.join(repoRoot, 'docs', 'diagrams', 'src', 'flagship');
const outputDir = path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'generated', 'flagship');

const names = (await readdir(sourceDir))
  .filter((name) => name.endsWith('.json'))
  .sort();

if (names.length === 0) {
  throw new Error(`No diagram JSON sources found in ${sourceDir}`);
}

await mkdir(outputDir, { recursive: true });
for (const fileName of names) {
  const name = path.basename(fileName, '.json');
  const sourcePath = path.join(sourceDir, fileName);
  const source = JSON.parse(await readFile(sourcePath, 'utf8'));
  const xml = await jsonFileToDrawio(sourcePath, { name });
  const outputPath = path.join(outputDir, `${name}.drawio`);
  await writeFile(outputPath, xml);
  console.log(`Generated ${path.relative(repoRoot, outputPath)} (${source.title})`);
}
