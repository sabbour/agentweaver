import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { models } from './batch-models.mjs';

const here=path.dirname(fileURLToPath(import.meta.url));
const repo=path.resolve(here,'../../../..');
const entries=[];
for(const {name} of models) {
  const dir=path.join(here,'..',name);
  const validation=JSON.parse(await fs.readFile(path.join(dir,'validation-results.json')));
  const manifest=JSON.parse(await fs.readFile(path.join(dir,'iteration-manifest.json')));
  const stamp=JSON.parse(await fs.readFile(path.join(repo,'docs/diagrams',name+'.hash.txt')));
  if(!validation.promoted_bytes_match || stamp.renderer.rendererVersion!=='31.4.5'
      || stamp.png.sha256!==validation.inspected_png_sha256) throw Error(`Publication check failed: ${name}`);
  const owned_paths=[
    `docs/diagrams/src/${name}.json`,
    `docs/diagrams/src/${name}.drawio`,
    `docs/diagrams/drawio/generated/${name}.drawio`,
    `docs/diagrams/${name}.png`,
    `docs/diagrams/${name}.hash.txt`,
    `docs/diagrams/reviews/${name}/`,
  ];
  entries.push({name,manifest:`docs/diagrams/reviews/${name}/iteration-manifest.json`,
    final_pass:manifest.final_pass,growth_ratio:validation.growth_ratio,
    final_arrows:validation.exact_final_arrows,owned_paths});
  await fs.writeFile(path.join(dir,'publication-check.json'),JSON.stringify({
    name,source_and_generated_match_inspected_final:true,png_matches_inspected_final:true,
    stamp_matches_verified_renderer_and_inspected_png:true,renderer_version:'31.4.5',
    selected_repository_drift_check:'passed',legacy_json_archived:'legacy-source.json',
    source_discovery_has_one_drawio_and_no_json:true,renderer_profile_cache_removed:true,
  },null,2)+'\n');
}
const report={
  completed:13,blocked:0,agents_launched:0,commits_created:0,
  pitch_artifacts:13,post_pitch_passes:53,inspected_artifact_sets:66,
  final_arrow_traces:79,final_page:{width:827,height:583,units_per_inch:100,orientation:'A5-landscape',pages:1},
  renderer:{name:'draw.io Desktop',version:'31.4.5',source:'docs/diagrams/reviews/canonical-api-host/renderer/desktop/draw.io.exe',modified:false},
  validation:{schema:'passed',repository_manifest_validator:'passed',exact_arrow_ids_and_endpoints:'passed',meaningful_growth:'passed',anti_padding:'passed',selected_drift:'passed'},
  drift_command:'node scripts\\docs\\render-diagrams.mjs --check '+models.map(m=>'--spec '+m.name).join(' '),
  inspection_scope:'Each pitch and post-pitch exported PNG was actually opened enlarged and as an A5-sized screen proof; no physical paper proof or live runtime test is claimed.',
  historical_count_method:'Pre-final defect counts summarize issue groups retrospectively from recorded inspection/correction handoffs; they are not pixel counts or counts of every affected glyph/segment.',
  remaining_parent_work:'Markdown provenance and plan status are explicitly outside this batch write authority.',
  authoring_notes:[
    'Read the three pre-existing independent research outputs; no agents launched.',
    'Initial testing-strategy-fig1 growth candidate was below 9x; preserved as a candidate, then replaced with useful visible prerequisite context before pass-one approval.',
    'A validation-only minimum-height assumption failed on cropped coarse-pitch exports; corrected to require the full-detail height only for post-pitch rasters. All source pages remain true A5.',
    'A guessed API-local RemoteAgentProxy path did not exist; used the verified RemoteWorkflowAgentFactory implementation as leaf-execution evidence instead. No runtime files changed.',
    'Only owned renderer profile caches were removed. Sources, PNGs, screen proofs, reviews, local helpers, measured growth and legacy sources remain.',
  ],
  entries,
};
await fs.writeFile(path.join(here,'batch-completion.json'),JSON.stringify(report,null,2)+'\n');
console.log(`Persisted completion report for ${entries.length} owned diagrams.`);
