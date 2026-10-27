/**
 * Source-only permission inventory for the separately configured P1 setup capability.
 * This is not a principal assignment or an executable database maintenance plan.
 */
export function buildP1ProjectSetupAuthorityPlan() {
  return {
    formatVersion: 1,
    capability: 'projects-config-p1-setup',
    schema: 'projects_config',
    schemaPrivileges: ['USAGE'],
    tables: [
      { table: '__ef_migrations_history', privileges: ['SELECT'] },
      { table: 'tenant_memberships', privileges: ['SELECT'] },
      { table: 'projects', privileges: ['SELECT', 'INSERT'] },
      { table: 'project_configuration_revisions', privileges: ['SELECT', 'INSERT'] },
      { table: 'project_role_assignments', privileges: ['SELECT', 'INSERT'] },
      { table: 'authority_audit', privileges: ['SELECT', 'INSERT'] },
      { table: 'project_setup_operations', privileges: ['SELECT', 'INSERT'] },
    ],
    functions: [{
      signature: 'lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
      privileges: ['EXECUTE'],
    }],
    deploymentPrerequisites: [
      'Resolve and separately approve an existing authority PostgreSQL role and workload identity; no identity is inferred by this inventory.',
      'The authority role and workload identity must differ from runtime and migration identities.',
      'Approve the authority workload federation and these exact database permissions separately; this source inventory applies nothing.',
      'Retain immutable setup/audit/configuration guards and verify the installed authority-lock function before assigning EXECUTE.',
      'Do not grant runtime access to project_setup_operations or add default, sequence, membership-write, UPDATE, DELETE, TRUNCATE, or schema-CREATE privileges.',
      'This inventory permits setup only, not the standalone command cleanup path or invocation/resource authorization.',
    ],
  };
}
