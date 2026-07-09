import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..', '..', '..', '..', '..');
const libraryRoot = path.join(repoRoot, 'apps', 'web', 'src', 'azure-fluent-system');
const catalogRoot = path.join(libraryRoot, 'catalog');

const design = fs.readFileSync(path.join(repoRoot, 'DESIGN.md'), 'utf8');
const portableDesign = fs.readFileSync(path.join(libraryRoot, 'DESIGN.md'), 'utf8');
const libraryReadme = fs.readFileSync(path.join(libraryRoot, 'README.md'), 'utf8');
const showcaseReadme = fs.readFileSync(path.join(__dirname, 'README.md'), 'utf8');
const showcaseApp = fs.readFileSync(path.join(__dirname, 'AzureFluentShowcaseApp.tsx'), 'utf8');
const skillDoc = fs.readFileSync(path.join(repoRoot, '.copilot', 'skills', 'azure-fluent-system-sync', 'SKILL.md'), 'utf8');
const skillEvals = fs.readFileSync(path.join(repoRoot, '.copilot', 'skills', 'azure-fluent-system-sync', 'evals', 'evals.json'), 'utf8');
const componentsDoc = fs.readFileSync(path.join(catalogRoot, 'COMPONENTS.md'), 'utf8');
const patternsDoc = fs.readFileSync(path.join(catalogRoot, 'PATTERNS.md'), 'utf8');
const iconsDoc = fs.readFileSync(path.join(catalogRoot, 'ICONS.md'), 'utf8');

function getSection(text, startHeading, endHeading) {
  const start = text.indexOf(startHeading);
  if (start === -1) return '';
  const from = text.slice(start + startHeading.length);
  if (!endHeading) return from;
  const end = from.indexOf(endHeading);
  return end === -1 ? from : from.slice(0, end);
}

function tableRows(section) {
  return section
    .split(/\r?\n/)
    .filter((line) => line.startsWith('| '))
    .filter((line) => !/^\|\s*-/.test(line))
    .map((line) => line.trim())
    .slice(1);
}

function summaryValue(text, label) {
  const escaped = label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = text.match(new RegExp(`\\| ${escaped} \\| ([^|]+) \\|`));
  return match?.[1]?.trim();
}

function catalogReferences(text) {
  return Array.from(text.matchAll(/catalog\/([A-Z0-9-]+\.md)/g)).map((match) => match[1]);
}

const failures = [];
const catalogFiles = fs.readdirSync(catalogRoot).filter((entry) => fs.statSync(path.join(catalogRoot, entry)).isFile());
const expectedCatalogFiles = ['COMPONENTS.md', 'PATTERNS.md', 'ICONS.md'];
if (catalogFiles.length !== expectedCatalogFiles.length || expectedCatalogFiles.some((file) => !catalogFiles.includes(file))) {
  failures.push(`catalog directory must contain exactly ${expectedCatalogFiles.join(', ')}; found ${catalogFiles.join(', ')}`);
}

function stripFoundationsSection(text) {
  // The "Fluent 2 foundation components" section is an explicitly-requested
  // discoverability surface that legitimately references the upstream
  // @fluentui/react-components package, the Fluent 2 React docs, and a single
  // usage import example. Exempt it from the prose-only / no-code-like checks
  // while keeping those checks strict for the coverage-inventory content.
  const start = text.indexOf('## Fluent 2 foundation components');
  if (start === -1) return text;
  const after = text.slice(start + '## Fluent 2 foundation components'.length);
  const end = after.indexOf('\n## ');
  return text.slice(0, start) + (end === -1 ? '' : after.slice(end));
}

