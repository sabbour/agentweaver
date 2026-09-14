#!/usr/bin/env node
// Browser-free draw.io generation and export pipeline. JSON graph/sequence
// inputs are converted to editable, uncompressed draw.io XML before the
// official draw.io Desktop CLI exports documentation assets.

import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import {
  diagramSourceHash,
  createDiagramStamp,
  drawioExportArgs,
  fileHash,
  generatedDrawioPath,
  listDiagramSources,
  parseDiagramStamp,
  resolveDrawioCommand,
  selectDiagramSources,
  validateUncompressedDrawio,
  verifyDrawioVersion,
} from './diagram-sources.mjs';
import { DRAWIO_CLI_VERSION, jsonFileToDrawio } from './drawio-generator.mjs';
import { requireFluentSource } from './fluent-validation.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const specsDir = path.join(repoRoot, 'docs', 'diagrams', 'src');
const outDir = path.join(repoRoot, 'docs', 'diagrams');
const generatedDir = path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'generated');

async function materializeDrawio(source) {
  if (source.kind === 'drawio') {
    const raw = await readFile(source.path, 'utf8');
    validateUncompressedDrawio(raw, source.path);
    requireFluentSource(raw, source.name);
    return source.path;
  }

  const outputPath = generatedDrawioPath(generatedDir, source.name);
  const generated = await jsonFileToDrawio(source.path, { name: source.name });
  validateUncompressedDrawio(generated, outputPath);
  requireFluentSource(generated, source.name);
  await mkdir(generatedDir, { recursive: true });
  await writeFile(outputPath, generated);
  console.log(`Generated ${path.relative(repoRoot, outputPath)}`);
  return outputPath;
}

function exportDrawio(drawioPath, sourceName, formats, commandInfo, { embed = true, execute = execFileSync } = {}) {
  for (const format of formats) {
    const outputPath = path.join(outDir, `${sourceName}.${format}`);
    const args = [
      ...commandInfo.prefixArgs,
      ...drawioExportArgs(drawioPath, outputPath, format, { embed }),
    ];
    try {
      execute(commandInfo.command, args, { cwd: repoRoot, stdio: 'inherit', shell: false });
    } catch (error) {
      if (error?.code === 'ENOENT') {
        throw new Error('draw.io Desktop CLI was not found. Install the pinned version or pass --drawio-cli <path>.');
      }
      throw error;
    }
    console.log(`Rendered ${sourceName}.${format}`);
  }
}

export async function render(
  requestedNames,
  {
    drawioFormats = ['png'],
    drawioCli,
    embed = true,
    allowVersionMismatch = false,
    execute = execFileSync,
  } = {},
) {
  const sources = selectDiagramSources(await listDiagramSources(specsDir), requestedNames);
  const commandInfo = resolveDrawioCommand({ explicitPath: drawioCli });
  const rendererVersion = verifyDrawioVersion(
    commandInfo,
    { execute, allowMismatch: allowVersionMismatch },
  );
  if (rendererVersion !== DRAWIO_CLI_VERSION) {
    throw new Error(
      `draw.io Desktop CLI ${rendererVersion} cannot write canonical diagram outputs or stamps; `
      + `use the pinned ${DRAWIO_CLI_VERSION} renderer. --allow-version-mismatch is for version probes only.`,
    );
  }

  // Validate the entire selected batch before materializing or replacing any output.
  const blocked=[];
  for(const source of sources) {
    try {
      const raw=source.kind==='json'
        ? await jsonFileToDrawio(source.path,{name:source.name})
        : await readFile(source.path,'utf8');
      requireFluentSource(raw,source.name);
    } catch(error) {
      blocked.push(error.message);
    }
  }
  if(blocked.length) throw new Error(`Batch publication blocked before any writes:\n${blocked.join('\n')}`);

  for (const source of sources) {
    const drawioPath = await materializeDrawio(source);
    exportDrawio(drawioPath, source.name, drawioFormats, commandInfo, { embed, execute });
    const pngPath = path.join(outDir, `${source.name}.png`);
    const stamp = await createDiagramStamp(
      source,
      drawioPath,
      pngPath,
      { rendererVersion },
    );
    await writeFile(path.join(outDir, `${source.name}.hash.txt`), `${JSON.stringify(stamp, null, 2)}\n`);
    console.log(`Wrote ${source.name}.hash.txt`);
  }
}

export async function check(requestedNames) {
  const sources = selectDiagramSources(await listDiagramSources(specsDir), requestedNames);
  let drift = false;
  for (const source of sources) {
    const result = await checkSourceArtifacts(source, { outputDirectory: outDir, generatedDirectory: generatedDir });
    if (!result.ok) {
      console.error(result.message);
      drift = true;
    } else console.log(result.message);
  }
  return !drift;
}

export async function checkSourceArtifacts(
  source,
  { outputDirectory, generatedDirectory },
) {
  const hashFile = path.join(outputDirectory, `${source.name}.hash.txt`);
  const pngFile = path.join(outputDirectory, `${source.name}.png`);
  if (!existsSync(hashFile) || !existsSync(pngFile)) {
    return {
      ok: false,
      message: `Missing rendered output for ${source.name}: run "npm run docs:render-diagrams" and commit the result.`,
    };
  }
  let committed;
  try {
    committed = parseDiagramStamp(await readFile(hashFile, 'utf8'), hashFile);
  } catch (error) {
    return {
      ok: false,
      message: error.message,
    };
  }
  const sourceHash = await diagramSourceHash(source);
  if (committed.source.sha256 !== sourceHash) {
    return {
      ok: false,
      message: `Diagram drift detected: ${path.basename(source.path)} changed since ${source.name}.png was last rendered. Run "npm run docs:render-diagrams" and commit the result.`,
    };
  }
  let drawioPath = source.path;
  if (source.kind === 'json') {
    drawioPath = generatedDrawioPath(generatedDirectory, source.name);
    const expectedXml = await jsonFileToDrawio(source.path, { name: source.name });
    if (!existsSync(drawioPath) || (await readFile(drawioPath, 'utf8')) !== expectedXml) {
      return {
        ok: false,
        message: `Editable draw.io source is stale for ${source.name}: run "npm run docs:render-diagrams" and commit ${drawioPath}.`,
      };
    }
  }
  if (committed.drawio.sha256 !== await fileHash(drawioPath)) {
    return { ok: false, message: `Draw.io XML drift detected for ${source.name}; re-export the diagram.` };
  }
  if (committed.png.sha256 !== await fileHash(pngFile)) {
    return { ok: false, message: `PNG drift detected for ${source.name}; re-export the diagram with draw.io Desktop.` };
  }
  return {
    ok: true,
    message: `OK: ${source.name}.png and editable draw.io source are in sync with ${path.basename(source.path)}`,
  };
}
