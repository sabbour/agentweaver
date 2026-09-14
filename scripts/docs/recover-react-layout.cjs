// Authoring helper: recover pure deterministic geometry, never restore a React runtime.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..', '..');
const requireWeb = createRequire(path.join(root, 'apps', 'web', 'package.json'));
const ts = requireWeb('typescript');
const revision = 'a46721ae77efeba505d2fbfa58c69cdae8b4f3eb';
function recover(name, cut) {
  let source = execFileSync('git', ['show', `${revision}:docs/diagram-renderer/src/${name}`], { cwd: root, encoding: 'utf8' });
  source = source.slice(0, source.indexOf(cut));
  const ast = ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  source = ast.statements.filter(statement => !ts.isImportDeclaration(statement))
    .map(statement => statement.getText(ast)).join('\n');
  source = source.replace(/^const nodeTypes = .*;$/m, '').replace(/^const edgeTypes = .*;$/m, '');
  return ts.transpileModule(source, { compilerOptions: {
    target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022, removeComments: false,
  } }).outputText;
}
const header = `// Pure geometry recovered from Agentweaver React renderer at ${revision}.\n`;
const edges=recover('edges.tsx', 'export function RoutedEdge(')
  .replace('const CORNER_RADIUS = 10;','const CORNER_RADIUS = DESIGN_SYSTEM.connectors.cornerRadiusPx;');
fs.writeFileSync(path.join(__dirname, 'fluent-edge-geometry.mjs'), header +
  "import { DESIGN_SYSTEM } from './fluent-tokens.mjs';\n" + edges);
let geometry = recover('DiagramCanvas.tsx', 'export interface DiagramCanvasProps');
geometry = geometry.replace('const LABEL_CHAR_W = 10.2;', 'const LABEL_CHAR_W = DESIGN_TOKENS.typography.edgeSize * 0.567;')
  .replace('const LABEL_LINE_H = 20.7;', 'const LABEL_LINE_H = DESIGN_TOKENS.typography.edgeSize * 1.15;')
  .replace('const GROUP_PAD_TOP = 96;', 'const GROUP_PAD_TOP = DESIGN_TOKENS.geometry.groupPaddingTop;');
for(const [original,key] of [
  ['COL_GAP = 56','columnGap'],['ROW_GAP = 62','rowGap'],['GROUP_PAD_SIDE = 48','groupPaddingX'],
  ['GROUP_PAD_BOTTOM = 44','groupPaddingBottom'],['CANVAS_MARGIN = 80','canvasMargin'],
]) geometry=geometry.replace(`const ${original};`,`const ${original.split(' = ')[0]} = DESIGN_TOKENS.geometry.${key};`);
fs.writeFileSync(path.join(__dirname, 'fluent-graph-layout.mjs'), header + `
import { DESIGN_TOKENS, cardContentMetrics } from './fluent-tokens.mjs';
import { alignLoopbackToContinuation, findConnectorBridges, findConnectorJunctions } from './fluent-edge-geometry.mjs';
const CARD_WIDTH = DESIGN_TOKENS.geometry.cardWidth;
const CARD_HEIGHT_2 = DESIGN_TOKENS.geometry.cardMinHeight;
const cardHeightFor = node => cardContentMetrics(node).height;
const MarkerType = { ArrowClosed: 'arrowclosed' };
const neutral = {
  foreground4: DESIGN_TOKENS.colors.inkFaint, background1: DESIGN_TOKENS.colors.surface,
  background2: DESIGN_TOKENS.colors.group1, background3: DESIGN_TOKENS.colors.group2,
};
const badgeTones = Object.fromEntries(Object.entries(DESIGN_TOKENS.badges).map(([key,value])=>[key,{bg:value.background,fg:value.foreground}]));
const radius = { card: DESIGN_TOKENS.geometry.cardRadius + 'px' };
` + geometry);
const React = requireWeb('react');
const { renderToStaticMarkup } = requireWeb('react-dom/server');
const icons = requireWeb('@fluentui/react-icons');
const names = { globe:'GlobeRegular',branch:'BranchRegular',route:'ArrowRoutingRegular',window:'WindowRegular',
  server:'ServerRegular',bot:'BotRegular',database:'DatabaseRegular',key:'KeyRegular',box:'BoxRegular' };
const directory = path.join(root, 'docs', 'diagrams', 'drawio', 'icons');
fs.mkdirSync(directory, { recursive: true });
for (const [key, name] of Object.entries(names)) {
  const svg = renderToStaticMarkup(React.createElement(icons[name], { primaryFill: 'currentColor' }))
    .replace('width="1em"', 'width="28"').replace('height="1em"', 'height="28"');
  fs.writeFileSync(path.join(directory, `${key}.svg`), svg);
}
console.log('Recovered pure graph/connector geometry and nine Fluent Regular icons.');
