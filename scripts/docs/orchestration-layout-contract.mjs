import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const contractPath = path.join(
  __dirname,
  '..',
  '..',
  'apps',
  'web',
  'src',
  'utils',
  'orchestration-layout.contract.json',
);

export async function loadOrchestrationLayoutContract() {
  const contract = JSON.parse(await readFile(contractPath, 'utf8'));
  if (
    contract.schemaVersion !== 1 ||
    contract.id !== 'agentweaver.orchestration-layout.v1' ||
    contract.ranking !== 'stable-longest-path' ||
    contract.cycleHandling !== 'exclude-back-edges' ||
    contract.edgeRouting !== 'orthogonal' ||
    !Number.isInteger(contract.serpentineMinRanks) ||
    contract.serpentineMinRanks < 2 ||
    !Number.isInteger(contract.workflowDefinitionLongLinearMinRanks) ||
    contract.workflowDefinitionLongLinearMinRanks < contract.serpentineMinRanks
  ) {
    throw new Error(`Invalid orchestration layout contract: ${contractPath}`);
  }
  return contract;
}

export function toDiagramLayoutContract(contract) {
  return {
    contract: contract.id,
    schemaVersion: contract.schemaVersion,
    ranking: contract.ranking,
    cycleHandling: contract.cycleHandling,
    edgeRouting: contract.edgeRouting,
    serpentineMinRanks: contract.serpentineMinRanks,
  };
}
