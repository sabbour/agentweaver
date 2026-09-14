import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { DRAWIO_CLI_VERSION } from './drawio-generator.mjs';

export const DRAWIO_FORMATS = new Set(['png', 'svg', 'pdf']);
export const DIAGRAM_STAMP_VERSION = 2;
export const PNG_EXPORT_RECIPE = Object.freeze({
  renderer: 'draw.io Desktop',
  rendererVersion: DRAWIO_CLI_VERSION,
  format: 'png',
  border: 16,
  scale: 2,
});

function canonicalize(value) {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === 'object') {
    return Object.keys(value)
      .sort()
      .reduce((result, key) => {
        result[key] = canonicalize(value[key]);
        return result;
      }, {});
  }
  return value;
}

export async function listDiagramSources(specsDir) {
  async function filesUnder(directory, relativeDirectory = '') {
    const entries = await readdir(directory, { withFileTypes: true });
    const files = [];
    for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
      const relativePath = path.join(relativeDirectory, entry.name);
      if (entry.isDirectory()) files.push(...await filesUnder(path.join(directory, entry.name), relativePath));
      else files.push(relativePath);
    }
    return files;
  }
  const entries = await filesUnder(specsDir);
  const sources = new Map();
  for (const relativePath of entries) {
    const fileName = path.basename(relativePath);
    const extension = path.extname(fileName).toLowerCase();
    if (!['.json', '.drawio'].includes(extension) || fileName.endsWith('-spec.schema.json')) continue;
    const name = fileName.slice(0, -extension.length);
    if (sources.has(name)) throw new Error(`Diagram source name is ambiguous: ${name} has both JSON and draw.io sources`);
    sources.set(name, {
      name,
      kind: extension === '.drawio' ? 'drawio' : 'json',
      path: path.join(specsDir, relativePath),
      relativeDirectory: path.dirname(relativePath) === '.' ? '' : path.dirname(relativePath),
    });
  }
  return [...sources.values()];
}

export function selectDiagramSources(sources, requestedNames = []) {
  if (!requestedNames.length) return [...sources];
  const byName = new Map(sources.map((source) => [source.name, source]));
  const names = [...new Set(requestedNames)].sort();
  const missing = names.filter((name) => !byName.has(name));
  if (missing.length) throw new Error(`Diagram spec not found: ${missing.join(', ')}`);
  return names.map((name) => byName.get(name));
}

export async function diagramSourceHash(source) {
  const raw = await readFile(source.path, 'utf8');
  const canonical = source.kind === 'json'
    ? JSON.stringify(canonicalize(JSON.parse(raw)))
    : raw.replace(/\r\n/g, '\n').trim();
  return createHash('sha256').update(canonical).digest('hex');
}

export async function fileHash(filePath) {
  return createHash('sha256').update(await readFile(filePath)).digest('hex');
}

export async function createDiagramStamp(
  source,
  drawioPath,
  pngPath,
  { rendererVersion } = {},
) {
  if (!rendererVersion) {
    throw new Error('The detected draw.io Desktop version is required when creating a diagram stamp');
  }
  return {
    version: DIAGRAM_STAMP_VERSION,
    source: {
      file: path.basename(source.path),
      sha256: await diagramSourceHash(source),
    },
    drawio: {
      file: path.basename(drawioPath),
      sha256: await fileHash(drawioPath),
    },
    renderer: {
      ...PNG_EXPORT_RECIPE,
      rendererVersion,
    },
    png: {
      file: path.basename(pngPath),
      sha256: await fileHash(pngPath),
    },
  };
}

