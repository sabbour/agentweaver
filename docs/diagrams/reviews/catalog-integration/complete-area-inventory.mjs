import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInventoryEntries, loadInventory, validateInventoryEntries, writeInventoryArea } from '../../../../scripts/docs/diagram-inventory.mjs';
import { listDiagramSources } from '../../../../scripts/docs/diagram-sources.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..','..','..','..');
const inventoryRoot=path.join(root,'docs','diagrams','drawio','inventory');
const sources=await listDiagramSources(path.join(root,'docs','diagrams','src'));
const previous=await loadInventory(inventoryRoot);
const audit=JSON.parse(await readFile(path.join(root,'docs-diagram-audit.json'),'utf8'));
const byName=new Map(audit.diagrams.map(d=>[d.name,d]));
const entries=createInventoryEntries(sources,previous.entries);
for(const entry of entries) {
  const completed=byName.get(entry.name);
  if(!completed||completed.status!=='done')throw new Error(`${entry.name}: not reconciled`);
  entry.disposition=completed.disposition;
  entry.replacement=completed.target;
  entry.owner='gpt-6-astra-sole-agent';
  entry.notes='Completed Fluent catalog migration. Stable canonical paths; reviewed export and iteration evidence in docs/diagrams/reviews/catalog-integration/completion-reconciliation.json.';
}
validateInventoryEntries(entries,sources);
for(const area of previous.index.areas)
  await writeInventoryArea(inventoryRoot,area,entries.filter(e=>e.area===area));
console.log(`Reconciled ${entries.length} source-backed entries across ${previous.index.areas.length} inventory shards.`);
