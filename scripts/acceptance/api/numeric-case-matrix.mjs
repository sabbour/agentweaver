import { readFileSync } from 'node:fs';

const matrix = JSON.parse(readFileSync(new URL('./numeric-cases.json', import.meta.url), 'utf8'));
const validStatuses = new Set(['passed', 'blocked', 'not-run']);

export function buildNumericMatrix(statuses = {}, options = {}) {
  const sourceCaseId = options?.sourceCaseId ?? null;
  const rows = matrix.requiredRows.map(row => {
    const dedicatedBlocked = row.dedicatedOnly && sourceCaseId !== row.caseId;
    const status = row.fixedStatus ?? (dedicatedBlocked ? 'blocked' : statuses[row.id] ?? row.initialStatus ?? 'not-run');
    const reason = row.fixedStatus === 'blocked' || dedicatedBlocked
      ? row.reason
      : status === 'blocked'
        ? statuses[`${row.id}:reason`] ?? row.reason ?? 'The required evidence is missing.'
        : row.initialStatus === 'blocked' ? row.reason : null;
    return {
      id: row.id,
      caseId: row.caseId,
      predicate: row.predicate,
      status,
      reason,
    };
  });
  return validateNumericMatrix(rows, options);
}

export function validateNumericMatrix(rows, options = {}) {
  const sourceCaseId = options?.sourceCaseId ?? null;
  if (!Array.isArray(rows) || rows.length !== matrix.requiredRows.length) {
    throw new Error('The numeric case matrix is missing required rows.');
  }
  const expected = new Set(matrix.requiredRows.map(row => row.id));
  const seen = new Set();
  for (const row of rows) {
    if (!expected.has(row?.id) || seen.has(row.id)) {
      throw new Error('The numeric case matrix has an unknown or duplicate row.');
    }
    if (!validStatuses.has(row.status)) {
      throw new Error(`The numeric case matrix row ${row.id} has an invalid status.`);
    }
    const required = matrix.requiredRows.find(item => item.id === row.id);
    if (required.fixedStatus && row.status !== required.fixedStatus) {
      throw new Error(`The numeric case matrix row ${row.id} cannot claim ${row.status}.`);
    }
    if (required.dedicatedOnly && row.status === 'passed' && sourceCaseId !== required.caseId) {
      throw new Error(`The numeric case matrix row ${row.id} cannot claim ${row.status}.`);
    }
    seen.add(row.id);
  }
  if (seen.size !== expected.size) throw new Error('The numeric case matrix is missing required rows.');
  return {
    status: rows.every(row => row.status === 'passed') ? 'complete' : 'blocked',
    complete: rows.every(row => row.status === 'passed'),
    rows,
  };
}

export function compareBoundary(value, configuredValue) {
  if (!isFiniteMetric(value) || !isFiniteMetric(configuredValue)) return null;
  if (value === configuredValue) return 'equal';
  return value > configuredValue ? 'overshoot' : 'below';
}

function isFiniteMetric(value) {
  return Number.isFinite(value) && Math.abs(value) <= Number.MAX_SAFE_INTEGER;
}

export function warningFloorTransition(guards, threshold) {
  if (!Array.isArray(guards) || !isFiniteMetric(threshold)) return false;
  const ordered = [...guards].sort((left, right) => left.sequence - right.sequence);
  if (ordered.some((guard, index) => !Number.isSafeInteger(guard.sequence)
    || !isFiniteMetric(guard.observedValue)
    || !Number.isSafeInteger(guard.accountedThroughLedgerEventCount)
    || !Number.isSafeInteger(guard.ownerRevision)
    || (index > 0 && guard.sequence <= ordered[index - 1].sequence))) return false;
  for (let warningIndex = 0; warningIndex < ordered.length; warningIndex += 1) {
    const warning = ordered[warningIndex];
    if (warning.softWarningEmitted !== true
      || warning.observedValue < threshold
      || warning.unit !== 'nanoAiu'
      || warning.scope !== 'copilotRun'
      || !ordered.slice(0, warningIndex).some(before => before.softWarningEmitted === false
        && before.observedValue < threshold
        && before.unit === 'nanoAiu'
        && before.scope === 'copilotRun')) continue;
    for (const floor of ordered.slice(warningIndex + 1)) {
      if (floor.unit === 'nanoAiu'
        && floor.scope === 'copilotRun'
        && floor.observedValue > warning.observedValue
        && floor.accountedThroughLedgerEventCount > warning.accountedThroughLedgerEventCount
        && floor.ownerRevision > warning.ownerRevision) return true;
    }
  }
  return false;
}
