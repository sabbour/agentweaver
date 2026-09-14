import { writeFile } from 'node:fs/promises';
import { graphSpecToDrawio, DESIGN_TOKENS } from './drawio-generator.mjs';

const samples = [
  ['Fluent card', {icon:'bot'}],
  ['Azure resource', {library:'azure'}],
  ['Kubernetes resource', {library:'kubernetes'}],
  ['Durable store', {library:'database'}],
];
const entries = samples.map(([title,fields]) => ({
  title,
  w:DESIGN_TOKENS.geometry.cardWidth,
  h:DESIGN_TOKENS.geometry.cardMinHeight,
  aspect:'fixed',
  xml:graphSpecToDrawio({title,nodes:[{id:'component',label:title,...fields,badge:{tone:'teal',text:'Service'}}],edges:[]},{name:'fluent-template'}),
}));
const escape = text => text.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
await writeFile(new URL('../../docs/diagrams/drawio/fluent-library.xml',import.meta.url),`<mxlibrary>${escape(JSON.stringify(entries))}</mxlibrary>\n`);
await writeFile(new URL('../../docs/diagrams/drawio/fluent-template.drawio',import.meta.url),entries[0].xml);
console.log('Regenerated Fluent library/template from the shared manifest and generator.');
