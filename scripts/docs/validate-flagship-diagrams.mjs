import { access, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

function pngDimensions(buffer) {
  if (buffer.length < 24 || buffer.toString('ascii', 1, 4) !== 'PNG') {
    throw new Error('not a PNG');
  }
  return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
}

function geometry(xml, id) {
  const cell = xml.match(new RegExp(`<mxCell[^>]*id="${id}"[\\s\\S]*?<mxGeometry[^>]*width="([^"]+)"[^>]*height="([^"]+)"`));
  return cell ? { width: Number(cell[1]), height: Number(cell[2]) } : null;
}

function countRole(xml, role) {
  return [...xml.matchAll(new RegExp(`fluentRole="${role}"`, 'g'))].length;
}

export function inspectFlagship(name, xml, png, embedWidthPx = 960) {
  const issues = [];
  const dimensions = pngDimensions(png);
  const aspectRatio = dimensions.width / dimensions.height;
  const canvas = geometry(xml, 'fluent-paper');
  if (!canvas) issues.push('missing Fluent canvas geometry');
  if (aspectRatio < 0.72 || aspectRatio > 3) {
    issues.push(`extreme aspect ratio ${aspectRatio.toFixed(2)}; use 0.72-3.00`);
  }
  const cards = countRole(xml, 'card');
  const connectors = countRole(xml, 'connector') + countRole(xml, 'message');
  if (cards > 16) issues.push(`${cards} cards; flagship limit is 16`);
  if (connectors > 24) issues.push(`${connectors} connectors/messages; flagship limit is 24`);
  if (canvas) {
    const effectiveTitle = 20 * Math.min(1, embedWidthPx / canvas.width);
    const effectiveSubtitle = 16 * Math.min(1, embedWidthPx / canvas.width);
    if (effectiveTitle < 11.5) issues.push(`title type scales to ${effectiveTitle.toFixed(1)}px at ${embedWidthPx}px embed width`);
    if (effectiveSubtitle < 9.2) issues.push(`subtitle type scales to ${effectiveSubtitle.toFixed(1)}px at ${embedWidthPx}px embed width`);
  }
  return { name, ...dimensions, aspectRatio, cards, connectors, issues };
}

async function exists(file) {
  try { await access(file); return true; } catch { return false; }
}

async function main() {
  const configPath = path.join(repoRoot, 'docs', 'diagrams', 'flagship-diagrams.json');
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  const results = [];
  for (const diagram of config.diagrams) {
    const source = path.join(repoRoot, 'docs', 'diagrams', 'src', 'flagship', `${diagram.name}.drawio`);
    const generated = path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'generated', 'flagship', `${diagram.name}.drawio`);
    const xmlPath = await exists(generated) ? generated : source;
    const pngPath = path.join(repoRoot, 'docs', 'diagrams', 'flagship', `${diagram.name}.png`);
    const hashPath = path.join(repoRoot, 'docs', 'diagrams', 'flagship', `${diagram.name}.hash.txt`);
    const missing = [];
    for (const [label, file] of [['editable source', xmlPath], ['PNG', pngPath], ['hash', hashPath]]) {
      if (!await exists(file)) missing.push(label);
    }
    if (missing.length) {
      results.push({ name: diagram.name, issues: [`missing ${missing.join(', ')}`] });
      continue;
    }
    results.push(inspectFlagship(
      diagram.name,
      await readFile(xmlPath, 'utf8'),
      await readFile(pngPath),
      config.embedWidthPx,
    ));
  }
  const failed = results.filter(result => result.issues.length);
  for (const result of results) {
    console.log(`${result.issues.length ? 'FAIL' : 'PASS'} ${result.name}${result.issues.length ? `: ${result.issues.join('; ')}` : ''}`);
  }
  if (failed.length) throw new Error(`${failed.length} flagship diagram(s) failed visual validation`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
