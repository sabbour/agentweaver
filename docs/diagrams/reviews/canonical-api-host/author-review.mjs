import fs from 'node:fs/promises';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { drawioExportArgs, verifyDrawioVersion } from '../../../../scripts/docs/diagram-sources.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../../..');
const template = await fs.readFile(path.join(repo, 'docs/diagrams/drawio/fluent-template.drawio'), 'utf8');
const library = await fs.readFile(path.join(repo, 'docs/diagrams/drawio/fluent-library.xml'), 'utf8');
if (!template.includes('pageWidth="1112"') || !library.includes('Agentweaver Fluent card')) {
  throw new Error('Reviewed Fluent template/library changed; inspect before authoring.');
}
const cli = path.join(here, 'renderer/desktop/draw.io.exe');
const command = { command: cli, prefixArgs: [] };
const version = verifyDrawioVersion(command);
if (version !== '31.4.5') throw new Error('Pinned renderer required.');
const model = JSON.parse(await fs.readFile(path.join(here, 'content-model.json'), 'utf8'));
const mode = process.argv[2];
if (!['pitch', 'pass-01'].includes(mode)) throw new Error('Choose pitch or pass-01.');
const xml = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const palette = [
  ['#d2ccf8', '#3f3682'], ['#a6e9ed', '#00666d'],
  ['#9fd89f', '#0e700e'], ['#f9e2ae', '#835b00'],
];
const cells = [];
function vertex(id, value, style, x, y, width, height, parent = '1') {
  cells.push(`<mxCell id="${id}" value="${xml(value)}" style="${style}" vertex="1" parent="${parent}"><mxGeometry x="${x}" y="${y}" width="${width}" height="${height}" as="geometry"/></mxCell>`);
}
function text(id, value, x, y, w, h, size, color = '#272320', bold = false, parent = '1') {
  vertex(id, value, `text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Segoe UI;fontSize=${size};fontColor=${color};fontStyle=${bold ? 1 : 0};align=left;verticalAlign=middle;spacing=0;`, x, y, w, h, parent);
}
function edge(id, from, to, label, points = [], anchors = '', returning = false) {
  cells.push(`<mxCell id="${id}" value="${xml(label)}" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=block;endFill=1;strokeColor=${returning ? '#d39300' : '#746d68'};strokeWidth=2;fontFamily=Segoe UI;fontSize=14;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;labelBorderColor=none;orthogonalLoop=1;jettySize=auto;jumpStyle=arc;jumpSize=8;${returning ? 'dashed=1;dashPattern=8 6;' : ''}${anchors}" edge="1" parent="1" source="${from}" target="${to}"><mxGeometry relative="1" as="geometry">${points.length ? `<Array as="points">${points.map(([x,y]) => `<mxPoint x="${x}" y="${y}"/>`).join('')}</Array>` : ''}</mxGeometry></mxCell>`);
}
const detailed = mode === 'pass-01';
text('title', model.title, 40, 20, 1032, 42, 28, '#272320', true);
if (!detailed) {
  // Pitch isolates the central authority contract; the upgrade expands its implementation.
  for (const [i, n] of model.pitch.entries()) {
    vertex(`pitch-${i}`, `<b>${n.title}</b><br>${n.description}<br><span style="font-family:Consolas;font-size:14px;color:#746d68">${i ? 'Program.cs:1274-1295' : 'HTTP credential boundary'}</span>`,
      'rounded=1;arcSize=16;html=1;whiteSpace=wrap;fillColor=#fdfbf8;strokeColor=#e2ddd9;shadow=1;fontFamily=Segoe UI;fontSize=24;fontColor=#272320;spacingLeft=60;spacingRight=12;align=left;',
      70 + i * 550, 270, 400, 170);
    vertex(`pitch-${i}-accent`, '', 'rounded=1;arcSize=100;fillColor=#00666d;strokeColor=none;', 0, 0, 5, 170, `pitch-${i}`);
    vertex(`pitch-${i}-icon`, '', `shape=${i ? 'process' : 'umlActor'};fillColor=#a6e9ed;strokeColor=#00666d;strokeWidth=1.5;`, 17, 62, 32, 40, `pitch-${i}`);
    vertex(`pitch-${i}-badge`, i ? 'AUTHORITY' : 'INTENT', 'rounded=1;arcSize=100;fillColor=#a6e9ed;strokeColor=none;fontFamily=Segoe UI;fontSize=12;fontColor=#00666d;', 275, 12, 110, 25, `pitch-${i}`);
  }
  edge('pitch-contract', 'pitch-0', 'pitch-1', model.pitch_relationship);
} else {
  text('takeaway', model.takeaway, 40, 64, 1032, 30, 17, '#635c57');
  vertex('startup-surface', '', 'rounded=1;arcSize=16;fillColor=#efeae7;strokeColor=#e2ddd9;strokeWidth=1;', 28, 100, 1056, 155);
  vertex('request-surface', '', 'rounded=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=1;', 28, 270, 1056, 480);
  text('startup-group-title', 'BOOTSTRAP', 507, 111, 110, 18, 12, '#635c57', true);
  text('request-group-title', 'PER REQUEST', 507, 279, 110, 18, 12, '#635c57', true);
  vertex('classification-guard', 'Unclassified<br>endpoint?<br>Fail closed.', 'shape=note;html=1;whiteSpace=wrap;fillColor=#e7e1dc;strokeColor=#e2ddd9;fontFamily=Segoe UI;fontSize=13;fontColor=#635c57;', 504, 353, 112, 58);
  for (const [i, n] of model.nodes.entries()) {
    const x = i % 2 ? 622 : 50, y = 112 + Math.floor(i / 2) * 165;
    const [bg, fg] = palette[Math.floor(i / 2)];
    vertex(n.id, '', 'rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#fdfbf8;strokeColor=#ece7e3;strokeWidth=1;shadow=1;', x, y, 440, 125);
    vertex(`${n.id}-accent`, '', `rounded=1;arcSize=100;fillColor=${fg};strokeColor=none;`, 0, 0, 5, 125, n.id);
    vertex(`${n.id}-icon`, '', `shape=${n.shape};${n.shape === 'cylinder3' ? 'size=8;boundedLbl=1;backgroundOutline=1;' : ''}fillColor=${bg};strokeColor=${fg};strokeWidth=1.5;`, 18, 18, 32, 32, n.id);
    text(`${n.id}-title`, n.title, 62, 13, 362, 29, 22, '#272320', true, n.id);
    text(`${n.id}-subtitle`, n.subtitle, 62, 44, 362, 24, 16, '#635c57', false, n.id);
    vertex(`${n.id}-divider`, '', 'fillColor=#ece7e3;strokeColor=none;', 18, 77, 404, 1, n.id);
    text(`${n.id}-detail`, n.detail, 18, 82, 404, 19, 13, '#3f3935', false, n.id);
    vertex(`${n.id}-metadata`, n.meta, 'text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Cascadia Code;fontSize=12;fontColor=#746d68;align=left;spacing=0;', 18, 104, 300, 16, n.id);
    vertex(`${n.id}-badge`, n.badge, `rounded=1;arcSize=100;fillColor=${bg};strokeColor=none;fontFamily=Segoe UI;fontSize=10;fontStyle=1;fontColor=${fg};`, 328, 104, 96, 17, n.id);
  }
  for (const e of model.edges) edge(e.id, e.source, e.target, e.label, e.points, e.anchors, e.returning);
  text('scope', model.scope, 40, 754, 1032, 23, 13, '#635c57');
}
const source = `<?xml version="1.0" encoding="UTF-8"?>\n<mxfile host="Agentweaver" version="31.4.5" compressed="false"><diagram id="${model.name}" name="${xml(model.title)}"><mxGraphModel page="1" pageScale="1" pageWidth="1112" pageHeight="789" background="#efeae7" grid="1" gridSize="10"><root><mxCell id="0"/><mxCell id="1" parent="0"/>${cells.join('\n')}</root></mxGraphModel></diagram></mxfile>\n`;
const basename = `${model.name}-${mode}`;
const sourcePath = path.join(here, `${basename}.drawio`);
const pngPath = path.join(here, `${basename}.png`);
await fs.writeFile(sourcePath, source, { flag: 'wx' });
execFileSync(cli, [`--user-data-dir=${path.join(here, 'renderer/profile')}`, ...drawioExportArgs(sourcePath, pngPath, 'png')], { stdio: 'inherit' });
console.log(JSON.stringify({ source: sourcePath, png: pngPath, renderer: version }));