export function parseDiagramStamp(contents, stampPath = 'diagram stamp') {
  let stamp;
  try {
    stamp = JSON.parse(contents);
  } catch {
    throw new Error(`${stampPath} uses the obsolete source-only hash format; re-export with draw.io Desktop`);
  }
  if (
    stamp?.version !== DIAGRAM_STAMP_VERSION
    || !stamp.source?.sha256
    || !stamp.drawio?.sha256
    || !stamp.png?.sha256
    || stamp.renderer?.renderer !== PNG_EXPORT_RECIPE.renderer
    || stamp.renderer?.rendererVersion !== PNG_EXPORT_RECIPE.rendererVersion
    || stamp.renderer?.format !== 'png'
    || stamp.renderer?.border !== 16
    || stamp.renderer?.scale !== 2
  ) {
    throw new Error(`${stampPath} does not match the current draw.io rendering recipe`);
  }
  return stamp;
}

export function generatedDrawioPath(generatedDir, name, relativeDirectory = '') {
  return path.join(generatedDir, relativeDirectory, `${name}.drawio`);
}

export function validateUncompressedDrawio(contents, source = 'draw.io source') {
  if (!contents.includes('<mxfile') || !contents.includes('<mxGraphModel')) {
    throw new Error(`${source} is not editable draw.io XML`);
  }
  if (/compressed\s*=\s*["']true["']/.test(contents)) {
    throw new Error(`${source} must use uncompressed draw.io XML`);
  }
  return true;
}

export function drawioExportArgs(sourcePath, outputPath, format, { embed = true } = {}) {
  if (!DRAWIO_FORMATS.has(format)) throw new Error(`Unsupported draw.io format: ${format}`);
  const args = ['--export', '--format', format, '--border', '16'];
  if (format === 'png') args.push('--scale', '2');
  if (embed && (format === 'svg' || format === 'pdf')) args.push('--embed-diagram');
  args.push('--output', outputPath, sourcePath);
  return args;
}

export function resolveDrawioCommand({ explicitPath, env = process.env, platform = process.platform } = {}) {
  const configured = explicitPath || env.DRAWIO_CLI;
  if (configured) return { command: configured, prefixArgs: [] };
  if (platform === 'win32') {
    const candidates = [
      path.join(env.ProgramFiles || '', 'draw.io', 'draw.io.exe'),
      path.join(env['ProgramFiles(x86)'] || '', 'draw.io', 'draw.io.exe'),
      path.join(env.LOCALAPPDATA || '', 'Programs', 'draw.io', 'draw.io.exe'),
    ].filter((candidate) => candidate && existsSync(candidate));
    return { command: candidates[0] ?? 'draw.io', prefixArgs: [] };
  }
  if (platform === 'linux' && !env.DISPLAY) {
    return { command: 'xvfb-run', prefixArgs: ['-a', 'drawio', '--no-sandbox'] };
  }
  return { command: 'drawio', prefixArgs: [] };
}

export function verifyDrawioVersion(
  { command, prefixArgs = [] },
  { execute, expected = DRAWIO_CLI_VERSION, allowMismatch = false, platform = process.platform } = {},
) {
  const run = execute ?? execFileSync;
  let output;
  try {
    output = run(command, [...prefixArgs, '--version'], { encoding: 'utf8', shell: false });
  } catch (error) {
    if (error?.code === 'ENOENT') throw new Error('draw.io Desktop CLI was not found. Install it or pass --drawio-cli <path>.');
    throw error;
  }
  let match = String(output).match(/\d+\.\d+\.\d+/);
  if (!match && platform === 'win32' && path.extname(command).toLowerCase() === '.exe') {
    const escaped = command.replaceAll("'", "''");
    output = run(
      'powershell.exe',
      ['-NoProfile', '-Command', `(Get-Item -LiteralPath '${escaped}').VersionInfo.ProductVersion`],
      { encoding: 'utf8', shell: false },
    );
    match = String(output).match(/\d+\.\d+\.\d+/);
  }
  if (!match) throw new Error(`Could not determine draw.io Desktop CLI version from: ${String(output).trim()}`);
  if (match[0] !== expected && !allowMismatch) {
    throw new Error(`draw.io Desktop CLI ${expected} is required for deterministic exports; found ${match[0]}. Pass --allow-version-mismatch only for local inspection.`);
  }
  return match[0];
}
