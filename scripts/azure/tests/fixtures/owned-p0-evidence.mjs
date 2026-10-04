export const nrmsNsgName = 'NRMS-gxlttooqhupscaw-v1-p0-vnet';

const sharedRuleFields = {
  direction: 'Inbound',
  sourcePortRange: '*',
  sourcePortRanges: [],
  sourceAddressPrefixes: [],
  destinationAddressPrefix: '*',
  destinationAddressPrefixes: [],
  sourceApplicationSecurityGroups: [],
  destinationApplicationSecurityGroups: [],
};

export const nrmsRules = [
  { name: 'NRMS-Rule-101', priority: 101, access: 'Allow', protocol: 'Tcp',
    sourceAddressPrefix: 'VirtualNetwork', destinationPortRange: '443', destinationPortRanges: [] },
  { name: 'NRMS-Rule-103', priority: 103, access: 'Allow', protocol: '*',
    sourceAddressPrefix: 'CorpNetPublic', destinationPortRange: '*', destinationPortRanges: [] },
  { name: 'NRMS-Rule-104', priority: 104, access: 'Allow', protocol: '*',
    sourceAddressPrefix: 'CorpNetSaw', destinationPortRange: '*', destinationPortRanges: [] },
  { name: 'NRMS-Rule-105', priority: 105, access: 'Deny', protocol: '*', sourceAddressPrefix: 'Internet',
    destinationPortRange: null,
    destinationPortRanges: ['1433', '1434', '3306', '4333', '5432', '6379', '7000', '7001', '7199',
      '9042', '9160', '9300', '16379', '26379', '27017'] },
  { name: 'NRMS-Rule-106', priority: 106, access: 'Deny', protocol: 'Tcp', sourceAddressPrefix: 'Internet',
    destinationPortRange: null, destinationPortRanges: ['22', '3389'] },
  { name: 'NRMS-Rule-107', priority: 107, access: 'Deny', protocol: 'Tcp', sourceAddressPrefix: 'Internet',
    destinationPortRange: null, destinationPortRanges: ['23', '135', '445', '5985', '5986'] },
  { name: 'NRMS-Rule-108', priority: 108, access: 'Deny', protocol: '*', sourceAddressPrefix: 'Internet',
    destinationPortRange: null,
    destinationPortRanges: ['13', '17', '19', '53', '69', '111', '123', '512', '514', '593', '873',
      '1900', '5353', '11211'] },
  { name: 'NRMS-Rule-109', priority: 109, access: 'Deny', protocol: '*', sourceAddressPrefix: 'Internet',
    destinationPortRange: null,
    destinationPortRanges: ['119', '137', '138', '139', '161', '162', '389', '636', '2049', '2301',
      '2381', '3268', '5800', '5900'] },
].map(rule => {
  const { name, ...properties } = rule;
  return { name, properties: { ...sharedRuleFields, ...properties } };
});

export const nrmsPolicyAssignments = [
  [101, '9d78e6174e6e69be', 'nrms-nsg-dine-sr101-v013'],
  [103, '3c07197392ad62f', 'nrms-nsg-dine-sr103-v014'],
  [104, 'bac0fb65020410a4', 'nrms-nsg-dine-sr104-v013'],
  [105, '91f42c0ca66ff7dd', 'nrms-nsg-dine-sr105-v013'],
  [106, '9b8d76c443040b08', 'nrms-nsg-dine-sr106-v013'],
  [107, 'fb6de85c9e746cf1', 'nrms-nsg-dine-sr107-v013'],
  [108, '532396f35af78946', 'nrms-nsg-dine-sr108-v013'],
  [109, 'e0bc08af3bd773ff', 'nrms-nsg-dine-sr109-v013'],
];

export const policyManagementGroupId =
  '/providers/Microsoft.Management/managementGroups/48fed3a1-0814-4847-88ce-b766155f2792';

export const smartDetectorEvidence = {
  name: 'Failure Anomalies - aw-v1-p0-appi',
  type: 'Microsoft.AlertsManagement/smartDetectorAlertRules',
  state: 'Enabled',
  severity: 'Sev3',
  frequency: 'PT1M',
  detectorId: 'FailureAnomaliesDetector',
  detectorName: 'Failure Anomalies',
  throttling: null,
  customEmailSubject: null,
  customWebhookPayload: null,
};
