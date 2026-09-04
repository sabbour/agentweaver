import { createRoot } from 'react-dom/client';
import { DiagramCanvas } from './DiagramCanvas';
import { SequenceCanvas } from './SequenceCanvas';
import type { DiagramSpec } from './types';

// The capture script (scripts/docs/capture-diagrams.mjs) copies each
// docs/diagrams/src/<name>.json into public/specs/<name>.json before
// building this app, then navigates here with `?spec=<name>`. Keeping specs
// under public/ (rather than importing across the Vite project root) avoids
// fiddling with Vite's fs.allow restrictions for a path outside this app.
const params = new URLSearchParams(window.location.search);
const specName = params.get('spec');

async function main() {
  const root = createRoot(document.getElementById('root')!);
  if (!specName) {
    root.render(<div>Missing ?spec=&lt;name&gt; query param</div>);
    return;
  }
  const res = await fetch(`./specs/${specName}.json`);
  if (!res.ok) {
    root.render(<div>Spec not found: {specName}</div>);
    return;
  }
  const spec = (await res.json()) as DiagramSpec;
  root.render(spec.kind === 'sequence' ? <SequenceCanvas spec={spec} /> : <DiagramCanvas spec={spec} />);
}

main();