for (const [label, rawText] of [['COMPONENTS.md', componentsDoc], ['PATTERNS.md', patternsDoc], ['ICONS.md', iconsDoc]]) {
  const text = label === 'COMPONENTS.md' ? stripFoundationsSection(rawText) : rawText;
  if (text.includes('```')) failures.push(`${label} must not contain fenced code blocks`);
  if (text.includes('```json')) failures.push(`${label} must not contain fenced json blocks`);
  if (text.includes('catalog-data:start')) failures.push(`${label} must not contain embedded catalog-data markers`);
  for (const forbidden of [/\bevidence\b/i, /\bpriority\b/i]) {
    if (forbidden.test(text)) failures.push(`${label} contains forbidden term matching ${forbidden}`);
  }
  for (const token of [/\bjsx\b/i, /\bjavascript\b/i, /\btypescript\b/i, /\breact\b/i, /\bfunction\b/, /\bconst\b/, /=>/, /\{[^}\n]{30,}\}/]) {
    if (token.test(text)) failures.push(`${label} contains code-like content matching ${token}`);
  }
}

for (const [label, text] of [
  ['azure-fluent-system/README.md', libraryReadme],
  ['showcase/README.md', showcaseReadme],
  ['azure-fluent-system/DESIGN.md', portableDesign],
  ['skill doc', skillDoc],
  ['skill evals', skillEvals],
]) {
  const invalidCatalogRefs = catalogReferences(text).filter((entry) => !expectedCatalogFiles.includes(entry));
  if (invalidCatalogRefs.length > 0) failures.push(`${label} references removed catalog names: ${invalidCatalogRefs.join(', ')}`);
}

for (const marker of [
  '### Pattern doctrine',
  'Coverage is not fidelity.',
  '#### Recipe mapping and fidelity gate',
  'Screenshot or manual browser inspection is required before PASS.',
]) {
  if (!design.includes(marker)) failures.push(`DESIGN.md missing marker: ${marker}`);
}

for (const marker of [
  '# Azure Fluent System design addendum',
  'catalog/COMPONENTS.md',
  'catalog/PATTERNS.md',
  'catalog/ICONS.md',
  'Do not require Figma MCP for ordinary implementation or review.',
]) {
  if (!portableDesign.includes(marker)) failures.push(`azure-fluent-system/DESIGN.md missing marker: ${marker}`);
}

for (const marker of [
  'catalog/COMPONENTS.md',
  'catalog/PATTERNS.md',
  'catalog/ICONS.md',
  'Local-first downstream workflow',
  'Downstream agents should be able to consume this library without Figma MCP.',
]) {
  if (!libraryReadme.includes(marker)) failures.push(`azure-fluent-system/README.md missing marker: ${marker}`);
}

for (const marker of [
  'exactly two primary experiences',
  'three checked-in catalog files',
  'inline icon catalog surface',
  'Local-first workflow',
  'Use Figma MCP only if it is available and you are intentionally refreshing the catalog.',
  'catalog/COMPONENTS.md',
  'catalog/PATTERNS.md',
  'catalog/ICONS.md',
]) {
  if (!showcaseReadme.includes(marker)) failures.push(`showcase/README.md missing marker: ${marker}`);
}

for (const marker of [
  'Exactly two primary experiences: a component preview and a pattern example browser',
  'Built from `catalog/COMPONENTS.md`',
  'Built from `catalog/ICONS.md`',
  'Built from `catalog/PATTERNS.md`',
  'Local source mappings',
  'Traceability citations',
]) {
  if (!showcaseApp.includes(marker)) failures.push(`AzureFluentShowcaseApp.tsx missing marker: ${marker}`);
}

for (const marker of [
  'catalog/ICONS.md',
  'minimal visible icon surface',
  'component or icon visibility in the showcase',
]) {
  if (!skillDoc.includes(marker)) failures.push(`skill doc missing marker: ${marker}`);
}

for (const marker of [
  'catalog/ICONS.md',
  'minimal showcase icon surface',
  'showcase icon visibility aligned with catalog/ICONS.md',
]) {
  if (!skillEvals.includes(marker)) failures.push(`skill evals missing marker: ${marker}`);
}

