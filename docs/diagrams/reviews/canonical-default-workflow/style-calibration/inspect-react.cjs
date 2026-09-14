const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..', '..', '..', '..', '..');
const appRequire = createRequire(path.join(root, 'apps', 'web', 'package.json'));
const ts = appRequire('typescript');
const cache = new Map();
function load(name) {
  if (cache.has(name)) return cache.get(name);
  const filename = path.join(__dirname, 'react-source', name);
  const source = fs.readFileSync(filename, 'utf8');
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText;
  const module = { exports: {} };
  const resolve = (id) => {
    if (id.endsWith('.css')) return {};
    if (id.startsWith('./')) {
      const basename = id.slice(2);
      const extension = fs.existsSync(path.join(__dirname, 'react-source', basename + '.tsx')) ? '.tsx' : '.ts';
      return load(basename + extension);
    }
    return appRequire(id);
  };
  new Function('require', 'module', 'exports', compiled)(resolve, module, module.exports);
  cache.set(name, module.exports);
  return module.exports;
}
const layout = load('DiagramCanvas.tsx').layout;
const spec = JSON.parse(fs.readFileSync(path.join(__dirname, 'original-graph.json'), 'utf8'));
const result = layout(spec);
fs.writeFileSync(path.join(__dirname, 'source-layout.json'), JSON.stringify(result, null, 2));
console.log('Current recovered source layout:', result.canvasWidth, result.canvasHeight);
console.log(result.nodes.map(n => ({ id: n.id, position: n.position })));
const React = appRequire('react');
const { renderToStaticMarkup } = appRequire('react-dom/server');
const icons = appRequire('@fluentui/react-icons');
for (const [name, color] of [['BotRegular', '#0e700e'], ['WindowRegular', '#00666d']]) {
  const markup = renderToStaticMarkup(React.createElement(icons[name], {
    fontSize: 38, primaryFill: color, style: { color },
  })).replace('width="1em"', 'width="38"').replace('height="1em"', 'height="38"');
  fs.writeFileSync(path.join(__dirname, name + '.svg'), markup);
}
const iconPackage = JSON.parse(fs.readFileSync(
  path.resolve(path.dirname(appRequire.resolve('@fluentui/react-icons')), '..', 'package.json'), 'utf8'));
fs.writeFileSync(path.join(__dirname, 'fluent-icon-provenance.json'), JSON.stringify({
  package: iconPackage.name, version: iconPackage.version, license: iconPackage.license,
  repository: iconPackage.repository, icons: ['BotRegular', 'WindowRegular'],
  note: 'SVG primitives rendered from the installed package; it ships no standalone LICENSE file.',
}, null, 2));
