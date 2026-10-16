import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export function inspectFluentSource(contents, { execute=spawnSync }={}) {
  const result=execute(process.env.PYTHON??'python',
    ['-B',fileURLToPath(new URL('./normalize-drawio.py',import.meta.url)),'-','--json'],
    {input:contents,encoding:'utf8',maxBuffer:16*1024*1024});
  if(result.error) throw new Error(`Python 3 is required for diagram style validation: ${result.error.message}`);
  if(![0,1].includes(result.status)) throw new Error(`Diagram XML validation failed: ${result.stderr||result.stdout}`);
  return JSON.parse(result.stdout);
}

export function requireFluentSource(contents, name) {
  const result=inspectFluentSource(contents);
  if(result.status!=='passed') {
    const codes=[...new Set(result.errors.map(e=>e.code))].join(', ');
    throw new Error(`${name}: Fluent publication gate blocked (${result.uncertainty.length} unresolved roles; ${codes||'semantic bindings required'}). Run the repair planner; no public artifact may be overwritten.`);
  }
  return result;
}