for (const marker of ['## Component inventory', '## Local-first workflow']) {
  if (!componentsDoc.includes(marker)) failures.push(`COMPONENTS.md missing marker: ${marker}`);
}
for (const marker of ['## Pattern inventory table', '## Source-reference rules', '## Shared token anchors', '## Worked example: `3203:24770` (`Isolated` → `First step`)']) {
  if (!patternsDoc.includes(marker)) failures.push(`PATTERNS.md missing marker: ${marker}`);
}
for (const marker of ['## Tracked local icon aliases', '## Vendored Azure icon collection coverage', '## Local-first workflow']) {
  if (!iconsDoc.includes(marker)) failures.push(`ICONS.md missing marker: ${marker}`);
}

const componentInventoryRows = tableRows(getSection(componentsDoc, '## Component inventory', '## Local-first workflow'));
const patternRows = tableRows(getSection(patternsDoc, '## Pattern inventory table', '## Source-reference rules'));
const iconAliasRows = tableRows(getSection(iconsDoc, '## Tracked local icon aliases', '## Vendored Azure icon collection coverage'));
const iconCollectionRows = tableRows(getSection(iconsDoc, '## Vendored Azure icon collection coverage', '## Local-first workflow'));

if (componentInventoryRows.length !== 148) failures.push(`COMPONENTS.md inventory row count must be 148; got ${componentInventoryRows.length}`);
if (patternRows.length !== 8) failures.push(`PATTERNS.md row count must be 8; got ${patternRows.length}`);
if (iconAliasRows.length !== 5) failures.push(`ICONS.md tracked alias row count must be 5; got ${iconAliasRows.length}`);
if (iconCollectionRows.length !== 27) failures.push(`ICONS.md collection row count must be 27; got ${iconCollectionRows.length}`);

if (summaryValue(componentsDoc, 'Inventory components/components sets') !== '148') failures.push('COMPONENTS.md summary count mismatch for inventory total');
if (summaryValue(componentsDoc, 'implemented-rendered') !== '26') failures.push('COMPONENTS.md summary count mismatch for implemented-rendered');
if (summaryValue(componentsDoc, 'needs-mcp-extraction') !== '45') failures.push('COMPONENTS.md summary count mismatch for needs-mcp-extraction');
if (summaryValue(componentsDoc, 'showcase-placeholder') !== '77') failures.push('COMPONENTS.md summary count mismatch for showcase-placeholder');
if (summaryValue(componentsDoc, 'needs-implementation') !== '0') failures.push('COMPONENTS.md summary count mismatch for needs-implementation');
if (summaryValue(componentsDoc, 'local-only-needed') !== '0') failures.push('COMPONENTS.md summary count mismatch for local-only-needed');
if (summaryValue(componentsDoc, 'not-in-inventory') !== '0') failures.push('COMPONENTS.md summary count mismatch for not-in-inventory');
if (summaryValue(patternsDoc, 'Pattern families') !== '8') failures.push('PATTERNS.md summary count mismatch for family total');
if (summaryValue(patternsDoc, 'Unique tracked dev-mode nodes') !== '25') failures.push('PATTERNS.md summary count mismatch for tracked nodes');
if (summaryValue(iconsDoc, 'Vendored Azure icon collections') !== '27') failures.push('ICONS.md summary count mismatch for collections');
if (summaryValue(iconsDoc, 'Raw visible icon exports') !== '1637') failures.push('ICONS.md summary count mismatch for raw exports');
if (summaryValue(iconsDoc, 'Unique checked-in SVG assets') !== '1441') failures.push('ICONS.md summary count mismatch for unique assets');
if (summaryValue(iconsDoc, 'Duplicate alias payloads') !== '196') failures.push('ICONS.md summary count mismatch for duplicate payloads');
if (!componentsDoc.includes('example path / status') || !componentsDoc.includes('| notes |')) failures.push('COMPONENTS.md missing example/notes columns');

if (failures.length > 0) {
  console.error('Pattern doctrine validation failed:');
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exit(1);
}

console.log('Pattern doctrine validation passed.');
