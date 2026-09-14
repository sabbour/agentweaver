#!/usr/bin/env node

import { readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const diagramsRoot = path.join(repoRoot, 'docs', 'diagrams');
const config = JSON.parse(await readFile(path.join(diagramsRoot, 'flagship-diagrams.json'), 'utf8'));
const flagshipNames = new Set(config.diagrams.map(item => item.name));
const startMarker = '<!-- flagship-diagrams:start -->';
const endMarker = '<!-- flagship-diagrams:end -->';
const authoringDocs = new Set([
  path.join(repoRoot, 'docs', 'diagrams', 'README.md'),
  path.join(repoRoot, 'docs', 'guide', 'diagram-authoring.md'),
]);

async function markdownFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    if (entry.name === 'reviews') continue;
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...await markdownFiles(entryPath));
    else if (entry.name.endsWith('.md')) files.push(entryPath);
  }
  return files;
}

function canonicalAssetLink(filePath, name, extension) {
  if (extension === 'png') return relativeLink(filePath, path.join(diagramsRoot, 'flagship', `${name}.png`));
  if (extension === 'drawio') return relativeLink(filePath, path.join(diagramsRoot, 'drawio', 'generated', 'flagship', `${name}.drawio`));
  return relativeLink(filePath, path.join(diagramsRoot, 'src', 'flagship', `${name}.json`));
}

function stripLegacyDiagramReferences(markdown, filePath) {
  let output = markdown.replace(
    new RegExp(`${startMarker}[\\s\\S]*?${endMarker}\\s*`, 'g'),
    '',
  );
  output = output.replace(/^[ \t]*<!-- diagram-context:[^:\r\n]+:(?:start|end) -->[ \t]*\r?\n?/gm, '');
  output = output.replace(/<!--(?:(?!-->)[\s\S])*(?:docs\/diagrams\/|diagrams\/src\/|\.drawio)(?:(?!-->)[\s\S])*-->\s*/g, '');
  output = output.replace(/^[ \t]*!\[[^\n]*\]\([^\n)]*diagrams\/[^\n)]*\.png[^\n)]*\)[ \t]*\r?\n?/gm, '');
  output = output.replace(/^[ \t]*<img\b[^\n>]*\bdiagrams\/[^\n>]*>[ \t]*\r?\n?/gim, '');
  output = output.replace(
    /\[([^\]]+)\]\(([^)\s]*diagrams\/(?:src\/)?([^/)\s]+)\.(png|drawio|json))\)/g,
    (_match, label, _target, name, extension) => flagshipNames.has(name)
      ? `[${label}](${canonicalAssetLink(filePath, name, extension)})`
      : label,
  );
  output = output.replace(
    /(?:\.\.?\/|docs\/)*diagrams\/(?:src\/)?([^/\s)"']+)\.(png|drawio|json)/g,
    (_match, name, extension) => flagshipNames.has(name)
      ? canonicalAssetLink(filePath, name, extension)
      : name,
  );
  return output.replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
}

function relativeLink(fromFile, target) {
  return path.relative(path.dirname(fromFile), target).replaceAll('\\', '/');
}

async function flagshipBlock(docPath, diagrams) {
  const sections = [];
  for (const diagram of diagrams) {
    const source = JSON.parse(await readFile(
      path.join(diagramsRoot, 'src', 'flagship', `${diagram.name}.json`),
      'utf8',
    ));
    const png = relativeLink(docPath, path.join(diagramsRoot, 'flagship', `${diagram.name}.png`));
    const drawio = relativeLink(docPath, path.join(diagramsRoot, 'drawio', 'generated', 'flagship', `${diagram.name}.drawio`));
    const json = relativeLink(docPath, path.join(diagramsRoot, 'src', 'flagship', `${diagram.name}.json`));
    sections.push(
      `### ${diagram.concept}\n\n`
      + `[![${source.alt}](${png})](${drawio})\n\n`
      + `[Structured source](${json}) · [Editable draw.io](${drawio})`,
    );
  }
  return `${startMarker}\n## Visual model\n\n${sections.join('\n\n')}\n${endMarker}\n`;
}

function insertBlock(markdown, block, isReadme) {
  if (isReadme) {
    const anchor = 'Workflows turn probabilistic agent work into a governed path toward a defined outcome, with the gates and approvals you set. Start and supervise work in the Agentweaver interface or through MCP from an assistant, editor, or CLI.\n';
    const index = markdown.indexOf(anchor);
    if (index === -1) throw new Error('README architecture insertion anchor changed');
    return `${markdown.slice(0, index + anchor.length)}\n${block}\n${markdown.slice(index + anchor.length)}`;
  }
  const related = markdown.search(/\n## (?:See also|Related|Next steps)\b/i);
  if (related >= 0) return `${markdown.slice(0, related)}\n\n${block}${markdown.slice(related)}`;
  return `${markdown.trimEnd()}\n\n${block}`;
}

const files = [
  path.join(repoRoot, 'README.md'),
  ...await markdownFiles(path.join(repoRoot, 'docs')),
];
for (const file of files) {
  if (authoringDocs.has(file)) continue;
  const original = await readFile(file, 'utf8');
  const stripped = stripLegacyDiagramReferences(original, file);
  if (stripped !== original) await writeFile(file, stripped);
}

const byDoc = new Map();
for (const diagram of config.diagrams) {
  for (const doc of diagram.docs) {
    const list = byDoc.get(doc) ?? [];
    list.push(diagram);
    byDoc.set(doc, list);
  }
}
for (const [relativeDoc, diagrams] of byDoc) {
  const docPath = path.join(repoRoot, relativeDoc);
  const markdown = stripLegacyDiagramReferences(await readFile(docPath, 'utf8'), docPath);
  const block = await flagshipBlock(docPath, diagrams);
  await writeFile(docPath, insertBlock(markdown, block, relativeDoc === 'README.md'));
  console.log(`Updated ${relativeDoc}: ${diagrams.map(item => item.name).join(', ')}`);
}
