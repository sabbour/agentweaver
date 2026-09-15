import assert from 'node:assert/strict';
import test from 'node:test';
import { applyIconCloudFluentIcons } from './apply-iconcloud-fluent-icons.mjs';

test('uses semantic Fluent icons while preserving Kubernetes symbols', () => {
  const xml = [
    '<mxCell id="node-memory-icon" fluentRole="icon" value="" style="shape=process;strokeColor=#007a78;" parent="node-memory" vertex="1"><mxGeometry/></mxCell>',
    '<mxCell id="node-memory-title" fluentRole="title" value="Project memory" style="text;" parent="node-memory" vertex="1"><mxGeometry/></mxCell>',
    '<mxCell id="node-pod-icon" fluentRole="icon" value="" style="shape=mxgraph.kubernetes.icon2;prIcon=pod;strokeColor=#107c10;" parent="node-pod" vertex="1"><mxGeometry/></mxCell>',
    '<mxCell id="node-pod-title" fluentRole="title" value="Agent pod" style="text;" parent="node-pod" vertex="1"><mxGeometry/></mxCell>',
  ].join('\n');
  const result = applyIconCloudFluentIcons(xml);
  assert.equal(result.replaced, 1);
  assert.equal(result.preservedKubernetes, 1);
  assert.match(result.xml, /data-iconcloud-source%3D%22https%3A%2F%2Ficoncloud\.design/);
  assert.match(result.xml, /mxgraph\.kubernetes\.icon2/);
});
