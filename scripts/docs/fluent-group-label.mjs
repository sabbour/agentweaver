import { DESIGN_TOKENS } from './fluent-tokens.mjs';

export function groupCaptionMetrics(label, availableWidth) {
  const size=DESIGN_TOKENS.typography.titleSize;
  const limit=Math.max(8,Math.min(28,Math.floor(availableWidth/(size*.72))));
  const lines=[''];
  for(const word of label.split(/\s+/)) {
    if(lines.at(-1)&&lines.at(-1).length+word.length+1>limit)lines.push('');
    lines[lines.length-1]+=(lines.at(-1)?' ':'')+word;
  }
  return {text:lines.join('\n'),width:Math.max(...lines.map(s=>s.length))*size*.72+4,
    height:lines.length*size*1.15};
}
