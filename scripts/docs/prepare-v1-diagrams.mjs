import { copyFile, mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const diagramsRoot = path.join(repoRoot, 'docs', 'diagrams');
const publicDir = path.join(repoRoot, 'docs', 'public', 'diagrams', 'flagship');
const manifest = JSON.parse(await readFile(path.join(diagramsRoot, 'flagship-diagrams.json'), 'utf8'));

await mkdir(publicDir, { recursive: true });
for (const { name } of manifest.diagrams) {
  for (const extension of ['png', 'drawio']) {
    const source = extension === 'png'
      ? path.join(diagramsRoot, 'flagship', `${name}.png`)
      : path.join(diagramsRoot, 'drawio', 'generated', 'flagship', `${name}.drawio`);
    const destination = path.join(publicDir, `${name}.${extension}`);
    await copyFile(source, destination);
    console.log(`Prepared ${path.relative(repoRoot, destination)}`);
  }
}
