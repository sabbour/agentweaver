import fs from 'node:fs/promises';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../../..');
const reportPath = '.github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json';
const plan = JSON.parse(await fs.readFile(path.join(repo, reportPath)));
const audit = JSON.parse(await fs.readFile(path.join(repo, '.github/skills/docs-diagram-audit/reports/deep-dive-core.json')));
const validation = JSON.parse(await fs.readFile(path.join(here, 'shard-validation.json')));
const mechanicallyValid = new Set(validation.diagrams.map(d => d.diagram));
const concepts = [];
for (const document of audit.documents) {
  if (!plan.document_paths.includes(document.path)) continue;
  for (const concept of document.concepts) {
    if (!plan.concept_ids.includes(concept.id)) continue;
    const result = {
      id: concept.id, document: document.path, planned_disposition: concept.disposition,
      planned_representation: concept.representation,
      canonical_diagram: concept.canonical_diagram,
      documentation: 'updated-or-preserved-against-reconciled-ground-truth',
    };
    if (concept.canonical_diagram === 'canonical-aks-components' ||
        concept.canonical_diagram === 'assistant-runtime-fig1') {
      result.status = 'blocked-shared-promotion';
      result.current_representation = 'corrected-prose-without-stale-embed';
    } else if (mechanicallyValid.has(concept.canonical_diagram)) {
      result.status = 'candidate-mechanically-validated-article-approval-pending';
      result.evidence = `docs/diagrams/reviews/${concept.canonical_diagram}/iteration-manifest.json`;
    } else if (concept.representation === 'mermaid') {
      result.status = 'replaced-with-table';
      result.current_representation = 'table';
    } else if (concept.canonical_diagram) {
      result.status = 'shared-reference-preserved-or-migrated';
      result.note = 'Shared content remains its owner responsibility; this is not independent publication approval.';
    } else {
      result.status = 'documentation-disposition-applied';
    }
    concepts.push(result);
  }
}
const found = new Set(concepts.map(c => c.id));
const missing = plan.concept_ids.filter(id => !found.has(id));
if (missing.length) throw new Error(`Unaccounted planned concepts: ${missing.join(', ')}`);
await fs.writeFile(path.join(here, 'disposition-results.json'), JSON.stringify({
  area: plan.area, overall_status: 'blocked', concept_count: concepts.length, concepts,
  retirements: [
    { diagram: 'api-core-fig7', status: 'retired', consumer: 'canonical host prose link' },
    { diagram: '00-system-overview-fig8', status: 'retired', consumer: 'approved shared durable-event sequence' },
    { diagram: '00-system-overview-fig7', status: 'retired-with-replacement-embed-blocked',
      consumer: 'corrected prose and stable shared AKS target link' },
  ],
}, null, 2) + '\n');

const tracked = execFileSync('git', ['--no-pager', 'diff', '--name-only', 'HEAD'], { cwd: repo, encoding: 'utf8' });
const untracked = execFileSync('git', ['ls-files', '--others', '--exclude-standard'], { cwd: repo, encoding: 'utf8' });
const reviewPrefixes = plan.diagram_names.map(name => `docs/diagrams/reviews/${name}/`);
const allowed = file => file === reportPath || plan.document_paths.includes(file) ||
  plan.exclusive_asset_paths.includes(file) || reviewPrefixes.some(prefix => file.startsWith(prefix));
const dirty = [...new Set((tracked + '\n' + untracked).split(/\r?\n/).filter(Boolean))];
await fs.writeFile(path.join(here, 'changed-paths.json'), JSON.stringify({
  note: 'Current dirty paths inside this assignment only; the shared dirty worktree prevents attributing pre-existing edits to this session.',
  paths: dirty.filter(allowed).sort(),
  document_paths: plan.document_paths,
  candidate_diagram_names: [...mechanicallyValid],
  retired_diagram_names: validation.retired,
  excluded_concurrent_paths_count: dirty.filter(file => !allowed(file)).length,
}, null, 2) + '\n');
console.log(`Accounted for ${concepts.length}/${plan.concept_ids.length} owned concepts; saved scoped changed paths.`);
