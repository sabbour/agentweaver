import assert from 'node:assert/strict';
import test from 'node:test';
import { inspectFlagship } from './validate-flagship-diagrams.mjs';

function png(width, height) {
  const buffer = Buffer.alloc(24);
  buffer.write('PNG', 1, 'ascii');
  buffer.writeUInt32BE(width, 16);
  buffer.writeUInt32BE(height, 20);
  return buffer;
}

test('accepts a readable flagship diagram', () => {
  const xml = '<mxCell id="fluent-paper"><mxGeometry width="1400" height="900"/></mxCell>'
    + '<mxCell fluentRole="card"/><mxCell fluentRole="connector"/>';
  assert.deepEqual(inspectFlagship('good', xml, png(2800, 1800)).issues, []);
});

test('rejects extreme, dense, and embed-unreadable diagrams', () => {
  const xml = '<mxCell id="fluent-paper"><mxGeometry width="2200" height="600"/></mxCell>'
    + '<mxCell fluentRole="card"/>'.repeat(17)
    + '<mxCell fluentRole="connector"/>'.repeat(25);
  const issues = inspectFlagship('bad', xml, png(4400, 1200)).issues;
  assert.ok(issues.some(issue => issue.startsWith('extreme aspect ratio')));
  assert.ok(issues.some(issue => issue.includes('17 cards')));
  assert.ok(issues.some(issue => issue.includes('25 connectors')));
  assert.ok(issues.some(issue => issue.includes('title type scales')));
});
